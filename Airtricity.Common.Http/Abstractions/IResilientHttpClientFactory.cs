namespace Airtricity.Common.Http.Abstractions;

/// <summary>
/// Resolves a registered <see cref="IResilientHttpClient"/> by name, for code that talks to several
/// downstream APIs and picks between them at runtime.
/// </summary>
/// <remarks>
/// Prefer injecting <see cref="IResilientHttpClient"/> directly, or a typed client, when the name is
/// known at compile time.
/// </remarks>
public interface IResilientHttpClientFactory
{
    /// <summary>Returns the client registered under <paramref name="name"/>.</summary>
    /// <param name="name">Name the client was registered with.</param>
    /// <exception cref="ArgumentException">
    /// No client is registered under that name. Failing loudly beats returning a client with
    /// default settings and no base address, which would only fail later and less clearly.
    /// </exception>
    IResilientHttpClient CreateClient(string name);

    /// <summary>Returns the client registered under <paramref name="name"/>, if there is one.</summary>
    /// <param name="name">Name the client was registered with.</param>
    /// <param name="client">The resolved client, or null.</param>
    /// <returns>True when a client with that name is registered.</returns>
    bool TryCreateClient(string name, out IResilientHttpClient? client);
}
