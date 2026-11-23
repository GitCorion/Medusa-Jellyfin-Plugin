using System.Text.Json;
using Jellyfin.Plugin.Catalogo.Playback;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class CatalogoMediaStreamsTests
{
    private const string Sample = """
        {
          "source": "ffprobe",
          "container": "matroska,webm",
          "duration_seconds": 3224.544,
          "size_bytes": 4664225386,
          "video": {
            "index": 0,
            "codec": "h264",
            "width": 1918,
            "height": 872,
            "bit_depth": 8,
            "profile": "High"
          },
          "audio": [
            {"index": 1, "codec": "eac3", "channels": 6, "language": "spa"},
            {"index": 2, "codec": "eac3", "channels": 6, "language": "eng"}
          ],
          "subtitles": [
            {"index": 3, "codec": "subrip", "language": "spa"}
          ]
        }
        """;

    [Fact]
    public void MapsVideoAudioAndSubtitles()
    {
        using var document = JsonDocument.Parse(Sample);
        var streams = CatalogoMediaStreams.FromJson(document.RootElement);

        Assert.Equal(4, streams.Count);
        var video = streams[0];
        Assert.Equal(MediaStreamType.Video, video.Type);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1918, video.Width);
        Assert.Equal(872, video.Height);
        Assert.True(video.IsDefault);

        var audio = streams[1];
        Assert.Equal(MediaStreamType.Audio, audio.Type);
        Assert.Equal("eac3", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.Equal("spa", audio.Language);
        Assert.True(audio.IsDefault);

        Assert.False(streams[2].IsDefault);
        Assert.Equal(MediaStreamType.Subtitle, streams[3].Type);
        Assert.Equal("subrip", streams[3].Codec);
        Assert.Equal("spa", streams[3].Language);
    }

    [Fact]
    public void NormalizesMatroskaContainerAndDuration()
    {
        using var document = JsonDocument.Parse(Sample);
        Assert.Equal("mkv", CatalogoMediaStreams.Container(document.RootElement));
        Assert.Equal(
            (long)Math.Round(3224.544 * TimeSpan.TicksPerSecond),
            CatalogoMediaStreams.DurationTicks(document.RootElement));
        Assert.Equal(
            4664225386,
            CatalogoMediaStreams.MediaSize(document.RootElement));
    }

    [Fact]
    public void EmptyPayloadIsSafe()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.Empty(CatalogoMediaStreams.FromJson(document.RootElement));
        Assert.Null(CatalogoMediaStreams.Container(document.RootElement));
        Assert.Null(CatalogoMediaStreams.DurationTicks(document.RootElement));
    }
}
