using System.Diagnostics.Metrics;
using Airtricity.Common.Http.Models;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// The library's own metrics. Deliberately small: it records only what nothing else already
/// reports.
/// </summary>
/// <remarks>
/// <para>
/// Request duration, in-flight count and connection counts come from the .NET HTTP client's
/// built-in instrumentation. Retry counts, circuit-breaker state changes, timeouts and
/// rate-limiter rejections come from Polly's own telemetry, which
/// <c>Microsoft.Extensions.Resilience</c> wires up automatically and tags with the pipeline name.
/// Emitting those again would double-count them on any dashboard that collects both.
/// </para>
/// <para>
/// What is left is the classification this library adds and the decisions only it makes.
/// </para>
/// </remarks>
public sealed class HttpClientMetrics : IDisposable
{
    /// <summary>Name of the meter this library publishes under.</summary>
    public const string MeterName = "Airtricity.Common.Http";

    private readonly Meter _meter;
    private readonly Counter<long> _results;
    private readonly Counter<long> _retriesSuppressed;
    private readonly Counter<long> _bodyReplay;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="meterFactory">
    /// Factory supplied by dependency injection, so the meter participates in the host's metrics
    /// configuration. When null, a standalone meter is created.
    /// </param>
    public HttpClientMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);

        _results = _meter.CreateCounter<long>(
            "airtricity.http.client.results",
            unit: "{request}",
            description: "Completed operations by error category. The only signal that separates a local rate-limit rejection from a remote 429, or a total timeout from an attempt timeout.");

        _retriesSuppressed = _meter.CreateCounter<long>(
            "airtricity.http.client.retries_suppressed",
            unit: "{request}",
            description: "Retries that were not attempted because the method is not idempotent, the body could not be replayed, or the caller opted out. A retry that never happens is invisible to Polly.");

        _bodyReplay = _meter.CreateCounter<long>(
            "airtricity.http.client.body_replay_buffered",
            unit: "{request}",
            description: "Caller-supplied request bodies buffered so a retry could replay them, and those that exceeded the buffer limit.");
    }

    /// <summary>Records a completed operation.</summary>
    /// <param name="clientName">Name of the client that made the call.</param>
    /// <param name="method">HTTP method used.</param>
    /// <param name="category">Outcome category; <see cref="HttpErrorCategory.None"/> for success.</param>
    /// <param name="statusCode">Status code, when a response was received.</param>
    public void RecordResult(string clientName, string method, HttpErrorCategory category, int? statusCode)
    {
        if (statusCode is null)
        {
            _results.Add(
                1,
                new KeyValuePair<string, object?>("client.name", clientName),
                new KeyValuePair<string, object?>("http.request.method", method),
                new KeyValuePair<string, object?>("airtricity.http.error.category", category.ToString()));
            return;
        }

        _results.Add(
            1,
            new KeyValuePair<string, object?>("client.name", clientName),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("airtricity.http.error.category", category.ToString()),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode.Value));
    }

    /// <summary>Records a retry that was deliberately not attempted.</summary>
    /// <param name="clientName">Name of the client.</param>
    /// <param name="method">HTTP method used.</param>
    /// <param name="reason">Why the retry was suppressed.</param>
    public void RecordRetrySuppressed(string clientName, string method, string reason) =>
        _retriesSuppressed.Add(
            1,
            new KeyValuePair<string, object?>("client.name", clientName),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("airtricity.http.suppression.reason", reason));

    /// <summary>Records the outcome of trying to buffer a request body for replay.</summary>
    /// <param name="clientName">Name of the client.</param>
    /// <param name="outcome">Either "buffered" or "exceeded_limit".</param>
    public void RecordBodyReplayBuffering(string clientName, string outcome) =>
        _bodyReplay.Add(
            1,
            new KeyValuePair<string, object?>("client.name", clientName),
            new KeyValuePair<string, object?>("airtricity.http.body_replay.outcome", outcome));

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
