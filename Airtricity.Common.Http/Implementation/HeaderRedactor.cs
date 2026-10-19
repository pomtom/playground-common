using System.Net.Http.Headers;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Replaces the values of configured header names before anything reaches a log or an error.
/// </summary>
/// <remarks>
/// Redaction is applied where headers are read out, not where they are set, so there is no path
/// that logs a header the caller never saw redacted. Header names are compared case-insensitively
/// because HTTP header names are case-insensitive.
/// </remarks>
internal sealed class HeaderRedactor
{
    internal const string RedactedValue = "***";

    private readonly HashSet<string> _redacted;

    internal HeaderRedactor(IEnumerable<string> redactedHeaderNames)
    {
        ArgumentNullException.ThrowIfNull(redactedHeaderNames);
        _redacted = new HashSet<string>(redactedHeaderNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when values of <paramref name="headerName"/> must never be revealed.</summary>
    internal bool IsRedacted(string headerName) => _redacted.Contains(headerName);

    /// <summary>
    /// Projects header collections into a plain dictionary, substituting redacted values.
    /// </summary>
    /// <param name="headers">Response or trailing headers. May be null.</param>
    /// <param name="contentHeaders">Content headers, which live on the content, not the response.</param>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Project(
        HttpHeaders? headers,
        HttpHeaders? contentHeaders = null)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        Add(headers, result);
        Add(contentHeaders, result);

        return result;
    }

    private void Add(HttpHeaders? source, Dictionary<string, IReadOnlyList<string>> target)
    {
        if (source is null)
        {
            return;
        }

        foreach (var header in source)
        {
            target[header.Key] = IsRedacted(header.Key)
                ? new[] { RedactedValue }
                : header.Value.ToArray();
        }
    }
}
