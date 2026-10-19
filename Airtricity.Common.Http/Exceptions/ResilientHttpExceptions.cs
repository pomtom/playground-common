using Airtricity.Common.Http.Models;

namespace Airtricity.Common.Http.Exceptions;

/// <summary>
/// Base type for every failure this library reports as an exception. Thrown only by
/// <see cref="HttpResult.EnsureSuccess"/> and its generic counterpart: the default path returns a
/// result instead.
/// </summary>
/// <remarks>
/// Caller cancellation is never represented by this hierarchy. When the caller's token fires, the
/// original <see cref="OperationCanceledException"/> propagates untouched.
/// </remarks>
public class ResilientHttpException : Exception
{
    /// <summary>Initializes a new instance carrying the given structured error.</summary>
    /// <param name="error">The error to describe. Must not be null.</param>
    public ResilientHttpException(HttpError error)
        : base(error?.Message ?? throw new ArgumentNullException(nameof(error)), error.Exception)
    {
        Error = error;
    }

    /// <summary>Initializes a new instance with an explicit message.</summary>
    /// <param name="message">Message for the exception.</param>
    /// <param name="error">The error to describe. Must not be null.</param>
    public ResilientHttpException(string message, HttpError error)
        : base(message, error?.Exception)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>Structured detail about the failure, including category, code and request context.</summary>
    public HttpError Error { get; }

    /// <summary>
    /// Creates the exception type that matches <paramref name="error"/>'s
    /// <see cref="HttpError.Category"/>.
    /// </summary>
    /// <param name="error">The error to translate. Must not be null.</param>
    /// <returns>The most specific exception type available for the category.</returns>
    public static ResilientHttpException FromError(HttpError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Category switch
        {
            HttpErrorCategory.HttpStatus => new HttpStatusException(error),
            HttpErrorCategory.Transport => new HttpTransportException(error),
            HttpErrorCategory.Timeout or HttpErrorCategory.AttemptTimeout => new HttpRequestTimeoutException(error),
            HttpErrorCategory.RateLimited => new HttpRateLimitRejectedException(error),
            HttpErrorCategory.CircuitOpen => new HttpCircuitOpenException(error),
            HttpErrorCategory.Serialization => new HttpSerializationException(error),
            _ => new ResilientHttpException(error),
        };
    }
}

/// <summary>
/// The request completed and the server returned a non-success status code.
/// </summary>
public sealed class HttpStatusException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpStatusException(HttpError error) : base(error)
    {
    }
}

/// <summary>
/// The request never completed because of a transport or connection failure.
/// </summary>
public sealed class HttpTransportException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpTransportException(HttpError error) : base(error)
    {
    }
}

/// <summary>
/// A timeout elapsed. <see cref="HttpError.Category"/> says whether it was the total request
/// budget or a single attempt.
/// </summary>
public sealed class HttpRequestTimeoutException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpRequestTimeoutException(HttpError error) : base(error)
    {
    }
}

/// <summary>
/// The local outbound rate limiter or concurrency limiter rejected the request. Nothing was sent,
/// so this is never a remote HTTP 429 - that arrives as <see cref="HttpStatusException"/>.
/// </summary>
public sealed class HttpRateLimitRejectedException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpRateLimitRejectedException(HttpError error) : base(error)
    {
    }
}

/// <summary>
/// The circuit breaker is open, so the request was not attempted.
/// </summary>
public sealed class HttpCircuitOpenException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpCircuitOpenException(HttpError error) : base(error)
    {
    }
}

/// <summary>
/// A request body could not be serialized, or a response body could not be deserialized.
/// </summary>
public sealed class HttpSerializationException : ResilientHttpException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="error">The error to describe.</param>
    public HttpSerializationException(HttpError error) : base(error)
    {
    }
}
