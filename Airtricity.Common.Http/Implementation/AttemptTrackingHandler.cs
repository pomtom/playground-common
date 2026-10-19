namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Innermost delegating handler. Counts the attempts that actually reach the transport, which is
/// the only place that number is observable: the resilience pipeline sits above it and the caller
/// sits above that.
/// </summary>
internal sealed class AttemptTrackingHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Options.TryGetValue(RequestAttemptTracker.OptionsKey, out var tracker))
        {
            tracker.RecordAttempt();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
