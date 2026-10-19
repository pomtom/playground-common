using Airtricity.Common.Http.Models;

namespace Airtricity.Common.Http.Abstractions;

/// <summary>
/// A named HTTP client with a configured resilience pipeline and a consistent error model.
/// </summary>
/// <remarks>
/// <para>
/// Every method returns an <see cref="HttpResult"/> or <see cref="HttpResult{T}"/> rather than
/// throwing: an HTTP 404, a timeout and a circuit-breaker rejection are all ordinary return values,
/// distinguished by <see cref="HttpError.Category"/>. Call
/// <see cref="HttpResult.EnsureSuccess"/> to convert a failure into a typed exception where that
/// reads better.
/// </para>
/// <para>
/// Two things always throw. An invalid argument throws <see cref="ArgumentNullException"/> or
/// <see cref="ArgumentException"/>, because that is a programming error rather than an outcome. And
/// cancellation through the caller's own token throws <see cref="OperationCanceledException"/>,
/// because turning a deliberate cancellation into a generic HTTP error would hide it.
/// </para>
/// <para>
/// The <c>configure</c> delegate on each verb helper receives the
/// <see cref="HttpRequestSpec"/> that is about to be sent, which is where per-call headers, query
/// parameters, content type and retry overrides go.
/// </para>
/// </remarks>
public interface IResilientHttpClient
{
    /// <summary>Name this client was registered under.</summary>
    string Name { get; }

    /// <summary>Sends a GET request and deserializes the JSON response body.</summary>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult<TResponse>> GetAsync<TResponse>(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a POST request with a JSON body and deserializes the JSON response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <remarks>POST is not retried unless the client or the request opts in; see
    /// <see cref="Configuration.HttpRetryOptions.RetryUnsafeMethods"/>.</remarks>
    Task<HttpResult<TResponse>> PostAsync<TRequest, TResponse>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a PUT request with a JSON body and deserializes the JSON response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult<TResponse>> PutAsync<TRequest, TResponse>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a PATCH request with a JSON body and deserializes the JSON response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult<TResponse>> PatchAsync<TRequest, TResponse>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a DELETE request and deserializes the JSON response body.</summary>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult<TResponse>> DeleteAsync<TResponse>(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a POST request with a JSON body, ignoring any response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> PostAsync<TRequest>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a PUT request with a JSON body, ignoring any response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> PutAsync<TRequest>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a PATCH request with a JSON body, ignoring any response body.</summary>
    /// <typeparam name="TRequest">Type of the request body.</typeparam>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="payload">Object serialized as the request body.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> PatchAsync<TRequest>(
        string requestUri,
        TRequest payload,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a DELETE request, ignoring any response body.</summary>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> DeleteAsync(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a HEAD request. Response headers are available on the result; HEAD responses carry no
    /// body by definition.
    /// </summary>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> HeadAsync(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an OPTIONS request. The advertised methods are in the Allow header on the result.
    /// </summary>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> OptionsAsync(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an arbitrary request and deserializes the JSON response body. Use this for a custom
    /// HTTP method, or whenever full control over the request is wanted. Every verb helper above
    /// funnels into this method, so resilience and error handling are identical.
    /// </summary>
    /// <typeparam name="TResponse">Type the response body deserializes to.</typeparam>
    /// <param name="request">The request to send. Must not be null.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult<TResponse>> SendAsync<TResponse>(
        HttpRequestSpec request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an arbitrary request, ignoring any response body.
    /// </summary>
    /// <param name="request">The request to send. Must not be null.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    Task<HttpResult> SendAsync(
        HttpRequestSpec request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a GET request and returns the response body as a stream, without buffering it.
    /// </summary>
    /// <param name="requestUri">URI relative to the configured base address, or an absolute URI.</param>
    /// <param name="configure">Optional per-request customisation.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <remarks>
    /// The caller owns the returned stream and must dispose it; disposing it is what releases the
    /// underlying connection back to the pool.
    /// </remarks>
    Task<HttpResult<Stream>> GetStreamAsync(
        string requestUri,
        Action<HttpRequestSpec>? configure = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request and returns the raw <see cref="HttpResponseMessage"/> without reading or
    /// interpreting the body. For the cases the typed methods cannot express.
    /// </summary>
    /// <param name="request">The request to send. Must not be null.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <remarks>
    /// The resilience pipeline still applies. A non-success status is reported through
    /// <see cref="HttpResult.IsSuccess"/> as usual, but the response is returned either way so the
    /// caller can inspect it. The caller owns the returned message and must dispose it.
    /// </remarks>
    Task<HttpResult<HttpResponseMessage>> SendRawAsync(
        HttpRequestSpec request,
        CancellationToken cancellationToken = default);
}
