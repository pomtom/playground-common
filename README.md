# Airtricity.Common.Http

A resilient HTTP client for calling REST APIs from .NET 10 applications. One registration call
gives you every HTTP verb, JSON handling, a consistent error model, and a configurable resilience
pipeline — retry, circuit breaker, timeouts, rate limiting and concurrency limiting — built on
`IHttpClientFactory`, `Microsoft.Extensions.Http.Resilience` and Polly v8.

## Features

- Every standard method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS) plus arbitrary custom methods.
- Typed JSON request and response handling via `System.Text.Json`, with raw-response and
  unbuffered-stream escape hatches.
- A result-based API: an HTTP 404, a timeout and a circuit-breaker rejection are return values, not
  exceptions. `EnsureSuccess()` is there when you would rather throw.
- A six-strategy resilience pipeline, each strategy independently configurable and disable-able.
- **Retries are off for POST and PATCH by default**, with a per-request opt-in.
- Request bodies buffered so a retry replays identical bytes.
- Named and typed clients, each with its own configuration, so two downstream APIs never share
  resilience settings.
- Header and URI redaction on every path that reaches a log or an error.
- Structured logging, trace correlation and metrics that do not duplicate what the platform emits.

## Installation

```
dotnet add package Airtricity.Common.Http
```

Targets `net10.0`.

> **Dependency floor.** This package requires `Microsoft.Extensions.*` **10.0.13** or later, not the
> 9.0.18 the four sibling `Airtricity.Common.*` packages advertise.
> `Microsoft.Extensions.Http.Resilience` 10.11.0 is the only line shipping a real `net10.0` asset,
> and it pulls `Microsoft.Extensions.Http` 10.0.13, which requires 10.0.13 of the rest of the stack.

## Registration

```csharp
builder.Services.AddResilientHttpClient("ExternalApi", options =>
{
    options.BaseAddress = new Uri("https://api.example.com/");
    options.Resilience.Timeout.TotalRequestTimeout = TimeSpan.FromSeconds(30);
    options.Resilience.Retry.MaxRetryAttempts = 5;
});
```

Then inject `IResilientHttpClientFactory` and resolve by name:

```csharp
public sealed class OrderService(IResilientHttpClientFactory factory)
{
    private readonly IResilientHttpClient _http = factory.CreateClient("ExternalApi");
}
```

### From configuration

```csharp
builder.Services.AddResilientHttpClient("ExternalApi", builder.Configuration);
```

```jsonc
{
  "ResilientHttpClients": {
    "ExternalApi": {
      "BaseAddress": "https://api.example.com/",
      "Resilience": {
        "Retry": { "MaxRetryAttempts": 5, "Delay": "00:00:01" },
        "Timeout": { "TotalRequestTimeout": "00:00:30", "AttemptTimeout": "00:00:10" },
        "CircuitBreaker": { "FailureRatio": 0.2, "MinimumThroughput": 20 }
      },
      "RateLimiter": { "Enabled": true, "PermitLimit": 100, "Window": "00:00:01" }
    }
  }
}
```

Register every client in that section at once:

```csharp
builder.Services.AddResilientHttpClients(builder.Configuration);
```

### Typed clients

```csharp
builder.Services.AddResilientHttpClient<OrdersApiClient>(options =>
    options.BaseAddress = new Uri("https://orders.example.com/"));

public sealed class OrdersApiClient(IResilientHttpClient http)
{
    public async Task<Order?> GetAsync(int id, CancellationToken ct = default) =>
        (await http.GetAsync<Order>($"orders/{id}", cancellationToken: ct)).Value;
}
```

The client is named after the type, so its configuration section is
`ResilientHttpClients:OrdersApiClient`.

## Verb examples

```csharp
// GET with a typed response
var order = await http.GetAsync<Order>("orders/42", cancellationToken: ct);

// POST a JSON body and read a JSON response
var created = await http.PostAsync<CreateOrder, Order>("orders", new CreateOrder("widget"), cancellationToken: ct);

// POST where the response body is not needed
await http.PostAsync("audit", new AuditEntry("order-created"), cancellationToken: ct);

// PUT and PATCH
await http.PutAsync<Order, Order>("orders/42", updated, cancellationToken: ct);
await http.PatchAsync<OrderPatch, Order>("orders/42", patch, cancellationToken: ct);

// DELETE, with or without a response body
await http.DeleteAsync("orders/42", cancellationToken: ct);
var receipt = await http.DeleteAsync<Receipt>("orders/42", cancellationToken: ct);

// HEAD and OPTIONS — headers are on the result
var head = await http.HeadAsync("orders/42", cancellationToken: ct);
var etag = head.Headers.TryGetValue("ETag", out var values) ? values[0] : null;

var options = await http.OptionsAsync("orders", cancellationToken: ct);
```

