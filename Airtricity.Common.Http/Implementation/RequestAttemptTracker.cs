namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Which timeout strategy produced a timeout. Both strategies throw the same Polly exception type,
/// so without this the two are indistinguishable.
/// </summary>
internal enum TimeoutSource
{
    /// <summary>No timeout has fired.</summary>
    None = 0,

    /// <summary>The per-attempt timeout fired. Retryable.</summary>
    Attempt,

    /// <summary>The total request timeout fired. Terminal.</summary>
    Total,
}

/// <summary>
/// Which local limiter shed a request. Both surface as the same Polly rejection exception.
/// </summary>
internal enum RejectionSource
{
    /// <summary>Nothing was rejected.</summary>
    None = 0,

    /// <summary>The window-based outbound rate limiter rejected it.</summary>
    RateLimiter,

    /// <summary>The concurrency limiter rejected it.</summary>
    ConcurrencyLimiter,
}

/// <summary>
/// Per-request mutable record of what the resilience pipeline did, carried on the
/// <see cref="HttpRequestMessage"/> itself.
/// </summary>
/// <remarks>
/// <para>
/// This travels on <see cref="HttpRequestMessage.Options"/> rather than on Polly's
/// <c>ResilienceContext</c> deliberately. The context is pooled and returned once the handler
/// finishes, so reading it after the call has unwound is not safe; the request message is owned by
/// the caller for the whole operation. Pipeline callbacks reach it by asking the context for its
/// request message, which is public API.
/// </para>
/// <para>
/// Mutated only from pipeline callbacks and the innermost handler, all of which run sequentially
/// within one logical call, so no synchronization is needed.
/// </para>
/// </remarks>
internal sealed class RequestAttemptTracker
{
    /// <summary>Key used to carry the tracker on a request message.</summary>
    internal static readonly HttpRequestOptionsKey<RequestAttemptTracker> OptionsKey =
        new("Airtricity.Common.Http.AttemptTracker");

    /// <summary>Number of attempts that actually reached the transport.</summary>
    internal int AttemptCount { get; private set; }

    /// <summary>Which timeout fired, if any.</summary>
    internal TimeoutSource TimeoutSource { get; set; }

    /// <summary>Which local limiter shed the request, if any.</summary>
    internal RejectionSource RejectionSource { get; set; }

    /// <summary>
    /// Produces a fresh request body for the next attempt. Null when the request has no body.
    /// </summary>
    internal Func<HttpContent>? ContentFactory { get; set; }

    /// <summary>
    /// False when the body cannot be reproduced for another attempt, which forbids a retry
    /// regardless of method or configuration.
    /// </summary>
    internal bool IsBodyReplayable { get; set; } = true;

    /// <summary>Per-request override of the idempotency rule. Null leaves the client policy in charge.</summary>
    internal bool? AllowRetry { get; set; }

    /// <summary>Records that another attempt has reached the transport.</summary>
    internal void RecordAttempt() => AttemptCount++;

    /// <summary>At least one attempt is always reported, so a shed request does not read as zero.</summary>
    internal int EffectiveAttemptCount => AttemptCount == 0 ? 1 : AttemptCount;
}
