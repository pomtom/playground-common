using System.Text;

namespace Airtricity.Common.Http.Implementation;

/// <summary>
/// Produces a URI string that is safe to log.
/// </summary>
/// <remarks>
/// Two things in a URI leak credentials. Userinfo (<c>https://user:password@host/</c>) carries them
/// outright, and query strings routinely carry tokens, keys and personal data as parameter values.
/// Both are stripped here, keeping the parts that make a log useful: scheme, host, port and path,
/// plus the query parameter names.
/// </remarks>
internal static class UriSanitizer
{
    internal const string RedactedValue = "***";

    /// <summary>
    /// Returns <paramref name="uri"/> with any userinfo removed, keeping the query string intact.
    /// </summary>
    /// <param name="uri">URI to strip. May be null.</param>
    /// <remarks>
    /// Used for the URI stored on an <see cref="Models.HttpError"/>. Credentials embedded in a URI
    /// are never legitimate to expose, so they go unconditionally; the query string stays because a
    /// caller inspecting the error usually needs to know which resource was requested. The fully
    /// sanitized form, with query values replaced too, is what gets logged.
    /// </remarks>
    internal static Uri? StripCredentials(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri;
    }

    /// <summary>
    /// Renders <paramref name="uri"/> with userinfo removed and every query value replaced.
    /// </summary>
    /// <param name="uri">URI to sanitize. May be null.</param>
    /// <returns>A safe string, or an empty string when <paramref name="uri"/> is null.</returns>
    internal static string ToSafeString(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            // A relative URI has no userinfo, but it can still carry a query string.
            var raw = uri.OriginalString;
            var separator = raw.IndexOf('?', StringComparison.Ordinal);
            return separator < 0 ? raw : string.Concat(raw.AsSpan(0, separator), RedactQuery(raw[separator..]));
        }

        var builder = new StringBuilder()
            .Append(uri.Scheme)
            .Append("://")
            .Append(uri.Authority)   // Authority excludes userinfo, unlike Host plus UserInfo.
            .Append(uri.AbsolutePath);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            builder.Append(RedactQuery(uri.Query));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rewrites a query string, keeping parameter names and replacing every value.
    /// </summary>
    /// <param name="query">Query string, with or without its leading question mark.</param>
    private static string RedactQuery(string query)
    {
        var withoutPrefix = query.StartsWith('?') ? query[1..] : query;
        if (withoutPrefix.Length == 0)
        {
            return "?";
        }

        var builder = new StringBuilder("?");
        var first = true;

        foreach (var pair in withoutPrefix.Split('&'))
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;

            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                // A bare flag carries no value, so there is nothing to hide.
                builder.Append(pair);
            }
            else
            {
                builder.Append(pair.AsSpan(0, equals + 1)).Append(RedactedValue);
            }
        }

        return builder.ToString();
    }
}
