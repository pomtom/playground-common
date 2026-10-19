using Airtricity.Common.Http.Abstractions;
using Airtricity.Common.Http.Configuration;
using Airtricity.Common.Http.Implementation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace Airtricity.Common.Http.Extensions;

/// <summary>Dependency injection registration for the Airtricity resilient HTTP client library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named resilient HTTP client, configuring
    /// <see cref="HttpClientOptions"/> via a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Name used to resolve the client and to scope its configuration.</param>
    /// <param name="configureOptions">Configures this client's options.</param>
    /// <returns>
    /// The underlying <see cref="IHttpClientBuilder"/>, so callers can chain their own
    /// authentication or delegating handlers onto this client.
    /// </returns>
    public static IHttpClientBuilder AddResilientHttpClient(
        this IServiceCollection services,
        string name,
        Action<HttpClientOptions> configureOptions)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.Configure(name, configureOptions);
        return AddClientCore(services, name);
    }

    /// <summary>
    /// Registers a named resilient HTTP client, binding
    /// <see cref="HttpClientOptions"/> from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Name used to resolve the client and to scope its configuration.</param>
    /// <param name="configuration">Configuration root or section to bind from.</param>
    /// <param name="sectionName">
    /// Section holding this client's settings. Defaults to
    /// "ResilientHttpClients:&lt;name&gt;".
    /// </param>
    /// <returns>The underlying <see cref="IHttpClientBuilder"/> for further handler chaining.</returns>
    public static IHttpClientBuilder AddResilientHttpClient(
        this IServiceCollection services,
        string name,
        IConfiguration configuration,
        string? sectionName = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var section = sectionName ?? $"{HttpClientOptions.SectionName}:{name}";
        services.Configure<HttpClientOptions>(name, configuration.GetSection(section));
        return AddClientCore(services, name);
    }

    /// <summary>
    /// Registers every client defined under a configuration section, so an application can add a
    /// downstream API by editing configuration alone.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration root or section to bind from.</param>
    /// <param name="sectionName">Section holding one child per client. Defaults to "ResilientHttpClients".</param>
    public static IServiceCollection AddResilientHttpClients(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = HttpClientOptions.SectionName)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        var root = configuration.GetSection(sectionName);

        foreach (var child in root.GetChildren())
        {
            services.Configure<HttpClientOptions>(child.Key, child);
            AddClientCore(services, child.Key);
        }

        return AddCoreServices(services);
    }

    /// <summary>
    /// Registers a typed client. <typeparamref name="TClient"/> receives an
    /// <see cref="IResilientHttpClient"/> named after itself, so it needs no knowledge of this
    /// library's registration.
    /// </summary>
    /// <typeparam name="TClient">The typed client to register.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Configures the underlying client's options.</param>
    /// <returns>The underlying <see cref="IHttpClientBuilder"/> for further handler chaining.</returns>
    public static IHttpClientBuilder AddResilientHttpClient<TClient>(
        this IServiceCollection services,
        Action<HttpClientOptions> configureOptions)
        where TClient : class
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        var name = typeof(TClient).Name;
        var builder = AddResilientHttpClient(services, name, configureOptions);

        services.AddTransient(provider => ActivatorUtilities.CreateInstance<TClient>(
            provider,
            provider.GetRequiredService<IResilientHttpClientFactory>().CreateClient(name)));

        return builder;
    }

    private static IHttpClientBuilder AddClientCore(IServiceCollection services, string name)
    {
        AddCoreServices(services);

        // Recorded so the factory can reject an unknown name with a message that lists what is
        // actually registered, instead of handing back a client with no base address.
        var registeredNames = (RegisteredClientNames)services
            .First(descriptor => descriptor.ServiceType == typeof(RegisteredClientNames))
            .ImplementationInstance!;
        registeredNames.Add(name);

        // ValidateOnStart only, with no inline rule: every check lives in
        // HttpClientOptionsValidator so there is one place to read and one place to change.
        services.AddOptions<HttpClientOptions>(name).ValidateOnStart();

        var builder = services.AddHttpClient(name, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<HttpClientOptions>>().Get(name);

            if (options.BaseAddress is not null)
            {
                client.BaseAddress = options.BaseAddress;
            }

            // When resilience is on, the total-timeout strategy is the single authority. Leaving
            // HttpClient's own timeout active would race it and surface a bare cancellation the
            // pipeline cannot classify.
            client.Timeout = options.Resilience.Enabled ? Timeout.InfiniteTimeSpan : options.Timeout;

            if (!string.IsNullOrWhiteSpace(options.UserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            }
        });

        builder.AddResilienceHandler($"airtricity-http-{name}", (pipelineBuilder, context) =>
        {
            var provider = context.ServiceProvider;
            var options = provider.GetRequiredService<IOptionsMonitor<HttpClientOptions>>().Get(name);

            ResiliencePipelineConfigurator.Configure(
                pipelineBuilder,
                options,
                name,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger($"Airtricity.Common.Http.{name}"),
                provider.GetRequiredService<HttpClientMetrics>(),
                provider.GetService<CircuitBreakerManualControl>(),
                provider.GetService<CircuitBreakerStateProvider>());
        });

        // Added after the resilience handler so it sits inside it, where it can see each attempt.
        builder.AddHttpMessageHandler(() => new AttemptTrackingHandler());

        return builder;
    }

    private static IServiceCollection AddCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton(new RegisteredClientNames());
        services.TryAddSingleton<HttpClientMetrics>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IResilientHttpClientFactory, ResilientHttpClientFactory>();
        services.TryAddSingleton<IValidateOptions<HttpClientOptions>, HttpClientOptionsValidator>();
        return services;
    }
}