### Query parameters, headers and content type

```csharp
var page = await http.GetAsync<OrderPage>("orders", spec =>
{
    spec.QueryParameters["status"] = "open";
    spec.QueryParameters["page"] = "2";
    spec.Headers["X-Tenant"] = tenantId;
    spec.Headers["Authorization"] = $"Bearer {token}";   // redacted from logs and errors
}, ct);
```

Query values are escaped. A `null` value writes a bare key, for APIs that use flag parameters.

### Custom HTTP methods

```csharp
var report = await http.SendAsync<Report>(new HttpRequestSpec
{
    Method = new HttpMethod("REPORT"),
    RequestUri = "orders/report",
    Payload = new ReportRequest("monthly"),
}, ct);
```

### Raw responses and streaming

```csharp
// Full control; the resilience pipeline still applies. You own disposal.
var raw = await http.SendRawAsync(new HttpRequestSpec { RequestUri = "export" }, ct);
using var response = raw.Value!;

// Large payloads, streamed rather than buffered. Disposing the stream releases the connection.
var download = await http.GetStreamAsync("files/big.csv", cancellationToken: ct);
await using var stream = download.Value!;
await stream.CopyToAsync(destination, ct);
```

## Error handling

Every method returns a result. Failures are categorised, so an infrastructure problem is never
mistaken for an HTTP response:

```csharp
var result = await http.GetAsync<Order>("orders/42", cancellationToken: ct);

if (result.IsSuccess)
{
    return result.Value;
}

return result.Error!.Category switch
{
    HttpErrorCategory.HttpStatus when result.StatusCode == HttpStatusCode.NotFound => null,
    HttpErrorCategory.RateLimited  => throw new RetryLaterException(result.Error.RetryAfter),
    HttpErrorCategory.CircuitOpen  => Fallback(),
    HttpErrorCategory.Timeout      => Fallback(),
    _ => throw result.Error.ToException(),
};
```

| Category | Meaning |
|---|---|
| `HttpStatus` | The server answered with a non-success status. **A remote HTTP 429 lands here.** |
| `Transport` | DNS, connection, TLS or socket failure. Nothing was answered. |
| `Timeout` | The total request budget elapsed. |
| `AttemptTimeout` | Every attempt exceeded its own budget. |
| `RateLimited` | **Our own** limiter shed the request. Nothing was sent, so there is no status code. |
| `CircuitOpen` | The circuit breaker is open; the request was not attempted. |
| `Serialization` | A request body could not be serialized, or a response body could not be read. |
| `Canceled` | Cancelled by something other than the caller's token. |
| `Unexpected` | Anything else. The original exception is preserved. |

A local rate-limit rejection and a remote 429 are deliberately distinguishable: the first is
`RateLimited` with no status code, the second is `HttpStatus` with status 429.

### Why results, and how to throw instead

Results make the expected-failure path cheap and explicit: a 404 or a shed request costs nothing to
handle, and an error cannot be accidentally ignored by forgetting a `catch`. The cost is a slightly
larger API surface and call sites that must branch. For the "this must not fail" path, use
`EnsureSuccess()`:

```csharp
var order = (await http.GetAsync<Order>("orders/42", cancellationToken: ct))
    .EnsureSuccess()
    .Value;
```

It throws the type matching the category — `HttpStatusException`, `HttpTransportException`,
`HttpRequestTimeoutException`, `HttpRateLimitRejectedException`, `HttpCircuitOpenException` or
`HttpSerializationException` — all deriving from `ResilientHttpException` and all carrying the same
`HttpError`, with the original exception as `InnerException`.

The library throws in exactly three cases: an invalid argument, caller cancellation, and
`EnsureSuccess()`. Everything else is a result.

### Cancellation

```csharp
var result = await http.GetAsync<Order>("orders/42", cancellationToken: ct);
```

If `ct` fires, the original `OperationCanceledException` propagates unchanged. It never becomes a
result and is never retried — turning a deliberate cancellation into a generic HTTP error would hide
a decision the caller made.

## Resilience

Strategies apply outermost to innermost:

| # | Strategy | Default | Why it sits here |
|---|---|---|---|
| 1 | Rate limiter | **off** | Shedding before any budget, permit or connection is spent |
| 2 | Total request timeout | 30 s | Covers every attempt and every backoff wait |
| 3 | Retry | on, 3 attempts | Inside the total budget; outside the breaker |
| 4 | Circuit breaker | on | Sees each attempt's outcome, not the retried aggregate |
| 5 | Concurrency limiter | **off** | Inside the breaker; an open circuit takes no permit |
| 6 | Per-attempt timeout | 10 s | A hung attempt becomes retryable instead of eating the call |

