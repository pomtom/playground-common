using System.IO;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Airtricity.Common.Http.Configuration;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Composes the resilience pipeline for a named client.
/// </summary>
/// <remarks>
/// <para>
/// This takes plain arguments rather than the SDK's <c>ResilienceHandlerContext</c> on purpose:
/// that type has only an internal constructor, so a test could never build one. Keeping the
/// composition free of it means the pipeline can be constructed and asserted directly.
/// </para>
/// <para>
/// <c>AddStandardResilienceHandler</c> was not used because it fixes five strategies in one order
/// with no extension point, and this library needs six - a concurrency limiter, configured
/// independently of the rate limiter, between the circuit breaker and the attempt timeout. The
/// standard handler's single limiter slot can express window-based rate limiting or concurrency
/// limiting, but not both. Its option types are still used here, so nothing is lost but the fixed
/// order.
/// </para>
/// </remarks>
internal static class ResiliencePipelineConfigurator
{
    internal const string RateLimiterStrategyName = "airtricity-rate-limiter";
    internal const string TotalTimeoutStrategyName = "airtricity-total-timeout";
    internal const string RetryStrategyName = "airtricity-retry";
    internal const string CircuitBreakerStrategyName = "airtricity-circuit-breaker";
    internal const string ConcurrencyLimiterStrategyName = "airtricity-concurrency-limiter";
    internal const string AttemptTimeoutStrategyName = "airtricity-attempt-timeout";

    /// <summary>
    /// Adds every enabled strategy to <paramref name="builder"/>, outermost first.
    /// </summary>
    /// <param name="builder">Pipeline builder handed over by the resilience handler.</param>
    /// <param name="options">Configuration for this client.</param>
    /// <param name="clientName">Name of the client, used in logs, metrics and the pipeline name.</param>
    /// <param name="timeProvider">Clock every strategy uses. Virtualized in tests.</param>
    /// <param name="logger">Logger for resilience events.</param>
    /// <param name="metrics">Metrics sink.</param>
    /// <param name="circuitBreakerControl">
    /// Optional manual control, so tests can drive circuit transitions without any clock at all.
    /// </param>
    /// <param name="circuitBreakerState">Optional state provider, so tests can observe the circuit.</param>
    internal static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpClientOptions options,
        string clientName,
        TimeProvider timeProvider,
        ILogger logger,
        HttpClientMetrics metrics,
        CircuitBreakerManualControl? circuitBreakerControl = null,
        CircuitBreakerStateProvider? circuitBreakerState = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // Set before anything is added, so every strategy below reads this clock. The setter is
        // public on ResiliencePipelineBuilderBase; this one line is what makes the retry, timeout
        // and circuit-breaker tests deterministic rather than dependent on real delays.
        builder.TimeProvider = timeProvider;
        builder.Name = $"airtricity-http:{clientName}";

        var resilience = options.Resilience;
        if (!resilience.Enabled)
        {
            return;
        }