/// <summary>
/// Validates a client's options at startup, so a misconfiguration is a failure to start rather than
/// a surprise on the first request.
/// </summary>
internal sealed class HttpClientOptionsValidator : IValidateOptions<HttpClientOptions>
{
    public ValidateOptionsResult Validate(string? name, HttpClientOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("HttpClientOptions must not be null.");
        }

        var prefix = $"{HttpClientOptions.SectionName}:{name ?? "(default)"}";
        var failures = new List<string>();

        if (options.BaseAddress is not null && !options.BaseAddress.IsAbsoluteUri)
        {
            failures.Add($"{prefix}:BaseAddress must be an absolute URI.");
        }
        if (string.IsNullOrWhiteSpace(options.DefaultMediaType))
        {
            failures.Add($"{prefix}:DefaultMediaType is required.");
        }
        if (options.MaxErrorBodyCharacters < 0)
        {
            failures.Add($"{prefix}:MaxErrorBodyCharacters must be greater than or equal to 0.");
        }
        if (options.MaxBufferedRequestBytes <= 0)
        {
            failures.Add($"{prefix}:MaxBufferedRequestBytes must be greater than 0.");
        }
        if (options.ResponseReadTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:ResponseReadTimeout must be greater than TimeSpan.Zero.");
        }

        ValidateTimeouts(options, prefix, failures);
        ValidateRetry(options, prefix, failures);
        ValidateCircuitBreaker(options, prefix, failures);
        ValidateLimiters(options, prefix, failures);

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateTimeouts(HttpClientOptions options, string prefix, List<string> failures)
    {
        var timeout = options.Resilience.Timeout;

        if (timeout.EnableAttemptTimeout && timeout.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Resilience:Timeout:AttemptTimeout must be greater than TimeSpan.Zero.");
        }
        if (timeout.TotalRequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Resilience:Timeout:TotalRequestTimeout must be greater than TimeSpan.Zero.");
        }
        if (timeout.EnableAttemptTimeout
            && timeout.AttemptTimeout > TimeSpan.Zero
            && timeout.TotalRequestTimeout > TimeSpan.Zero
            && timeout.AttemptTimeout >= timeout.TotalRequestTimeout)
        {
            // An attempt budget at least as large as the total budget means the attempt timeout can
            // never fire, so a hung attempt would consume the entire call.
            failures.Add($"{prefix}:Resilience:Timeout:AttemptTimeout must be less than TotalRequestTimeout.");
        }
        if (!options.Resilience.Enabled && options.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Timeout must be greater than TimeSpan.Zero when resilience is disabled.");
        }
    }

