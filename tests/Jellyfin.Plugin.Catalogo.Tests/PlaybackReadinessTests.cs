using System.Text.Json;
using Jellyfin.Plugin.Catalogo.Playback;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class PlaybackReadinessTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Theory]
    [InlineData("""{"progress": 1.0, "state": "DOWNLOADING"}""", true)]
    [InlineData("""{"progress": 0.42, "state": "DOWNLOADING"}""", false)]
    [InlineData("""{"progress": 0.0, "state": "COLD"}""", false)]
    [InlineData("""{"state": "SEEDING"}""", true)]
    [InlineData("""{"state": "AVAILABLE"}""", true)]
    [InlineData("""{"state": "SEEDING_LOCAL"}""", true)]
    [InlineData("""{"state": "COLD"}""", false)]
    [InlineData("""{"state": "UNAVAILABLE"}""", false)]
    [InlineData("""{}""", true)]
    public void IsComplete_ReflectsStatus(string raw, bool expected)
        => Assert.Equal(expected, CatalogoMediaSourceManager.IsComplete(Json(raw)));

    [Fact]
    public void HideTextSubtitles_KeepsVideoAndBitmapSubtitles()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams = new List<MediaStream>
            {
                new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
                new() { Index = 1, Type = MediaStreamType.Audio, Codec = "eac3" },
                new()
                {
                    Index = 2,
                    Type = MediaStreamType.Subtitle,
                    Codec = "subrip"
                },
                new()
                {
                    Index = 3,
                    Type = MediaStreamType.Subtitle,
                    Codec = "pgssub"
                }
            }
        };

        var result = CatalogoMediaSourceManager.HideTextSubtitles(new[] { source });

        var streams = Assert.Single(result).MediaStreams;
        Assert.Equal(3, streams.Count);
        Assert.DoesNotContain(
            streams,
            stream => stream.Type == MediaStreamType.Subtitle
                && stream.IsTextSubtitleStream);
        Assert.Contains(streams, stream => stream.Codec == "pgssub");
    }

    [Fact]
    public void HideTextSubtitles_LeavesSourcesWithoutSubtitlesUntouched()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams = new List<MediaStream>
            {
                new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" }
            }
        };

        var result = CatalogoMediaSourceManager.HideTextSubtitles(new[] { source });

        Assert.Single(Assert.Single(result).MediaStreams);
    }
}
