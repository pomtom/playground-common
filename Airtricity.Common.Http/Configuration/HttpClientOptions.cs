using System.Text.Json;

namespace Airtricity.Common.Http.Configuration;

/// <summary>
/// Strongly-typed configuration for a single named HTTP client, bound from the
/// "ResilientHttpClients:&lt;name&gt;" configuration section.
/// </summary>
/// <remarks>
/// Every named client gets its own instance, so two downstream APIs can have completely different
/// resilience settings. The library holds no credentials and no environment-specific URLs of its
/// own: everything here is supplied by the consuming application.
/// </remarks>
public sealed class HttpClientOptions
{
    /// <summary>
    /// Default configuration section name. Each named client binds from a child section, for
    /// example "ResilientHttpClients:ExternalApi".
    /// </summary>
    public const string SectionName = "ResilientHttpClients";

    /// <summary>
    /// Base address that request URIs are resolved against. A request may also pass an absolute
    /// URI, in which case the base address is ignored. Must be absolute, and should normally end
    /// with a trailing slash so that relative paths combine as expected.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Timeout applied to the underlying <see cref="System.Net.Http.HttpClient"/>. This only takes
    /// effect when <see cref="HttpResilienceOptions.Enabled"/> is false.
    /// </summary>
    /// <remarks>
    /// When resilience is enabled the underlying client's timeout is set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> and
    /// <see cref="HttpTimeoutOptions.TotalRequestTimeout"/> becomes the single authority. Leaving
    /// both active would mean two independent clocks racing, and the one belonging to
    /// <see cref="System.Net.Http.HttpClient"/> reports a bare cancellation that cannot be told
    /// apart from the caller cancelling.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Headers applied to every request from this client. A per-request header of the same name
    /// replaces the value here.
    /// </summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional User-Agent value. Ignored when null or whitespace.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Content type used for request bodies and the Accept header. Defaults to application/json.</summary>
    public string DefaultMediaType { get; set; } = "application/json";

    /// <summary>
    /// Serializer settings for request and response bodies. When null, a shared cached instance
    /// using camelCase property naming and case-insensitive reads is used, so that serializer
    /// metadata is not rebuilt per request.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// When true, up to <see cref="MaxErrorBodyCharacters"/> of the response body of a failed
    /// request is captured into <see cref="Models.HttpError.ResponseSnippet"/>. Defaults to false,
    /// because a response body can contain personal data.
    /// </summary>
    public bool CaptureResponseBodyOnError { get; set; }

    /// <summary>Maximum length of the captured error body snippet. Defaults to 2048 characters.</summary>
    public int MaxErrorBodyCharacters { get; set; } = 2048;

    /// <summary>
    /// Bounds reading and deserializing the response body. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// Requests complete as soon as the response headers arrive, so that a large body can be
    /// streamed instead of buffered. That means reading the body happens after the resilience
    /// pipeline has already returned, and neither
    /// <see cref="HttpTimeoutOptions.AttemptTimeout"/> nor
    /// <see cref="HttpTimeoutOptions.TotalRequestTimeout"/> covers it. Without this, a server that
    /// sends headers and then stalls mid-body would hang the call indefinitely.
    /// </remarks>
    public TimeSpan ResponseReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum size of a caller-supplied request body that will be buffered so that it can be
    /// replayed on a retry. A body larger than this is sent once and marked non-retryable rather
    /// than being silently replayed from an exhausted stream. Defaults to 1 MB.
    /// </summary>
    public long MaxBufferedRequestBytes { get; set; } = 1L * 1024 * 1024;

    /// <summary>
    /// When true, additional per-request detail is logged at Debug level. Redaction still applies:
    /// this raises verbosity, it never reveals a redacted header value or a request body.
    /// </summary>
    public bool EnableDetailedLogging { get; set; }

    /// <summary>
    /// Header names whose values are replaced with "***" before anything is logged or placed in an
    /// error message. Add to this list for any bespoke header that carries a credential.
    /// </summary>
    public IList<string> RedactedHeaders { get; } = new List<string>
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "api-key",
        "Ocp-Apim-Subscription-Key",
    };

    /// <summary>Retry, circuit-breaker and timeout configuration.</summary>
    public HttpResilienceOptions Resilience { get; set; } = new();

    /// <summary>Outbound rate-limiting and concurrency-limiting configuration.</summary>
    public HttpRateLimiterOptions RateLimiter { get; set; } = new();

    /// <summary>Lightweight self-check mirroring the sibling libraries' options pattern.</summary>
    public bool IsValid() =>
        (BaseAddress is null || BaseAddress.IsAbsoluteUri) &&
        !string.IsNullOrWhiteSpace(DefaultMediaType) &&
        Resilience.Timeout.AttemptTimeout > TimeSpan.Zero &&
        Resilience.Timeout.TotalRequestTimeout > Resilience.Timeout.AttemptTimeout;
}
