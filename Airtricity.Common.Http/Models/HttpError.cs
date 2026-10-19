using System.Net;
using Airtricity.Common.Http.Implementation;

namespace Airtricity.Common.Http.Models;

/// <summary>
/// Structured description of a failed HTTP operation. Safe to log as-is: header values are never
/// placed here, and a response body appears only when
/// <see cref="Configuration.HttpClientOptions.CaptureResponseBodyOnError"/> is enabled.
/// </summary>
public sealed class HttpError
{
    /// <summary>What kind of failure this is.</summary>
    public HttpErrorCategory Category { get; init; }

    /// <summary>
    /// Stable machine-readable code, suitable for metrics and alert rules. For example
    /// "http.status.404", "http.transport", "http.timeout.total", "http.ratelimited".
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Human-readable summary. Contains no credentials, header values or personal data.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Status code the server returned, when a response was received at all. Null for transport
    /// failures, timeouts, and rate-limiter or circuit-breaker rejections.
    /// </summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>Reason phrase that accompanied <see cref="StatusCode"/>, when present.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>HTTP method of the request that failed.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>URI of the request that failed, including its query string.</summary>
    public Uri? RequestUri { get; init; }

    /// <summary>
    /// Truncated beginning of the response body, present only when the client is configured to
    /// capture it.
    /// </summary>
    public string? ResponseSnippet { get; init; }

    /// <summary>
    /// Trace identifier of the ambient <see cref="System.Diagnostics.Activity"/>, for correlating
    /// this failure with distributed traces and downstream logs.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>Span identifier of the ambient activity, when one is in scope.</summary>
    public string? SpanId { get; init; }

    /// <summary>Total number of attempts made, including the initial one.</summary>
    public int AttemptCount { get; init; }

    /// <summary>
    /// Delay requested by a Retry-After response header, when the server sent one. Useful for
    /// scheduling work the caller decides to retry itself.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Underlying exception, preserved rather than discarded. Becomes the inner exception of the
    /// exception thrown by <see cref="HttpResult.EnsureSuccess"/>.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates the exception type matching <see cref="Category"/>, for callers who want to throw a
    /// failure they have already inspected.
    /// </summary>
    /// <returns>The most specific exception available for this error's category.</returns>
    public Exceptions.ResilientHttpException ToException() =>
        Exceptions.ResilientHttpException.FromError(this);

    /// <summary>Returns a single-line description suitable for a log message.</summary>
    /// <remarks>
    /// The URI is rendered through the sanitizer, so any embedded credentials are gone and query
    /// values are replaced. This is the form that ends up in logs, so it is the stricter of the two.
    /// </remarks>
    public override string ToString()
    {
        var safeUri = UriSanitizer.ToSafeString(RequestUri);

        return StatusCode is null
            ? $"{Category} ({Code}): {Message} [{Method} {safeUri}, attempts {AttemptCount}]"
            : $"{Category} ({Code}): {Message} [{Method} {safeUri} -> {(int)StatusCode.Value}, attempts {AttemptCount}]";
    }
}
