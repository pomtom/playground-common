using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Airtricity.Common.Http.Configuration;
using Airtricity.Common.Http.Models;
using Microsoft.Extensions.Logging;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Turns an <see cref="HttpRequestSpec"/> into an <see cref="HttpRequestMessage"/>, and arranges
/// for the body to be reproducible if the request is retried.
/// </summary>
internal static class HttpRequestFactory
{
    /// <summary>Header used to carry a caller-supplied idempotency key.</summary>
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Shared serializer settings, created once. Rebuilding <see cref="JsonSerializerOptions"/> per
    /// request would re-derive and re-cache type metadata every time, which is the single easiest
    /// way to make a JSON client slow.
    /// </summary>
    internal static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds the request message and populates <paramref name="tracker"/> with everything the
    /// resilience pipeline will need to know about replaying the body.
    /// </summary>
    /// <param name="spec">The request description.</param>
    /// <param name="options">Client configuration supplying defaults.</param>
    /// <param name="tracker">Per-request record carried on the message.</param>
    /// <param name="metrics">Metrics sink, for body-buffering outcomes.</param>
    /// <param name="clientName">Name of the client, for metric tags.</param>
    /// <param name="logger">Logger, for the non-replayable-body notice.</param>
    /// <param name="cancellationToken">Token that cancels buffering of a caller-supplied body.</param>
    internal static async Task<HttpRequestMessage> CreateAsync(
        HttpRequestSpec spec,
        HttpClientOptions options,
        RequestAttemptTracker tracker,
        HttpClientMetrics metrics,
        string clientName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(spec.Method, BuildUri(spec, options));

        ApplyHeaders(spec, options, request);

        tracker.AllowRetry = ResolveAllowRetry(spec);
        tracker.ContentFactory = await BuildContentFactoryAsync(
            spec, options, tracker, metrics, clientName, logger, cancellationToken).ConfigureAwait(false);

        if (tracker.ContentFactory is not null)
        {
            request.Content = tracker.ContentFactory();
        }

        request.Options.Set(RequestAttemptTracker.OptionsKey, tracker);
        return request;
    }

    /// <summary>
    /// Composes the request URI from the spec's path and query parameters. Absolute URIs are used
    /// as given; relative ones are left relative so that
    /// <see cref="HttpClient.BaseAddress"/> resolves them.
    /// </summary>
    internal static Uri BuildUri(HttpRequestSpec spec, HttpClientOptions options)
    {
        var path = spec.RequestUri ?? string.Empty;
        var query = BuildQueryString(spec.QueryParameters);

        if (query.Length > 0)
        {
            path += path.Contains('?', StringComparison.Ordinal) ? "&" + query : "?" + query;
        }

        if (path.Length == 0)
        {
            return options.BaseAddress
                ?? throw new ArgumentException(
                    "A request URI is required when the client has no BaseAddress configured.", nameof(spec));
        }

        return Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(path, UriKind.Relative);
    }

    /// <summary>Escapes and joins query parameters. A null value emits a bare key.</summary>
    private static string BuildQueryString(IDictionary<string, string?> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var parameter in parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(parameter.Key));

