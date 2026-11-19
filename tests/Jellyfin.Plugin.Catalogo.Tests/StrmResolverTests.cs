using Jellyfin.Plugin.Catalogo.Services;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class StrmResolverTests
{
    [Fact]
    public void ParsesKeyAndRelease()
    {
        var path = Path.GetTempFileName() + ".strm";
        try
        {
            File.WriteAllText(
                path,
                "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A1447420&release=109&download=0\n");
            var (key, release) = StrmResolver.Parse(path);
            Assert.Equal("movie:tmdb:1447420", key);
            Assert.Equal(109, release);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsNonCatalogStrm()
    {
        var path = Path.GetTempFileName() + ".strm";
        try
        {
            File.WriteAllText(path, "http://127.0.0.1:8773/healthz\n");
            var (key, release) = StrmResolver.Parse(path);
            Assert.Null(key);
            Assert.Null(release);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsNonStrmPath()
    {
        var (key, release) = StrmResolver.Parse("/tmp/movie.mkv");
        Assert.Null(key);
        Assert.Null(release);
    }
}
