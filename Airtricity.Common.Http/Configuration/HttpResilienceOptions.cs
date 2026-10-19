using System.ComponentModel.DataAnnotations;

namespace Airtricity.Common.Http.Configuration;

/// <summary>
/// Resilience configuration for a single named HTTP client: retry, circuit breaker and timeouts.
/// Rate limiting and concurrency limiting are configured separately through
/// <see cref="HttpRateLimiterOptions"/>, because throttling outbound traffic and reacting to
/// downstream failures are independent concerns.
/// </summary>
public sealed class HttpResilienceOptions
{
    /// <summary>
    /// When false, no resilience handler is registered at all and the client behaves like a plain
    /// <see cref="System.Net.Http.HttpClient"/>. Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Retry configuration for transient failures.</summary>
    public HttpRetryOptions Retry { get; set; } = new();

    /// <summary>Circuit-breaker configuration.</summary>
    public HttpCircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>Total-request and per-attempt timeout configuration.</summary>
    public HttpTimeoutOptions Timeout { get; set; } = new();
}

/// <summary>
/// Controls how transient HTTP failures are retried.
/// </summary>
/// <remarks>
/// Transient means HTTP 408, HTTP 429, HTTP 5xx and transport-level failures such as a dropped
/// connection or a DNS failure. Ordinary 4xx responses are permanent and are never retried.
/// Caller cancellation is never retried.
/// </remarks>
public sealed class HttpRetryOptions
{
    /// <summary>When false, requests are attempted exactly once. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of retry attempts after the initial try.</summary>
    [Range(0, 20, ErrorMessage = "MaxRetryAttempts must be between 0 and 20")]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay between attempts.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for the delay between attempts when using exponential backoff.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, uses exponential backoff; otherwise a constant delay.</summary>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>
    /// When true, a random jitter is applied to each delay so that many callers failing at the same
    /// moment do not retry in lockstep. Defaults to true.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// When true, a Retry-After header on a 429 or 503 response overrides the computed backoff
    /// delay. Defaults to true.
    /// </summary>
    public bool RespectRetryAfterHeader { get; set; } = true;

    /// <summary>
    /// When true, HTTP 5xx responses are treated as retryable. Set to false for a downstream API
    /// where a server error may mean the request was applied anyway. HTTP 408 and 429 and transport
    /// failures remain retryable either way. Defaults to true.
    /// </summary>
    public bool RetryServerErrors { get; set; } = true;

    /// <summary>
    /// When true, every method is eligible for retry, overriding
    /// <see cref="RetryableMethods"/>. Defaults to false: retrying a POST that already reached the
    /// server duplicates its effect, so opting in is deliberate.
    /// </summary>
    /// <remarks>
    /// A single request can opt in without changing this client-wide setting, by setting
    /// <see cref="Models.HttpRequestSpec.AllowRetry"/> or
    /// <see cref="Models.HttpRequestSpec.IdempotencyKey"/>.
    /// </remarks>
    public bool RetryUnsafeMethods { get; set; }

    /// <summary>
    /// Methods eligible for automatic retry. POST and PATCH are deliberately absent: they are not
    /// idempotent, so a retry can duplicate an effect that already happened.
    /// </summary>
    /// <remarks>
    /// PUT and DELETE are present because RFC 9110 defines them as idempotent - applying either
    /// twice leaves the same state as applying it once - so retrying them is both safe and
    /// usually what the caller wants. Remove them here for an API that does not honour that.
    /// </remarks>
    public IList<string> RetryableMethods { get; } = new List<string>
    {
        "GET",
        "HEAD",
        "OPTIONS",
        "TRACE",
        "PUT",
        "DELETE",
    };
}

/// <summary>
/// Circuit-breaker configuration. While the circuit is open, requests fail immediately with
/// <see cref="Models.HttpErrorCategory.CircuitOpen"/> instead of reaching an unhealthy downstream
/// service.
/// </summary>
public sealed class HttpCircuitBreakerOptions
{
    /// <summary>When false, no circuit breaker is added. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Proportion of failed requests within <see cref="SamplingDuration"/> that opens the circuit,
    /// greater than 0 and at most 1. Defaults to 0.1, so ten per cent failures.
    /// </summary>
    [Range(0.0, 1.0)]
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>Window over which <see cref="FailureRatio"/> is measured. Defaults to 30 seconds.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum number of requests that must occur within <see cref="SamplingDuration"/> before the
    /// circuit can open. Stops a handful of early failures from breaking a quiet client.
    /// Defaults to 100.
    /// </summary>
    [Range(2, int.MaxValue)]
    public int MinimumThroughput { get; set; } = 100;

    /// <summary>
    /// How long the circuit stays open before moving to half-open and letting a trial request
    /// through. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Timeout configuration. Two independent budgets apply: one for the whole call including every
/// retry, and one for each individual attempt.
/// </summary>
public sealed class HttpTimeoutOptions
{
    /// <summary>
    /// Budget for the entire call, covering all attempts and the waits between them. Defaults to
    /// 30 seconds.
    /// </summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When false, no per-attempt timeout is applied and
    /// <see cref="TotalRequestTimeout"/> is the only budget. Defaults to true.
    /// </summary>
    /// <remarks>
    /// Turn this off for a downstream API whose latency varies so widely that any per-attempt
    /// bound would either cut off legitimate slow responses or be too generous to be useful.
    /// </remarks>
    public bool EnableAttemptTimeout { get; set; } = true;

    /// <summary>
    /// Budget for a single attempt. An attempt that exceeds it is a retryable failure, so a hung
    /// connection does not consume the whole total budget. Must be shorter than
    /// <see cref="TotalRequestTimeout"/>. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
