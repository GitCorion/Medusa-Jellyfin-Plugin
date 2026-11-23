using System.Globalization;
using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Catalogo.Playback;

/// <summary>
/// Maps the media information returned by the Catalogo proxy to Jellyfin streams.
/// </summary>
public static class CatalogoMediaStreams
{
    /// <summary>
    /// Builds Jellyfin media streams from a Catalogo media-info payload.
    /// </summary>
    /// <param name="info">The media-info JSON.</param>
    /// <returns>The streams, video first.</returns>
    public static IReadOnlyList<MediaStream> FromJson(JsonElement info)
    {
        var streams = new List<MediaStream>();
        if (info.TryGetProperty("video", out var video)
            && video.ValueKind == JsonValueKind.Object)
        {
            var stream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = GetInt(video, "index") ?? 0,
                Codec = GetString(video, "codec"),
                Width = GetInt(video, "width"),
                Height = GetInt(video, "height"),
                BitDepth = GetInt(video, "bit_depth"),
                Profile = GetString(video, "profile"),
                IsDefault = true,
            };
            streams.Add(stream);
        }

        var firstAudio = true;
        if (info.TryGetProperty("audio", out var audio)
            && audio.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in audio.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = GetInt(item, "index") ?? streams.Count,
                    Codec = GetString(item, "codec"),
                    Channels = GetInt(item, "channels"),
                    Language = GetString(item, "language"),
                    Title = GetString(item, "title"),
                    IsDefault = firstAudio,
                });
                firstAudio = false;
            }
        }

        if (info.TryGetProperty("subtitles", out var subtitles)
            && subtitles.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in subtitles.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = GetInt(item, "index") ?? streams.Count,
                    Codec = GetString(item, "codec"),
                    Language = GetString(item, "language"),
                    Title = GetString(item, "title"),
                    IsDefault = false,
                    IsForced = false,
                });
            }
        }

        return streams;
    }

    /// <summary>
    /// Gets the container name Jellyfin expects for the media source.
    /// </summary>
    /// <param name="info">The media-info JSON.</param>
    /// <returns>A container name such as <c>mkv</c>.</returns>
    public static string? Container(JsonElement info)
    {
        var container = GetString(info, "container");
        if (string.IsNullOrWhiteSpace(container))
        {
            return null;
        }

        var lower = container.ToLowerInvariant();
        if (lower.Contains("matroska", StringComparison.Ordinal)
            || lower.Contains("webm", StringComparison.Ordinal))
        {
            return "mkv";
        }

        var separator = lower.IndexOf(',', StringComparison.Ordinal);
        return separator > 0 ? lower[..separator] : lower;
    }

    /// <summary>
    /// Gets the duration in Jellyfin ticks, if known.
    /// </summary>
    /// <param name="info">The media-info JSON.</param>
    /// <returns>Ticks or <c>null</c>.</returns>
    public static long? DurationTicks(JsonElement info)
    {
        if (!info.TryGetProperty("duration_seconds", out var duration)
            || duration.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var seconds = duration.GetDouble();
        if (seconds <= 0)
        {
            return null;
        }

        return (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Gets the media size in bytes, if known.
    /// </summary>
    /// <param name="info">The media-info JSON.</param>
    /// <returns>Size in bytes or <c>null</c>.</returns>
    public static long? MediaSize(JsonElement info)
    {
        if (!info.TryGetProperty("size_bytes", out var size)
            || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var bytes))
        {
            return null;
        }

        return bytes > 0 ? bytes : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}
