using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Extracts the Catalogo key and release from a Jellyfin .strm file or play URL.
/// </summary>
public static class StrmResolver
{
    /// <summary>
    /// Parses a .strm file.
    /// </summary>
    /// <param name="path">Path to the .strm file.</param>
    /// <returns>The key and optional release id.</returns>
    public static (string? Key, int? ReleaseId) Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }

        var line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();
        return ParseUrl(line);
    }

    /// <summary>
    /// Parses a Catalogo play URL (from a .strm or MediaSourceInfo.Path).
    /// </summary>
    /// <param name="url">Catalogo URL.</param>
    /// <returns>The key and optional release id.</returns>
    public static (string? Key, int? ReleaseId) ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (null, null);
        }

        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return (null, null);
        }

        var query = QueryHelpers.ParseQuery(url[queryIndex..]);
        var key = query.TryGetValue("key", out var keys)
            ? keys.FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, null);
        }

        int? releaseId = null;
        if (query.TryGetValue("release", out var releases)
            && int.TryParse(releases.FirstOrDefault(), out var parsed))
        {
            releaseId = parsed;
        }

        return (key, releaseId);
    }
}
