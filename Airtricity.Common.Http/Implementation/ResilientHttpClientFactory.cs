using System.Collections.Concurrent;
using Airtricity.Common.Http.Abstractions;
using Airtricity.Common.Http.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Default <see cref="IResilientHttpClientFactory"/>. Wraps the named
/// <see cref="HttpClient"/> instances that <see cref="IHttpClientFactory"/> manages.
/// </summary>
/// <remarks>
/// Wrappers are cached per name because they are cheap, immutable and stateless; the underlying
/// <see cref="HttpClient"/> is still resolved from <see cref="IHttpClientFactory"/> on every call,
/// so handler rotation and connection pooling remain the factory's business.
/// </remarks>
internal sealed class ResilientHttpClientFactory : IResilientHttpClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HttpClientOptions> _optionsMonitor;
    private readonly ILogger<ResilientHttpClient> _logger;
    private readonly HttpClientMetrics _metrics;
    private readonly RegisteredClientNames _registeredNames;
    private readonly ConcurrentDictionary<string, IResilientHttpClient> _cache = new(StringComparer.Ordinal);

    public ResilientHttpClientFactory(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HttpClientOptions> optionsMonitor,
        ILogger<ResilientHttpClient> logger,
        HttpClientMetrics metrics,
        RegisteredClientNames registeredNames)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _registeredNames = registeredNames ?? throw new ArgumentNullException(nameof(registeredNames));
    }

    /// <inheritdoc />
    public IResilientHttpClient CreateClient(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_registeredNames.Contains(name))
        {
            throw new ArgumentException(
                $"No resilient HTTP client named '{name}' is registered. Register it with AddResilientHttpClient(\"{name}\", ...). " +
                $"Registered names: {_registeredNames.Describe()}.",
                nameof(name));
        }

        return Resolve(name);
    }

    /// <inheritdoc />
    public bool TryCreateClient(string name, out IResilientHttpClient? client)
    {
        if (string.IsNullOrWhiteSpace(name) || !_registeredNames.Contains(name))
        {
            client = null;
            return false;
        }

        client = Resolve(name);
        return true;
    }

    private IResilientHttpClient Resolve(string name) =>
        _cache.GetOrAdd(
            name,
            static (key, state) => new ResilientHttpClient(
                key,
                state._httpClientFactory,
                state._optionsMonitor.Get(key),
                state._logger,
                state._metrics),
            this);
}

/// <summary>
/// The set of client names registered at startup, so an unknown name fails with a useful message
/// instead of silently producing a default client.
/// </summary>
internal sealed class RegisteredClientNames
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    internal void Add(string name) => _names.Add(name);

    internal bool Contains(string name) => _names.Contains(name);

    internal string Describe() => _names.Count == 0 ? "(none)" : string.Join(", ", _names.OrderBy(n => n, StringComparer.Ordinal));
}