    private static void ValidateRetry(HttpClientOptions options, string prefix, List<string> failures)
    {
        var retry = options.Resilience.Retry;

        if (retry.MaxRetryAttempts < 0)
        {
            failures.Add($"{prefix}:Resilience:Retry:MaxRetryAttempts must be greater than or equal to 0.");
        }
        if (retry.Delay < TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Resilience:Retry:Delay must be greater than or equal to TimeSpan.Zero.");
        }
        if (retry.MaxDelay < retry.Delay)
        {
            failures.Add($"{prefix}:Resilience:Retry:MaxDelay must be greater than or equal to Delay.");
        }
        if (retry.Enabled && retry.MaxRetryAttempts > 0 && retry.RetryableMethods.Count == 0 && !retry.RetryUnsafeMethods)
        {
            failures.Add($"{prefix}:Resilience:Retry:RetryableMethods must contain at least one method, or RetryUnsafeMethods must be true.");
        }
    }

    private static void ValidateCircuitBreaker(HttpClientOptions options, string prefix, List<string> failures)
    {
        var circuitBreaker = options.Resilience.CircuitBreaker;
        if (!circuitBreaker.Enabled)
        {
            return;
        }

        if (circuitBreaker.FailureRatio is <= 0 or > 1)
        {
            failures.Add($"{prefix}:Resilience:CircuitBreaker:FailureRatio must be greater than 0 and less than or equal to 1.");
        }
        if (circuitBreaker.MinimumThroughput < 2)
        {
            failures.Add($"{prefix}:Resilience:CircuitBreaker:MinimumThroughput must be greater than or equal to 2.");
        }
        if (circuitBreaker.SamplingDuration <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Resilience:CircuitBreaker:SamplingDuration must be greater than TimeSpan.Zero.");
        }
        if (circuitBreaker.BreakDuration <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}:Resilience:CircuitBreaker:BreakDuration must be greater than TimeSpan.Zero.");
        }
    }

    private static void ValidateLimiters(HttpClientOptions options, string prefix, List<string> failures)
    {
        var rateLimiter = options.RateLimiter;

        if (rateLimiter.Enabled)
        {
            if (rateLimiter.PermitLimit < 1)
            {
                failures.Add($"{prefix}:RateLimiter:PermitLimit must be greater than or equal to 1.");
            }
            if (rateLimiter.Window <= TimeSpan.Zero)
            {
                failures.Add($"{prefix}:RateLimiter:Window must be greater than TimeSpan.Zero.");
            }
            if (rateLimiter.SegmentsPerWindow < 1)
            {
                failures.Add($"{prefix}:RateLimiter:SegmentsPerWindow must be greater than or equal to 1.");
            }
            if (rateLimiter.QueueLimit < 0)
            {
                failures.Add($"{prefix}:RateLimiter:QueueLimit must be greater than or equal to 0.");
            }
        }

        var concurrency = rateLimiter.Concurrency;
        if (!concurrency.Enabled)
        {
            return;
        }

        if (concurrency.PermitLimit < 1)
        {
            failures.Add($"{prefix}:RateLimiter:Concurrency:PermitLimit must be greater than or equal to 1.");
        }
        if (concurrency.QueueLimit < 0)
        {
            failures.Add($"{prefix}:RateLimiter:Concurrency:QueueLimit must be greater than or equal to 0.");
        }
    }
}
