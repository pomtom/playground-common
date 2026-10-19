using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Airtricity.Common.Http.Abstractions;
using Airtricity.Common.Http.Configuration;
using Airtricity.Common.Http.Models;
using Microsoft.Extensions.Logging;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Default <see cref="IResilientHttpClient"/>. Builds the request, sends it through the named
/// <see cref="HttpClient"/> supplied by <see cref="IHttpClientFactory"/>, and turns the outcome into
/// a result.
/// </summary>
/// <remarks>
/// Every verb helper funnels into <see cref="SendCoreAsync{TResponse}"/>, so resilience, error
/// mapping, logging and metrics behave identically no matter which method the caller used.
/// </remarks>
internal sealed class ResilientHttpClient : IResilientHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClientOptions _options;
    private readonly ILogger<ResilientHttpClient> _logger;
    private readonly HttpClientMetrics _metrics;
    private readonly HeaderRedactor _redactor;

    internal ResilientHttpClient(
        string name,
        IHttpClientFactory httpClientFactory,
        HttpClientOptions options,
        ILogger<ResilientHttpClient> logger,
        HttpClientMetrics metrics)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _redactor = new HeaderRedactor(options.RedactedHeaders);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> GetAsync<TResponse>(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(Spec(HttpMethod.Get, requestUri, payload: null, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> PostAsync<TRequest, TResponse>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(Spec(HttpMethod.Post, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> PutAsync<TRequest, TResponse>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(Spec(HttpMethod.Put, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> PatchAsync<TRequest, TResponse>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(Spec(HttpMethod.Patch, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> DeleteAsync<TResponse>(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(Spec(HttpMethod.Delete, requestUri, payload: null, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> PostAsync<TRequest>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Post, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> PutAsync<TRequest>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Put, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> PatchAsync<TRequest>(
        string requestUri, TRequest payload, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Patch, requestUri, payload, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> DeleteAsync(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Delete, requestUri, payload: null, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> HeadAsync(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Head, requestUri, payload: null, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult> OptionsAsync(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Spec(HttpMethod.Options, requestUri, payload: null, configure), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<TResponse>> SendAsync<TResponse>(
        HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendCoreAsync<TResponse>(request, ResponseHandling.Deserialize, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HttpResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendCoreAsync<object>(request, ResponseHandling.Discard, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<HttpResult<Stream>> GetStreamAsync(
        string requestUri, Action<HttpRequestSpec>? configure = null, CancellationToken cancellationToken = default) =>
        SendCoreAsync<Stream>(
            Spec(HttpMethod.Get, requestUri, payload: null, configure), ResponseHandling.Stream, cancellationToken);

    /// <inheritdoc />
    public Task<HttpResult<HttpResponseMessage>> SendRawAsync(
        HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendCoreAsync<HttpResponseMessage>(request, ResponseHandling.Raw, cancellationToken);
    }

    /// <summary>
    /// Expands a relative request URI against the configured base address, so logs and errors name
    /// the host that was actually called.
    /// </summary>
    private Uri? ResolveAbsoluteUri(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return _options.BaseAddress;
        }

        if (requestUri.IsAbsoluteUri || _options.BaseAddress is null)
        {
            return requestUri;
        }

        return Uri.TryCreate(_options.BaseAddress, requestUri, out var absolute) ? absolute : requestUri;
    }

    private static HttpRequestSpec Spec(
        HttpMethod method, string requestUri, object? payload, Action<HttpRequestSpec>? configure)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        var spec = new HttpRequestSpec
        {
            Method = method,
            RequestUri = requestUri,
            Payload = payload,
        };

        configure?.Invoke(spec);
        return spec;
    }

    /// <summary>
    /// The one path every request takes. Builds the message, sends it, classifies the outcome and
    /// reads the body according to <paramref name="handling"/>.
    /// </summary>
    private async Task<HttpResult<TResponse>> SendCoreAsync<TResponse>(
        HttpRequestSpec spec,
        ResponseHandling handling,
        CancellationToken cancellationToken)
    {
        var tracker = new RequestAttemptTracker();
        var method = spec.Method.Method;
        var stopwatch = Stopwatch.StartNew();
        Uri? requestUri = null;

        HttpRequestMessage request;
        try
        {
            request = await HttpRequestFactory.CreateAsync(
                spec, _options, tracker, _metrics, Name, _logger, cancellationToken).ConfigureAwait(false);

            // Resolved against the base address for diagnostics. HttpClient does this itself when
            // it sends, but an error reporting only "orders/1" does not say which service failed.
            requestUri = ResolveAbsoluteUri(request.RequestUri);
        }
        catch (JsonException exception)
        {
            return Fail<TResponse>(
                HttpErrorMapper.FromRequestSerializationFailure(exception, method, requestUri, tracker),
                headers: null,
                stopwatch.Elapsed);
        }
        catch (NotSupportedException exception)
        {
            return Fail<TResponse>(
                HttpErrorMapper.FromRequestSerializationFailure(exception, method, requestUri, tracker),
                headers: null,
                stopwatch.Elapsed);
        }

        var client = _httpClientFactory.CreateClient(Name);
        HttpResponseMessage? response = null;

        try
        {
            if (_options.EnableDetailedLogging)
            {
                HttpClientLog.RequestStarting(_logger, Name, method, UriSanitizer.ToSafeString(requestUri));
            }

            response = await client
                .SendAsync(request, spec.CompletionOption, cancellationToken)
                .ConfigureAwait(false);

            return await HandleResponseAsync<TResponse>(
                response, spec, handling, method, requestUri, tracker, stopwatch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop. Reporting that as an HTTP error would hide a deliberate
            // decision behind a failure the caller never made, so it propagates untouched.
            response?.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            response?.Dispose();
            var error = HttpErrorMapper.FromException(exception, method, requestUri, tracker);
            return Fail<TResponse>(error, headers: null, stopwatch.Elapsed);
        }
        finally
        {
            // The response is disposed above on every failing path, and by HandleResponseAsync on
            // the success paths that read the body. Raw and stream results hand ownership to the
            // caller, which the XML docs state.
            request.Dispose();
        }
    }

    private async Task<HttpResult<TResponse>> HandleResponseAsync<TResponse>(
        HttpResponseMessage response,
        HttpRequestSpec spec,
        ResponseHandling handling,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var headers = _redactor.Project(response.Headers, response.Content.Headers);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = await ReadErrorSnippetAsync(response, cancellationToken).ConfigureAwait(false);
            var error = HttpErrorMapper.FromResponse(response, method, requestUri, tracker, snippet);

            if (handling != ResponseHandling.Raw)
            {
                response.Dispose();
                return Fail<TResponse>(error, headers, stopwatch.Elapsed);
            }

            // A raw caller wants to inspect the response whatever its status, so ownership passes
            // to them even on failure.
            _metrics.RecordResult(Name, method, error.Category, (int)response.StatusCode);
            HttpClientLog.RequestFailed(_logger, Name, error);
            return new HttpResult<TResponse>(
                false, response.StatusCode, (TResponse)(object)response, hasValue: true, error, headers,
                tracker.EffectiveAttemptCount, stopwatch.Elapsed, error.TraceId);
        }

        switch (handling)
        {
            case ResponseHandling.Raw:
                return Succeed<TResponse>(
                    response.StatusCode, (TResponse)(object)response, hasValue: true, headers, tracker, stopwatch, method, requestUri);

            case ResponseHandling.Stream:
            {
                // Ownership of both the stream and the response passes to the caller; disposing
                // the stream is what returns the connection to the pool.
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return Succeed<TResponse>(
                    response.StatusCode, (TResponse)(object)stream, hasValue: true, headers, tracker, stopwatch, method, requestUri);
            }

            case ResponseHandling.Discard:
                response.Dispose();
                return Succeed<TResponse>(
                    response.StatusCode, default, hasValue: false, headers, tracker, stopwatch, method, requestUri);

            default:
                return await DeserializeAsync<TResponse>(
                    response, spec, method, requestUri, tracker, headers, stopwatch, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private async Task<HttpResult<TResponse>> DeserializeAsync<TResponse>(
        HttpResponseMessage response,
        HttpRequestSpec spec,
        string method,
        Uri? requestUri,
        RequestAttemptTracker tracker,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return Succeed<TResponse>(
                    response.StatusCode, default, hasValue: false, headers, tracker, stopwatch, method, requestUri);
            }

            var json = spec.JsonSerializerOptions
                ?? _options.JsonSerializerOptions
                ?? HttpRequestFactory.DefaultJsonOptions;

            // The response completes at the headers, so reading the body is not covered by either
            // resilience timeout. This bounds it.
            using var readTimeout = CreateReadTimeout(cancellationToken);

            var stream = await response.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var value = await JsonSerializer
                    .DeserializeAsync<TResponse>(stream, json, readTimeout.Token)
                    .ConfigureAwait(false);

                // A zero-length body deserializes to null without throwing, which is a success
                // with no value rather than a deserialized null.
                var hasValue = value is not null;
                return Succeed(response.StatusCode, value, hasValue, headers, tracker, stopwatch, method, requestUri);
            }
        }
        catch (JsonException exception)
        {
            var error = HttpErrorMapper.FromException(exception, method, requestUri, tracker);
            return Fail<TResponse>(error, headers, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The read timeout fired, not the caller.
            var error = HttpErrorMapper.FromException(
                new TimeoutException(
                    $"Reading the response body exceeded the configured ResponseReadTimeout of {_options.ResponseReadTimeout}."),
                method,
                requestUri,
                tracker);
            return Fail<TResponse>(error, headers, stopwatch.Elapsed);
        }
        finally
        {
            response.Dispose();
        }
    }

    private CancellationTokenSource CreateReadTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ResponseReadTimeout > TimeSpan.Zero)
        {
            source.CancelAfter(_options.ResponseReadTimeout);
        }

        return source;
    }

    /// <summary>
    /// Reads a bounded prefix of a failed response body, and only when the client is configured to.
    /// </summary>
    private async Task<string?> ReadErrorSnippetAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!_options.CaptureResponseBodyOnError || _options.MaxErrorBodyCharacters <= 0)
        {
            return null;
        }

        try
        {
            using var readTimeout = CreateReadTimeout(cancellationToken);
            var stream = await response.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

                var buffer = new char[_options.MaxErrorBodyCharacters];
                var read = await reader.ReadBlockAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                return read == 0 ? null : new string(buffer, 0, read);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException
                                              or OperationCanceledException or HttpRequestException)
        {
            // A diagnostic extra must never replace the failure the caller actually needs to see.
            return null;
        }
    }

    private HttpResult<TResponse> Succeed<TResponse>(
        HttpStatusCode statusCode,
        TResponse? value,
        bool hasValue,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        RequestAttemptTracker tracker,
        Stopwatch stopwatch,
        string method,
        Uri? requestUri)
    {
        stopwatch.Stop();
        _metrics.RecordResult(Name, method, HttpErrorCategory.None, (int)statusCode);
        HttpClientLog.RequestCompleted(
            _logger,
            Name,
            method,
            UriSanitizer.ToSafeString(requestUri),
            statusCode,
            tracker.EffectiveAttemptCount,
            stopwatch.ElapsedMilliseconds);

        return HttpResult<TResponse>.Success(
            statusCode,
            value,
            hasValue,
            headers,
            tracker.EffectiveAttemptCount,
            stopwatch.Elapsed,
            Activity.Current?.TraceId.ToString());
    }

    private HttpResult<TResponse> Fail<TResponse>(
        HttpError error,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers,
        TimeSpan duration)
    {
        _metrics.RecordResult(Name, error.Method, error.Category, error.StatusCode is null ? null : (int)error.StatusCode);
        HttpClientLog.RequestFailed(_logger, Name, error);
        return HttpResult<TResponse>.Failure(error, headers, duration);
    }

    /// <summary>What to do with the response body.</summary>
    private enum ResponseHandling
    {
        /// <summary>Deserialize it as JSON.</summary>
        Deserialize = 0,

        /// <summary>Ignore it and release the response.</summary>
        Discard,

        /// <summary>Hand the stream to the caller.</summary>
        Stream,

        /// <summary>Hand the whole response to the caller.</summary>
        Raw,
    }
}