### Defaults

| Option | Default |
|---|---|
| `Resilience.Enabled` | `true` |
| `Retry.Enabled` / `MaxRetryAttempts` | `true` / `3` |
| `Retry.Delay` / `MaxDelay` | `2s` / `30s` |
| `Retry.UseExponentialBackoff` / `UseJitter` | `true` / `true` |
| `Retry.RespectRetryAfterHeader` | `true` |
| `Retry.RetryServerErrors` | `true` |
| `Retry.RetryUnsafeMethods` | **`false`** |
| `Retry.RetryableMethods` | GET, HEAD, OPTIONS, TRACE, PUT, DELETE |
| `CircuitBreaker.Enabled` | `true` |
| `CircuitBreaker.FailureRatio` / `MinimumThroughput` | `0.1` / `100` |
| `CircuitBreaker.SamplingDuration` / `BreakDuration` | `30s` / `5s` |
| `Timeout.TotalRequestTimeout` / `AttemptTimeout` | `30s` / `10s` |
| `RateLimiter.Enabled` | **`false`** |
| `RateLimiter.PermitLimit` / `Window` / `QueueLimit` | `100` / `1s` / `0` |
| `RateLimiter.Concurrency.Enabled` | **`false`** |
| `ResponseReadTimeout` | `30s` |
| `CaptureResponseBodyOnError` | **`false`** |

Rate limiting and concurrency limiting are opt-in: a throttle nobody asked for is a liability.
The circuit-breaker defaults need 100 requests in a 30-second window before they can trip, which is
deliberate — lower `MinimumThroughput` for a low-traffic client or it will never open.

### Retry and idempotency

Retried conditions: HTTP 408, HTTP 429, HTTP 5xx (unless `RetryServerErrors` is `false`), transport
failures, and per-attempt timeouts. Ordinary 4xx is never retried. Caller cancellation is never
retried. A request shed by our own limiter or rejected by the circuit breaker is never retried —
retrying either would defeat it.

**POST and PATCH are not retried by default.** They are not idempotent, so a retry can duplicate an
effect that already happened. PUT and DELETE *are* retried, because RFC 9110 defines them as
idempotent. Three ways to change that:

```csharp
// Per request, explicitly
await http.PostAsync<Create, Order>("orders", body, spec => spec.AllowRetry = true, ct);

// Per request, via an idempotency key the API honours — this also opts in
await http.PostAsync<Create, Order>("orders", body, spec => spec.IdempotencyKey = key, ct);

// Per client, for an API where every write is idempotent
options.Resilience.Retry.RetryUnsafeMethods = true;

// Or narrow the set
options.Resilience.Retry.RetryableMethods.Remove("DELETE");
```

`spec.AllowRetry = false` forbids a retry that would otherwise be allowed, and wins over everything.

### Request bodies and retries

A retried attempt must send the same bytes as the first. JSON payloads are serialized once into a
buffer; caller-supplied `HttpContent` is buffered up to `MaxBufferedRequestBytes` (1 MB) before the
first send. A body larger than that is sent once and **marked non-retryable** — it is not replayed
from an exhausted stream, which would silently send an empty body. Raise the limit, or pre-buffer the
content yourself, if you need large bodies retried.

### Timeouts

`TotalRequestTimeout` bounds the whole call including retries and backoff waits. `AttemptTimeout`
bounds one attempt, and an attempt that exceeds it is a retryable failure. `AttemptTimeout` must be
less than `TotalRequestTimeout`, which is checked at startup; set
`Timeout.EnableAttemptTimeout = false` if you want only a total budget.

> When resilience is enabled, the underlying `HttpClient.Timeout` is set to
> `Timeout.InfiniteTimeSpan` so the total-timeout strategy is the single authority. Two independent
> clocks would race, and `HttpClient`'s own reports a bare cancellation the pipeline cannot tell
> apart from the caller cancelling. `HttpClientOptions.Timeout` therefore applies only when
> `Resilience.Enabled` is `false`.

Reading the response body happens *after* the pipeline returns, because responses complete at the
headers so large bodies can stream. Neither resilience timeout covers it, which is what
`ResponseReadTimeout` is for.

## Authentication and custom handlers

Registration returns the underlying `IHttpClientBuilder`, so the library needs to know nothing about
your auth scheme:

