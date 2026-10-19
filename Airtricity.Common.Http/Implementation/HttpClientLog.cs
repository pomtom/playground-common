using System.Net;
using Airtricity.Common.Http.Models;
using Microsoft.Extensions.Logging;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Every log message the library writes, in one place, with structured named placeholders.
/// </summary>
/// <remarks>
/// Nothing here accepts a header collection, a request body or a response body. The only URI that
/// reaches a template has already been through <see cref="UriSanitizer"/>, and credentials never
/// appear in any argument. Keeping the templates together is what makes that claim checkable.
/// </remarks>
internal static class HttpClientLog
{
    // 1000-1099: request lifecycle.
    private static readonly Action<ILogger, string, string, string, int, int, long, Exception?> RequestCompletedCallback =
        LoggerMessage.Define<string, string, string, int, int, long>(
            LogLevel.Information,
            new EventId(1000, nameof(RequestCompleted)),
            "HTTP {ClientName} {Method} {SafeUri} responded {StatusCode} after {AttemptCount} attempt(s) in {ElapsedMilliseconds}ms");

    private static readonly Action<ILogger, string, string, string, Exception?> RequestStartingCallback =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            new EventId(1001, nameof(RequestStarting)),
            "HTTP {ClientName} sending {Method} {SafeUri}");

    // 1100-1199: resilience events.
    private static readonly Action<ILogger, string, string, int, TimeSpan, string, Exception?> RetryingCallback =
        LoggerMessage.Define<string, string, int, TimeSpan, string>(
            LogLevel.Warning,
            new EventId(1100, nameof(Retrying)),
            "HTTP {ClientName} {Method} transient failure ({Outcome}); retry {AttemptNumber} scheduled after {Delay}");

    private static readonly Action<ILogger, string, string, string, Exception?> RetrySuppressedCallback =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1101, nameof(RetrySuppressed)),
            "HTTP {ClientName} {Method} failed transiently but was not retried: {Reason}");

    private static readonly Action<ILogger, string, TimeSpan, Exception?> TotalTimeoutCallback =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Warning,
            new EventId(1102, nameof(TotalTimeout)),
            "HTTP {ClientName} exceeded its total request timeout of {Timeout}");

    private static readonly Action<ILogger, string, TimeSpan, Exception?> AttemptTimeoutCallback =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Warning,
            new EventId(1103, nameof(AttemptTimeout)),
            "HTTP {ClientName} attempt exceeded its timeout of {Timeout}");

    private static readonly Action<ILogger, string, TimeSpan, Exception?> CircuitOpenedCallback =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Warning,
            new EventId(1104, nameof(CircuitOpened)),
            "HTTP {ClientName} circuit opened for {BreakDuration}; requests will fail fast until it closes");

    private static readonly Action<ILogger, string, Exception?> CircuitClosedCallback =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1105, nameof(CircuitClosed)),
            "HTTP {ClientName} circuit closed; downstream service is healthy again");

    private static readonly Action<ILogger, string, Exception?> CircuitHalfOpenedCallback =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1106, nameof(CircuitHalfOpened)),
            "HTTP {ClientName} circuit half-opened; letting one trial request through");

    private static readonly Action<ILogger, string, string, Exception?> LimiterRejectedCallback =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1107, nameof(LimiterRejected)),
            "HTTP {ClientName} request shed locally by the {Limiter}; nothing was sent");

    private static readonly Action<ILogger, string, long, Exception?> BodyNotReplayableCallback =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(1108, nameof(BodyNotReplayable)),
            "HTTP {ClientName} request body exceeds the {MaxBufferedRequestBytes}-byte replay buffer, so this request will not be retried");

    // 1200-1299: failures.
    private static readonly Action<ILogger, string, string, string, string, string, int, Exception?> RequestFailedCallback =
        LoggerMessage.Define<string, string, string, string, string, int>(
            LogLevel.Error,
            new EventId(1200, nameof(RequestFailed)),
            "HTTP {ClientName} {Method} {SafeUri} failed: {Category} ({Code}) after {AttemptCount} attempt(s)");

    internal static void RequestStarting(ILogger logger, string clientName, string method, string safeUri) =>
        RequestStartingCallback(logger, clientName, method, safeUri, null);

    internal static void RequestCompleted(
        ILogger logger, string clientName, string method, string safeUri, HttpStatusCode statusCode, int attemptCount, long elapsedMilliseconds) =>
        RequestCompletedCallback(logger, clientName, method, safeUri, (int)statusCode, attemptCount, elapsedMilliseconds, null);

    internal static void Retrying(
        ILogger logger, string clientName, string method, int attemptNumber, TimeSpan delay, string outcome, Exception? exception) =>
        RetryingCallback(logger, clientName, method, attemptNumber, delay, outcome, exception);

    internal static void RetrySuppressed(ILogger logger, string clientName, string method, string reason) =>
        RetrySuppressedCallback(logger, clientName, method, reason, null);

    internal static void TotalTimeout(ILogger logger, string clientName, TimeSpan timeout) =>
        TotalTimeoutCallback(logger, clientName, timeout, null);

    internal static void AttemptTimeout(ILogger logger, string clientName, TimeSpan timeout) =>
        AttemptTimeoutCallback(logger, clientName, timeout, null);

    internal static void CircuitOpened(ILogger logger, string clientName, TimeSpan breakDuration) =>
        CircuitOpenedCallback(logger, clientName, breakDuration, null);

    internal static void CircuitClosed(ILogger logger, string clientName) =>
        CircuitClosedCallback(logger, clientName, null);

    internal static void CircuitHalfOpened(ILogger logger, string clientName) =>
        CircuitHalfOpenedCallback(logger, clientName, null);

    internal static void LimiterRejected(ILogger logger, string clientName, string limiter) =>
        LimiterRejectedCallback(logger, clientName, limiter, null);

    internal static void BodyNotReplayable(ILogger logger, string clientName, long maxBufferedRequestBytes) =>
        BodyNotReplayableCallback(logger, clientName, maxBufferedRequestBytes, null);

    internal static void RequestFailed(ILogger logger, string clientName, HttpError error) =>
        RequestFailedCallback(
            logger,
            clientName,
            error.Method,
            UriSanitizer.ToSafeString(error.RequestUri),
            error.Category.ToString(),
            error.Code,
            error.AttemptCount,
            error.Exception);
}
