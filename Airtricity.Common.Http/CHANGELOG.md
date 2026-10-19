# Changelog

All notable changes to Airtricity.Common.Http are documented here.

## [2.0.0] - 2026-10-19

First release. This is a new package, not a rename of anything: before it, every Airtricity
application that called a REST API built its own `HttpClient` usage, its own retry handling and its
own error shape.

The version starts at `2.0.x` because all five packages in this repository are built and published
as one unit from `Airtricity.Common.sln` at a single shared version. It does not imply a `1.x` ever
existed.

### Added
- `IResilientHttpClient` — GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS and arbitrary custom
  methods, with typed JSON request and response handling, query parameters, per-request headers,
  cancellation throughout, and escape hatches for the raw `HttpResponseMessage` and for an unbuffered
  response stream.
- `HttpResult` / `HttpResult<T>` — a result-based API where an HTTP 404, a timeout and a
  circuit-breaker rejection are all ordinary return values, distinguished by
  `HttpError.Category`. `EnsureSuccess()` converts a failure into a typed exception for callers who
  prefer that style.
- `HttpError` with a stable machine-readable `Code`, status code, method, URI, attempt count,
  `Retry-After`, trace and span ids, and the original exception preserved.
- A six-strategy resilience pipeline built on `Microsoft.Extensions.Http.Resilience` and Polly v8:
  outbound rate limiter, total request timeout, retry, circuit breaker, concurrency limiter and
  per-attempt timeout, each independently configurable and independently disable-able.
- Retries are **not** applied to POST or PATCH by default, with a per-request opt-in via
  `AllowRetry` or `IdempotencyKey`.
- Request bodies are buffered before the first send so a retried attempt replays identical bytes; a
  body too large to buffer is sent once and marked non-retryable rather than silently replayed from
  an exhausted stream.
- `AddResilientHttpClient` registration overloads for code, `IConfiguration` and typed clients,
  plus `AddResilientHttpClients` to register every client defined in configuration. Each returns the
  underlying `IHttpClientBuilder`, so authentication and other delegating handlers chain on normally.
- Startup options validation through `IValidateOptions<HttpClientOptions>` and `ValidateOnStart()`.
- Header and URI redaction applied wherever a value could reach a log or an error, with a
  configurable `RedactedHeaders` list.
- Three metrics on the `Airtricity.Common.Http` meter, covering only what the platform and Polly do
  not already report: results by error category, suppressed retries, and request-body replay
  buffering.

### Notes
- **This package's `Microsoft.Extensions.*` floor is 10.0.13, not the 9.0.18 the four sibling
  packages advertise.** `Microsoft.Extensions.Http.Resilience` 10.11.0 is the only line with a real
  `net10.0` asset, and it requires `Microsoft.Extensions.Http` 10.0.13, which requires 10.0.13 of the
  rest of the stack. A lower direct reference is a NuGet package downgrade (NU1605) and fails the
  build. The floor is real whether or not it is written down.
- When resilience is enabled, the underlying `HttpClient.Timeout` is set to
  `Timeout.InfiniteTimeSpan` and `Resilience.Timeout.TotalRequestTimeout` becomes the single
  authority. `HttpClientOptions.Timeout` applies only when `Resilience.Enabled` is `false`.