            if (parameter.Value is not null)
            {
                builder.Append('=').Append(Uri.EscapeDataString(parameter.Value));
            }
        }

        return builder.ToString();
    }

    private static void ApplyHeaders(HttpRequestSpec spec, HttpClientOptions options, HttpRequestMessage request)
    {
        // Client defaults first, so a per-request header of the same name replaces rather than
        // appends. HttpClient.DefaultRequestHeaders would append, which silently sends two
        // Authorization headers.
        foreach (var header in options.DefaultHeaders)
        {
            if (!spec.Headers.ContainsKey(header.Key))
            {
                TrySetHeader(request, header.Key, header.Value);
            }
        }

        foreach (var header in spec.Headers)
        {
            TrySetHeader(request, header.Key, header.Value);
        }

        if (!string.IsNullOrWhiteSpace(spec.IdempotencyKey))
        {
            TrySetHeader(request, IdempotencyKeyHeader, spec.IdempotencyKey!);
        }

        var accept = spec.MediaType ?? options.DefaultMediaType;
        if (!string.IsNullOrWhiteSpace(accept) && request.Headers.Accept.Count == 0)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }
    }

    private static void TrySetHeader(HttpRequestMessage request, string name, string value)
    {
        // TryAddWithoutValidation keeps a header the BCL considers content-scoped, or malformed,
        // from throwing mid-send. Remove first so a replace really replaces.
        request.Headers.Remove(name);
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException(
                $"Header '{name}' could not be added to the request.", nameof(name));
        }
    }

    /// <summary>
    /// A request opts into retries when it says so explicitly, or by carrying an idempotency key -
    /// which is exactly the mechanism that makes a repeated POST safe.
    /// </summary>
    private static bool? ResolveAllowRetry(HttpRequestSpec spec) =>
        spec.AllowRetry ?? (string.IsNullOrWhiteSpace(spec.IdempotencyKey) ? null : true);

    /// <summary>
    /// Produces a factory that can build the request body as many times as there are attempts.
    /// </summary>
    /// <remarks>
    /// A factory rather than a single <see cref="HttpContent"/> instance, because
    /// <see cref="HttpClient"/> disposes the request content once an attempt completes. Re-sending
    /// the same instance on a retry would read from a disposed object; giving every attempt a fresh
    /// instance over the same buffer sidesteps the problem entirely.
    /// </remarks>
    private static async Task<Func<HttpContent>?> BuildContentFactoryAsync(
        HttpRequestSpec spec,
        HttpClientOptions options,
        RequestAttemptTracker tracker,
        HttpClientMetrics metrics,
        string clientName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var mediaType = spec.MediaType ?? options.DefaultMediaType;

        if (spec.Content is not null)
        {
            return await BufferCallerContentAsync(
                spec.Content, options, tracker, metrics, clientName, logger, cancellationToken).ConfigureAwait(false);
        }

        if (spec.Payload is null)
        {
            return null;
        }

        var json = spec.JsonSerializerOptions ?? options.JsonSerializerOptions ?? DefaultJsonOptions;

        // Serialized once, outside the pipeline, so a retry replays bytes instead of repeating the
        // serialization work - and so a serialization failure is reported before anything is sent.
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(spec.Payload, spec.Payload.GetType(), json);

        return () =>
        {
            var content = new ByteArrayContent(payloadBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            return content;
        };
    }

    /// <summary>
    /// Buffers a caller-supplied body so it can be replayed, within the configured limit.
    /// </summary>
    private static async Task<Func<HttpContent>?> BufferCallerContentAsync(
        HttpContent content,
        HttpClientOptions options,
        RequestAttemptTracker tracker,
        HttpClientMetrics metrics,
        string clientName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;

        if (declaredLength > options.MaxBufferedRequestBytes)
        {
            // Too large to hold: send it once and forbid a retry rather than replay an exhausted
            // stream and silently send an empty body.
            tracker.IsBodyReplayable = false;
            metrics.RecordBodyReplayBuffering(clientName, "exceeded_limit");
            HttpClientLog.BodyNotReplayable(logger, clientName, options.MaxBufferedRequestBytes);
            return () => content;
        }

        byte[] buffer;
        try
        {
            await content.LoadIntoBufferAsync(options.MaxBufferedRequestBytes, cancellationToken).ConfigureAwait(false);
            buffer = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // LoadIntoBufferAsync throws this when the body runs past the limit. The body is now
            // partly consumed, so it is neither replayable nor reliably sendable.
            tracker.IsBodyReplayable = false;
            metrics.RecordBodyReplayBuffering(clientName, "exceeded_limit");
            HttpClientLog.BodyNotReplayable(logger, clientName, options.MaxBufferedRequestBytes);
            return () => content;
        }

        var headers = content.Headers.ToList();
        metrics.RecordBodyReplayBuffering(clientName, "buffered");

        return () =>
        {
            var replay = new ByteArrayContent(buffer);
            replay.Headers.Clear();
            foreach (var header in headers)
            {
                replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return replay;
        };
    }
}