```csharp
builder.Services.AddResilientHttpClient("ExternalApi", options =>
    {
        options.BaseAddress = new Uri("https://api.example.com/");
    })
    .AddHttpMessageHandler<BearerTokenHandler>();

public sealed class BearerTokenHandler(ITokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
```

Handlers added this way run **inside** the resilience pipeline, so a token is re-acquired on every
retried attempt rather than once per logical call.

Credentials belong to the application, never to this library. Source them from Key Vault,
user-secrets or environment variables; nothing here persists or logs them.

## Logging, telemetry and troubleshooting

### Logging

Structured, with named placeholders. Event ids: 1000–1099 request lifecycle, 1100–1199 resilience
events (retries, timeouts, circuit transitions, shed requests), 1200–1299 failures. Request
completion logs at `Information`, retries and shedding at `Warning`, terminal failures at `Error`.

Set `EnableDetailedLogging = true` for per-request detail at `Debug`. It raises verbosity; it never
lifts redaction.

### Redaction

Values of these headers are replaced with `***` everywhere they could surface — logs, the
`Headers` dictionary on a result, and error messages:
`Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, `api-key`,
`Ocp-Apim-Subscription-Key`. Add your own:

```csharp
options.RedactedHeaders.Add("X-Internal-Signature");
```

Request and response bodies are **never** logged. A truncated response body appears on an error only
if you set `CaptureResponseBodyOnError = true`, which is off by default because response bodies
routinely contain personal data. URIs are logged with any embedded credentials stripped and query
values replaced, keeping parameter names so the log still says what was called.

### Metrics

On the meter `Airtricity.Common.Http`:

| Instrument | What it is for |
|---|---|
| `airtricity.http.client.results` | Completed operations by error category — the only signal that separates a local rate-limit rejection from a remote 429, or a total timeout from an attempt timeout |
| `airtricity.http.client.retries_suppressed` | Retries deliberately not attempted. A retry that never happens is invisible to Polly, yet silently un-retried POSTs are what operators need to see |
| `airtricity.http.client.body_replay_buffered` | Request bodies buffered for replay, and those that exceeded the limit |

Nothing else is emitted. Request duration, in-flight counts and connection counts come from the
platform's HTTP instrumentation; retries, circuit-breaker transitions, timeouts and rate-limiter
rejections come from Polly's own telemetry, which is wired up automatically and tagged with the
pipeline name `airtricity-http:<clientName>`. Re-emitting those would double-count them.

Trace context propagation is the platform's job and happens automatically. The library adds the
trace id to every result and error for correlation.

### Troubleshooting

| Symptom | Cause |
|---|---|
| `OptionsValidationException` at startup | A configuration rule failed; the message names the exact key, e.g. `ResilientHttpClients:ExternalApi:Resilience:Timeout:AttemptTimeout must be less than TotalRequestTimeout.` |
| `ArgumentException: No resilient HTTP client named 'x' is registered` | The name does not match a registration. The message lists the registered names. |
| POSTs are not being retried | Expected. See *Retry and idempotency*. |
| A large POST is not retried | Its body exceeded `MaxBufferedRequestBytes`, so it cannot be replayed. Look for the `Information` log naming the limit. |
| The circuit never opens | `MinimumThroughput` defaults to 100 requests per 30-second window. Lower it for a low-traffic client. |
| `RateLimited` when you expected a 429 | `RateLimited` is *your own* limiter. A remote 429 is `HttpStatus`. |
| A call hangs past `TotalRequestTimeout` | The body read is not covered by it. Check `ResponseReadTimeout`. |
| Timeouts report `AttemptTimeout`, not `Timeout` | Every attempt timed out and retries ran out before the total budget did. Both are timeouts; the category says which clock won. |

## Building, testing and packing

From the repository root:

```bash
dotnet restore Airtricity.Common.sln --interactive false
dotnet build   Airtricity.Common.sln -c Release --no-restore -p:Version=2.0.999
dotnet test    Airtricity.Common.sln -c Release --no-build   -p:Version=2.0.999
dotnet pack    Airtricity.Common.sln -c Release --no-build   -p:Version=2.0.999 -o ./artifacts
```

Never put `<Version>` or `<PackageVersion>` in the `.csproj` — `Major.Minor` come from
`<VersionPrefix>` in the root `Directory.Build.props` and the pipeline supplies the patch. See
`PIPELINE-GUIDE.md`.

The test suite makes no network calls and contains no sleeps: responses come from a scriptable fake
handler, blocking is done with task gates, and every timeout, backoff and break duration elapses on
a `FakeTimeProvider`.

## License

MIT. See the repository root.
