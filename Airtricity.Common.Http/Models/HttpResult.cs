using System.Net;
using Airtricity.Common.Http.Exceptions;

namespace Airtricity.Common.Http.Models;

/// <summary>
/// Outcome of an HTTP operation that returns no body. Success and failure are both ordinary return
/// values, so an expected failure such as a 404 costs nothing to handle.
/// </summary>
/// <remarks>
/// The library throws only for programmer error, for caller cancellation, and from
/// <see cref="EnsureSuccess"/>. Everything else is reported here.
/// </remarks>
public class HttpResult
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyHeaders =
        new Dictionary<string, IReadOnlyList<string>>(0);

    /// <summary>Initializes a new instance. Use the static factory methods instead.</summary>
    internal HttpResult(
        bool isSuccess,
        HttpStatusCode? statusCode,
        HttpError? error,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers,
        int attemptCount,
        TimeSpan duration,
        string? traceId)
    {
        IsSuccess = isSuccess;
        StatusCode = statusCode;
        Error = error;
        Headers = headers ?? EmptyHeaders;
        AttemptCount = attemptCount;
        Duration = duration;
        TraceId = traceId;
    }

    /// <summary>True when the operation completed with a success status code.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed. The inverse of <see cref="IsSuccess"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Status code the server returned, or null when no response was received - a transport
    /// failure, a timeout, or a local rate-limiter or circuit-breaker rejection.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Structured failure detail. Null when <see cref="IsSuccess"/> is true.</summary>
    public HttpError? Error { get; }

    /// <summary>
    /// Response headers, with redacted names already stripped of their values. Empty when no
    /// response was received.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Number of attempts made, including the initial one.</summary>
    public int AttemptCount { get; }

    /// <summary>Wall-clock time the whole operation took, including retries and backoff waits.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Trace identifier of the ambient activity, for correlation with distributed traces.</summary>
    public string? TraceId { get; }

    /// <summary>
    /// Returns this result when it succeeded; otherwise throws the exception matching
    /// <see cref="HttpError.Category"/>. The escape hatch for callers who prefer exceptions.
    /// </summary>
    /// <returns>This instance, so the call can be chained.</returns>
    /// <exception cref="ResilientHttpException">The operation failed.</exception>
    public HttpResult EnsureSuccess()
    {
        if (!IsSuccess)
        {
            throw ResilientHttpException.FromError(Error!);
        }

        return this;
    }

    /// <summary>Creates a successful result for an operation with no response body.</summary>
    /// <param name="statusCode">Status code the server returned.</param>
    /// <param name="headers">Response headers, already redacted.</param>
    /// <param name="attemptCount">Number of attempts made.</param>
    /// <param name="duration">Time the operation took.</param>
    /// <param name="traceId">Trace identifier of the ambient activity.</param>
    public static HttpResult Success(
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        int attemptCount = 1,
        TimeSpan duration = default,
        string? traceId = null) =>
        new(true, statusCode, error: null, headers, attemptCount, duration, traceId);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">Structured failure detail. Must not be null.</param>
    /// <param name="headers">Response headers, already redacted, when a response was received.</param>
    /// <param name="duration">Time the operation took.</param>
    public static HttpResult Failure(
        HttpError error,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        TimeSpan duration = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new HttpResult(false, error.StatusCode, error, headers, error.AttemptCount, duration, error.TraceId);
    }
}

/// <summary>
/// Outcome of an HTTP operation that returns a deserialized body of type
/// <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">Type the response body deserializes to.</typeparam>
public sealed class HttpResult<T> : HttpResult
{
    internal HttpResult(
        bool isSuccess,
        HttpStatusCode? statusCode,
        T? value,
        bool hasValue,
        HttpError? error,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers,
        int attemptCount,
        TimeSpan duration,
        string? traceId)
        : base(isSuccess, statusCode, error, headers, attemptCount, duration, traceId)
    {
        Value = value;
        HasValue = hasValue;
    }

    /// <summary>
    /// The deserialized response body, or the default of <typeparamref name="T"/> when the
    /// operation failed or the response had no body. Check <see cref="HasValue"/> to tell a
    /// deserialized <c>null</c> apart from an absent body.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// True when a response body was present and deserialized. False for a 204, a zero-length
    /// body, or any failure - a success with no body is still a success.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Returns this result when it succeeded; otherwise throws the exception matching
    /// <see cref="HttpError.Category"/>.
    /// </summary>
    /// <returns>This instance, so <see cref="Value"/> can be read from the same expression.</returns>
    /// <exception cref="ResilientHttpException">The operation failed.</exception>
    public new HttpResult<T> EnsureSuccess()
    {
        if (!IsSuccess)
        {
            throw ResilientHttpException.FromError(Error!);
        }

        return this;
    }

    /// <summary>Creates a successful result carrying a deserialized body.</summary>
    /// <param name="statusCode">Status code the server returned.</param>
    /// <param name="value">The deserialized body.</param>
    /// <param name="hasValue">Whether a body was actually present.</param>
    /// <param name="headers">Response headers, already redacted.</param>
    /// <param name="attemptCount">Number of attempts made.</param>
    /// <param name="duration">Time the operation took.</param>
    /// <param name="traceId">Trace identifier of the ambient activity.</param>
    public static HttpResult<T> Success(
        HttpStatusCode statusCode,
        T? value,
        bool hasValue,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        int attemptCount = 1,
        TimeSpan duration = default,
        string? traceId = null) =>
        new(true, statusCode, value, hasValue, error: null, headers, attemptCount, duration, traceId);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">Structured failure detail. Must not be null.</param>
    /// <param name="headers">Response headers, already redacted, when a response was received.</param>
    /// <param name="duration">Time the operation took.</param>
    public static new HttpResult<T> Failure(
        HttpError error,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        TimeSpan duration = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new HttpResult<T>(
            false, error.StatusCode, default, hasValue: false, error, headers, error.AttemptCount, duration, error.TraceId);
    }
}
