using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Airtricity.Common.Http.Models;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Classifies a failure into an <see cref="HttpError"/>. Pure: no I/O, no logging, no side effects,
/// so the whole mapping table is directly testable.
/// </summary>
internal static class HttpErrorMapper
{
    /// <summary>
    /// Maps a non-success response.
    /// </summary>
    /// <param name="response">The response received. Must not be null.</param>
    /// <param name="method">Method of the request that produced it.</param>
    /// <param name="requestUri">URI of the request that produced it.</param>
    /// <param name="tracker">Attempt record for the request.</param>
    /// <param name="responseSnippet">Truncated response body, when the client captures it.</param>
    internal static HttpError FromResponse(
        HttpResponseMessage response,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker,
        string? responseSnippet)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;

        return Build(
            HttpErrorCategory.HttpStatus,
            $"http.status.{status}",
            $"Request failed with status {status} ({response.ReasonPhrase ?? response.StatusCode.ToString()}).",
            method,
            requestUri,
            tracker,
            statusCode: response.StatusCode,
            reasonPhrase: response.ReasonPhrase,
            retryAfter: ReadRetryAfter(response),
            responseSnippet: responseSnippet,
            exception: null);
    }

    /// <summary>
    /// Maps an exception that escaped the resilience pipeline.
    /// </summary>
    /// <param name="exception">The exception to classify. Must not be null.</param>
    /// <param name="method">Method of the request.</param>
    /// <param name="requestUri">URI of the request.</param>
    /// <param name="tracker">Attempt record for the request.</param>
    /// <remarks>
    /// Caller cancellation must never reach here. The client checks the caller's token first and
    /// rethrows, because reporting a deliberate cancellation as an HTTP failure would hide it.
    /// </remarks>
    internal static HttpError FromException(
        Exception exception,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Our own limiters shed the request. Nothing was sent, so there is no status code -
            // which is what separates this from a remote 429.
            RateLimiterRejectedException rejected => Build(
                HttpErrorCategory.RateLimited,
                tracker.RejectionSource == RejectionSource.ConcurrencyLimiter
                    ? "http.concurrency_rejected"
                    : "http.ratelimited",
                tracker.RejectionSource == RejectionSource.ConcurrencyLimiter
                    ? "Request rejected locally by the concurrency limiter; it was not sent."
                    : "Request rejected locally by the outbound rate limiter; it was not sent.",
                method,
                requestUri,
                tracker,
                retryAfter: rejected.RetryAfter,
                exception: exception),

            BrokenCircuitException => Build(
                HttpErrorCategory.CircuitOpen,
                "http.circuit_open",
                "Circuit breaker is open for this client; the request was not attempted.",
                method,
                requestUri,
                tracker,
                exception: exception),

            // Both timeout strategies throw this one type, so the strategy that fired stamps the
            // tracker on its way out and we read it back here.
            TimeoutRejectedException when tracker.TimeoutSource == TimeoutSource.Attempt => Build(
                HttpErrorCategory.AttemptTimeout,
                "http.timeout.attempt",
                "Every attempt exceeded the per-attempt timeout.",
                method,
                requestUri,
                tracker,
                exception: exception),

            TimeoutRejectedException => Build(
                HttpErrorCategory.Timeout,
                "http.timeout.total",
                "The total request timeout elapsed.",
                method,
                requestUri,
                tracker,
                exception: exception),

            HttpRequestException transport => Build(
                HttpErrorCategory.Transport,
                "http.transport",
                $"The request failed at the transport level: {transport.Message}",
                method,
                requestUri,
                tracker,
                statusCode: transport.StatusCode,
                exception: exception),

            SocketException or IOException => Build(
                HttpErrorCategory.Transport,
                "http.transport",
                $"The connection failed: {exception.Message}",
                method,
                requestUri,
                tracker,
                exception: exception),

            JsonException or NotSupportedException => Build(
                HttpErrorCategory.Serialization,
                "http.serialization",
                $"The response body could not be deserialized: {exception.Message}",
                method,
                requestUri,
                tracker,
                exception: exception),

            // A timeout inside the stack cancels a token that is not the caller's. The client has
            // already established that the caller's token did not fire, so this is ours.
            OperationCanceledException => Build(
                HttpErrorCategory.Timeout,
                "http.timeout.total",
                "The operation was cancelled before it completed, and the caller did not request it. This is normally a timeout.",
                method,
                requestUri,
                tracker,
                exception: exception),

            _ => Build(
                HttpErrorCategory.Unexpected,
                "http.unexpected",
                $"An unexpected failure occurred: {exception.GetType().Name}.",
                method,
                requestUri,
                tracker,
                exception: exception),
        };
    }

    /// <summary>Maps a failure to serialize the request body, which happens before anything is sent.</summary>
    /// <param name="exception">The serialization exception.</param>
    /// <param name="method">Method of the request.</param>
    /// <param name="requestUri">URI of the request.</param>
    /// <param name="tracker">Attempt record for the request.</param>
    internal static HttpError FromRequestSerializationFailure(
        Exception exception,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker) =>
        Build(
            HttpErrorCategory.Serialization,
            "http.serialization.request",
            $"The request body could not be serialized: {exception.Message}",
            method,
            requestUri,
            tracker,
            exception: exception);

    /// <summary>Reads a Retry-After header, accepting both the delta-seconds and HTTP-date forms.</summary>
    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    private static HttpError Build(
        HttpErrorCategory category,
        string code,
        string message,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker,
        HttpStatusCode? statusCode = null,
        string? reasonPhrase = null,
        TimeSpan? retryAfter = null,
        string? responseSnippet = null,
        Exception? exception = null)
    {
        var activity = Activity.Current;

        return new HttpError
        {
            Category = category,
            Code = code,
            Message = message,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Method = method,
            RequestUri = UriSanitizer.StripCredentials(requestUri),
            ResponseSnippet = responseSnippet,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            AttemptCount = tracker.EffectiveAttemptCount,
            RetryAfter = retryAfter,
            Exception = exception,
        };
    }
}
