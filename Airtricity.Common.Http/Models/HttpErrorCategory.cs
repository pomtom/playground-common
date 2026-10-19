namespace Airtricity.Common.Http.Models;

/// <summary>
/// The kind of failure an <see cref="HttpError"/> describes. Callers switch on this rather than
/// inspecting exception types or status codes, so that an infrastructure problem is never mistaken
/// for an HTTP response and the reverse.
/// </summary>
public enum HttpErrorCategory
{
    /// <summary>No failure. Present on a successful result.</summary>
    None = 0,

    /// <summary>
    /// The request completed and the server returned a non-success status code. The status is in
    /// <see cref="HttpError.StatusCode"/>. A remote HTTP 429 lands here, not in
    /// <see cref="RateLimited"/>.
    /// </summary>
    HttpStatus,

    /// <summary>
    /// The request never completed because of a transport or connection failure: DNS failure,
    /// connection refused, TLS failure, a dropped socket.
    /// </summary>
    Transport,

    /// <summary>
    /// The total request timeout elapsed, covering every attempt and the waits between them.
    /// </summary>
    Timeout,

    /// <summary>
    /// A single attempt exceeded its own timeout. Seen when retries are exhausted by repeated
    /// per-attempt timeouts; a single such timeout is normally retried instead of surfacing.
    /// </summary>
    AttemptTimeout,

    /// <summary>
    /// The local outbound rate limiter or concurrency limiter rejected the request. Nothing was
    /// sent, so there is no status code. This is not a remote 429.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The circuit breaker is open because the downstream service has been failing, so the request
    /// was not attempted.
    /// </summary>
    CircuitOpen,

    /// <summary>A request body could not be serialized, or a response body could not be deserialized.</summary>
    Serialization,

    /// <summary>
    /// The operation was cancelled by something other than the caller's cancellation token. When
    /// the caller's own token fires, the original <see cref="OperationCanceledException"/>
    /// propagates instead of becoming a result, so this category is rare.
    /// </summary>
    Canceled,

    /// <summary>An unanticipated failure. The original exception is preserved on the error.</summary>
    Unexpected,
}
