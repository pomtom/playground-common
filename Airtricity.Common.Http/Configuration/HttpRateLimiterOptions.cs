using System.ComponentModel.DataAnnotations;

namespace Airtricity.Common.Http.Configuration;

/// <summary>
/// Outbound rate-limiting configuration for a single named HTTP client. This throttles what the
/// application sends. It is not the same thing as reacting to an HTTP 429 returned by a remote
/// server, which is handled by <see cref="HttpRetryOptions"/>.
/// </summary>
/// <remarks>
/// A request rejected here never leaves the process and surfaces as
/// <see cref="Models.HttpErrorCategory.RateLimited"/> with no HTTP status code. A remote 429
/// surfaces as <see cref="Models.HttpErrorCategory.HttpStatus"/> with status 429. The two are
/// deliberately distinguishable.
/// </remarks>
public sealed class HttpRateLimiterOptions
{
    /// <summary>
    /// When false, no rate limiter is added. Defaults to false: a throttle the application did not
    /// ask for is a liability, so this is opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Requests permitted per <see cref="Window"/>. Defaults to 100.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    /// <summary>Length of the rate-limiting window. Defaults to 1 second.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Number of segments the window is divided into. One is a fixed window; a higher value gives a
    /// smoother sliding window at the cost of a little more bookkeeping. Defaults to 1.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SegmentsPerWindow { get; set; } = 1;

    /// <summary>
    /// How many requests may wait for a permit instead of being rejected outright. Defaults to 0,
    /// so excess requests fail fast rather than queueing.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int QueueLimit { get; set; }

    /// <summary>
    /// Concurrency limiting, which caps requests in flight rather than requests per unit of time.
    /// Configured and enabled independently of the rate limiter above.
    /// </summary>
    public HttpConcurrencyLimiterOptions Concurrency { get; set; } = new();
}

/// <summary>
/// Limits how many requests a named client may have in flight at once, protecting both the
/// calling application's resources and the downstream service.
/// </summary>
public sealed class HttpConcurrencyLimiterOptions
{
    /// <summary>When false, no concurrency limiter is added. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum number of requests in flight at once. Defaults to 100.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// How many requests may wait for a permit instead of being rejected outright. Defaults to 0.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int QueueLimit { get; set; }
}
