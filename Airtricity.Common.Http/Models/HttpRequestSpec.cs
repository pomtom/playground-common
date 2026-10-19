using System.Text.Json;

namespace Airtricity.Common.Http.Models;

/// <summary>
/// Describes a single HTTP request. The verb helpers on
/// <see cref="Abstractions.IResilientHttpClient"/> build one of these and hand it to the caller's
/// configure delegate, so per-call headers, query parameters and overrides need no extra overloads.
/// </summary>
public sealed class HttpRequestSpec
{
    /// <summary>
    /// HTTP method. Any method is allowed, including one the BCL does not predefine, for example
    /// <c>new HttpMethod("REPORT")</c>.
    /// </summary>
    public HttpMethod Method { get; set; } = HttpMethod.Get;

    /// <summary>
    /// Request URI. Relative to the client's configured base address, or absolute to bypass it.
    /// May be null when the base address is the complete target.
    /// </summary>
    public string? RequestUri { get; set; }

    /// <summary>
    /// Query parameters appended to the URI and escaped. A null value writes the key with no
    /// value, which some APIs use for flags.
    /// </summary>
    public IDictionary<string, string?> QueryParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Headers for this request only, overriding a client default of the same name. This is where
    /// a per-request Authorization header belongs; its value is redacted from logs and errors.
    /// </summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Content type for the request body. Falls back to the client's
    /// <see cref="Configuration.HttpClientOptions.DefaultMediaType"/>.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>
    /// Object serialized as the request body. Ignored when <see cref="Content"/> is set. Serialized
    /// once into a buffer, so a retried attempt replays the same bytes without re-serializing.
    /// </summary>
    public object? Payload { get; set; }

    /// <summary>
    /// Caller-supplied body, for multipart, form or binary content. Takes precedence over
    /// <see cref="Payload"/>. The caller keeps ownership of any stream it wraps; see
    /// <see cref="Configuration.HttpClientOptions.MaxBufferedRequestBytes"/> for how this interacts
    /// with retries.
    /// </summary>
    public HttpContent? Content { get; set; }

    /// <summary>
    /// Overrides the client's idempotency rule for this request only. Set true to allow a retry of
    /// a method that would otherwise not be retried, or false to forbid a retry that would
    /// otherwise be allowed. Null leaves
    /// <see cref="Configuration.HttpRetryOptions.RetryUnsafeMethods"/> in charge.
    /// </summary>
    public bool? AllowRetry { get; set; }

    /// <summary>
    /// Idempotency key sent as an Idempotency-Key header. Supplying one makes the request safe to
    /// retry, so it also opts this request into retries unless
    /// <see cref="AllowRetry"/> explicitly says otherwise.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// When the response is considered complete. Defaults to
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so that a large body is streamed
    /// rather than buffered in full before deserialization begins.
    /// </summary>
    public HttpCompletionOption CompletionOption { get; set; } = HttpCompletionOption.ResponseHeadersRead;

    /// <summary>
    /// Serializer settings for this request only. Falls back to the client's configured settings.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