        AddRateLimiter(builder, options, clientName, logger);
        AddTotalTimeout(builder, resilience, clientName, logger);
        AddRetry(builder, options, clientName, logger, metrics);
        AddCircuitBreaker(builder, resilience, clientName, logger, circuitBreakerControl, circuitBreakerState);
        AddConcurrencyLimiter(builder, options, clientName, logger);
        AddAttemptTimeout(builder, resilience, clientName, logger);
    }

    /// <summary>
    /// Outermost. Shedding happens before any timeout budget is spent and before a permit or a
    /// connection is taken, so a rejected request costs almost nothing.
    /// </summary>
    private static void AddRateLimiter(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpClientOptions options,
        string clientName,
        ILogger logger)
    {
        var rateLimiter = options.RateLimiter;
        if (!rateLimiter.Enabled)
        {
            return;
        }

        // Created once per pipeline and captured. Building one per request would give every
        // request its own fresh window, which is no limit at all.
        var limiter = CreateWindowLimiter(rateLimiter);

        builder.AddRateLimiter(new HttpRateLimiterStrategyOptions
        {
            Name = RateLimiterStrategyName,
            RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
            OnRejected = args =>
            {
                MarkRejection(args.Context, RejectionSource.RateLimiter);
                HttpClientLog.LimiterRejected(logger, clientName, "rate limiter");
                return default;
            },
        });
    }

    /// <summary>
    /// Above retry, so the budget covers every attempt and every backoff wait between them. This
    /// is the number a caller reasons about: the call returns within this time or it fails.
    /// </summary>
    private static void AddTotalTimeout(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpResilienceOptions resilience,
        string clientName,
        ILogger logger) =>
        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = TotalTimeoutStrategyName,
            Timeout = resilience.Timeout.TotalRequestTimeout,
            OnTimeout = args =>
            {
                MarkTimeout(args.Context, TimeoutSource.Total);
                HttpClientLog.TotalTimeout(logger, clientName, args.Timeout);
                return default;
            },
        });

    /// <summary>
    /// Inside the total timeout so backoff is charged against the budget, and outside the circuit
    /// breaker so each attempt consults it and an open circuit cuts the retries short.
    /// </summary>
    private static void AddRetry(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpClientOptions options,
        string clientName,
        ILogger logger,
        HttpClientMetrics metrics)
    {
        var retry = options.Resilience.Retry;
        if (!retry.Enabled || retry.MaxRetryAttempts <= 0)
        {
            return;
        }

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            Name = RetryStrategyName,
            MaxRetryAttempts = retry.MaxRetryAttempts,
            BackoffType = retry.UseExponentialBackoff ? DelayBackoffType.Exponential : DelayBackoffType.Constant,
            UseJitter = retry.UseJitter,
            Delay = retry.Delay,
            MaxDelay = retry.MaxDelay,
            ShouldRetryAfterHeader = retry.RespectRetryAfterHeader,
            ShouldHandle = args => new ValueTask<bool>(
                ShouldRetry(args.Outcome, args.Context, retry, clientName, logger, metrics)),
            OnRetry = args =>
            {
                // The response from the attempt we are abandoning is never read, so nothing else
                // will dispose it. Leaving it undisposed holds its connection out of the pool for
                // the handler's lifetime. Double disposal is harmless, so this is safe even if the
                // SDK's handler also disposes it.
                args.Outcome.Result?.Dispose();

                RefreshRequestBody(args.Context);

                HttpClientLog.Retrying(
                    logger,
                    clientName,
                    MethodOf(args.Context),
                    args.AttemptNumber + 1,
                    args.RetryDelay,
                    DescribeOutcome(args.Outcome),
                    args.Outcome.Exception);

                return default;
            },
        });
    }

    /// <summary>
    /// Inside retry so it sees each attempt's outcome rather than the aggregate, and outside the
    /// concurrency limiter so an open circuit never consumes a permit.
    /// </summary>
    private static void AddCircuitBreaker(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpResilienceOptions resilience,
        string clientName,
        ILogger logger,
        CircuitBreakerManualControl? manualControl,
        CircuitBreakerStateProvider? stateProvider)
    {
        var circuitBreaker = resilience.CircuitBreaker;
        if (!circuitBreaker.Enabled)
        {
            return;
        }

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            Name = CircuitBreakerStrategyName,
            FailureRatio = circuitBreaker.FailureRatio,
            MinimumThroughput = circuitBreaker.MinimumThroughput,
            SamplingDuration = circuitBreaker.SamplingDuration,
            BreakDuration = circuitBreaker.BreakDuration,
            ManualControl = manualControl,
            StateProvider = stateProvider,
            // These callbacks must not dispose args.Outcome.Result. Unlike a retry, the circuit
            // breaker does not abandon the outcome that triggered a transition - it still flows
            // back to the caller, who reads its body. Disposing it here produced an
            // ObjectDisposedException on the request that closed the circuit.
            OnOpened = args =>
            {
                HttpClientLog.CircuitOpened(logger, clientName, args.BreakDuration);
                return default;
            },
            OnClosed = args =>
            {
                HttpClientLog.CircuitClosed(logger, clientName);
                return default;
            },
            OnHalfOpened = _ =>
            {
                HttpClientLog.CircuitHalfOpened(logger, clientName);
                return default;
            },
        });
    }

    /// <summary>
    /// Inside the breaker, so requests queued behind a saturated client are not counted as
    /// failures, and outside the attempt timeout, so waiting for a permit is not charged against a
    /// single attempt's budget.
    /// </summary>
    private static void AddConcurrencyLimiter(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpClientOptions options,
        string clientName,
        ILogger logger)
    {
        var concurrency = options.RateLimiter.Concurrency;
        if (!concurrency.Enabled)
        {
            return;
        }

        builder.AddRateLimiter(new HttpRateLimiterStrategyOptions
        {
            Name = ConcurrencyLimiterStrategyName,
            DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
            {
                PermitLimit = concurrency.PermitLimit,
                QueueLimit = concurrency.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            },
            OnRejected = args =>
            {
                MarkRejection(args.Context, RejectionSource.ConcurrencyLimiter);
                HttpClientLog.LimiterRejected(logger, clientName, "concurrency limiter");
                return default;
            },
        });
    }

    /// <summary>
    /// Innermost. Bounds one network attempt, so a hung connection becomes a retryable failure
    /// rather than consuming the whole total budget.
    /// </summary>
    private static void AddAttemptTimeout(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HttpResilienceOptions resilience,
        string clientName,
        ILogger logger)
    {
        if (!resilience.Timeout.EnableAttemptTimeout)
        {
            return;
        }

        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = AttemptTimeoutStrategyName,
            Timeout = resilience.Timeout.AttemptTimeout,
            OnTimeout = args =>
            {
                MarkTimeout(args.Context, TimeoutSource.Attempt);
                HttpClientLog.AttemptTimeout(logger, clientName, args.Timeout);
                return default;
            },
        });
    }

    /// <summary>
    /// Decides whether an outcome is worth retrying. Written out rather than delegated to
    /// <c>DisableForUnsafeHttpMethods</c> because that helper also excludes PUT and DELETE, which
    /// RFC 9110 defines as idempotent, and it offers no per-request override.
    /// </summary>
    internal static bool ShouldRetry(
        Outcome<HttpResponseMessage> outcome,
        ResilienceContext context,
        HttpRetryOptions retry,
        string clientName,
        ILogger logger,
        HttpClientMetrics metrics)
    {
        // First, and unconditionally: the caller asking to stop is not a failure to retry.
        if (context.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // Retrying a request our own limiter shed defeats the shedding; retrying an open circuit
        // defeats the breaker.
        if (outcome.Exception is RateLimiterRejectedException or BrokenCircuitException)
        {
            return false;
        }

        // The total timeout is terminal. Only a per-attempt timeout is worth another attempt.
        if (outcome.Exception is TimeoutRejectedException)
        {
            return TimeoutSourceOf(context) != TimeoutSource.Total && IsRetryAllowed(
                context, retry, clientName, logger, metrics);
        }

        var isTransient = outcome.Exception switch
        {
            HttpRequestException or IOException or SocketException => true,
            null => IsRetryableStatus(outcome.Result, retry),
            _ => false,
        };

        return isTransient && IsRetryAllowed(context, retry, clientName, logger, metrics);
    }

    private static bool IsRetryableStatus(HttpResponseMessage? response, HttpRetryOptions retry)
    {
        if (response is null)
        {
            return false;
        }

        var status = (int)response.StatusCode;

        // 408 and 429 are retryable regardless of the server-error setting: the first says the
        // server gave up waiting, the second says to come back later. Neither implies the request
        // was applied.
        if (status is 408 or 429)
        {
            return true;
        }

        return retry.RetryServerErrors && status >= 500 && status <= 599;
    }

    /// <summary>
    /// Applies the idempotency safeguard. Fails closed: when the method cannot be determined, the
    /// request is not retried.
    /// </summary>
    private static bool IsRetryAllowed(
        ResilienceContext context,
        HttpRetryOptions retry,
        string clientName,
        ILogger logger,
        HttpClientMetrics metrics)
    {
        var request = context.GetRequestMessage();
        var tracker = TrackerOf(request);
        var method = request?.Method.Method ?? "UNKNOWN";

        // A body that cannot be reproduced forbids a retry whatever the method or configuration
        // says, because the second attempt would send something different from the first.
        if (tracker is { IsBodyReplayable: false })
        {
            Suppress(clientName, method, "non_replayable_body", logger, metrics);
            return false;
        }

        // An explicit per-request decision wins in both directions.
        if (tracker?.AllowRetry is { } allowRetry)
        {
            if (!allowRetry)
            {
                Suppress(clientName, method, "caller_opt_out", logger, metrics);
            }

            return allowRetry;
        }

        if (retry.RetryUnsafeMethods)
        {
            return true;
        }

        if (request?.Method is null)
        {
            Suppress(clientName, method, "unknown_method", logger, metrics);
            return false;
        }

        var allowed = retry.RetryableMethods.Contains(request.Method.Method, StringComparer.OrdinalIgnoreCase);
        if (!allowed)
        {
            Suppress(clientName, method, "non_idempotent", logger, metrics);
        }

        return allowed;
    }

    private static void Suppress(
        string clientName, string method, string reason, ILogger logger, HttpClientMetrics metrics)
    {
        metrics.RecordRetrySuppressed(clientName, method, reason);
        HttpClientLog.RetrySuppressed(logger, clientName, method, reason);
    }

    /// <summary>
    /// Replaces the request body with a fresh instance before the next attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defence in depth rather than the mechanism replay depends on. The retry strategy re-sends
    /// the same <see cref="HttpRequestMessage"/>, and <see cref="HttpClient"/> disposes its content
    /// once an attempt finishes. Byte-backed content happens to survive that - disposing a
    /// <see cref="ByteArrayContent"/> does not release the array it serializes from - and the tests
    /// pass with this method removed, so it is not load-bearing today.
    /// </para>
    /// <para>
    /// What replay actually rests on is that every body reaching this pipeline is byte-backed:
    /// <see cref="HttpRequestFactory"/> serializes a payload to a buffer and buffers
    /// caller-supplied content before the first send. This method keeps the guarantee honest if a
    /// body ever arrives that is not, and costs one allocation per retry.
    /// </para>
    /// </remarks>
    private static void RefreshRequestBody(ResilienceContext context)
    {
        var request = context.GetRequestMessage();
        if (request is null)
        {
            return;
        }

        var tracker = TrackerOf(request);
        if (tracker?.ContentFactory is null)
        {
            return;
        }

        var previous = request.Content;
        request.Content = tracker.ContentFactory();

        if (!ReferenceEquals(previous, request.Content))
        {
            previous?.Dispose();
        }
    }

    private static RateLimiter CreateWindowLimiter(HttpRateLimiterOptions options) =>
        options.SegmentsPerWindow > 1
            ? new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = options.Window,
                SegmentsPerWindow = options.SegmentsPerWindow,
                QueueLimit = options.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            })
            : new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = options.Window,
                QueueLimit = options.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });

    private static RequestAttemptTracker? TrackerOf(HttpRequestMessage? request) =>
        request is not null && request.Options.TryGetValue(RequestAttemptTracker.OptionsKey, out var tracker)
            ? tracker
            : null;

    private static void MarkTimeout(ResilienceContext context, TimeoutSource source)
    {
        var tracker = TrackerOf(context.GetRequestMessage());
        if (tracker is not null)
        {
            tracker.TimeoutSource = source;
        }
    }

    private static TimeoutSource TimeoutSourceOf(ResilienceContext context) =>
        TrackerOf(context.GetRequestMessage())?.TimeoutSource ?? TimeoutSource.None;

    private static void MarkRejection(ResilienceContext context, RejectionSource source)
    {
        var tracker = TrackerOf(context.GetRequestMessage());
        if (tracker is not null)
        {
            tracker.RejectionSource = source;
        }
    }

    private static string MethodOf(ResilienceContext context) =>
        context.GetRequestMessage()?.Method.Method ?? "UNKNOWN";

    private static string DescribeOutcome(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception?.GetType().Name
        ?? (outcome.Result is { } response ? ((int)response.StatusCode).ToString() : "unknown");
}
