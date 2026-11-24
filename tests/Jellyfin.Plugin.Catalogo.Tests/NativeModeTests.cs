using Jellyfin.Plugin.Catalogo.Configuration;
using Jellyfin.Plugin.Catalogo.Playback;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class NativeModeTests
{
    [Fact]
    public void NativeMode_IsOffByDefault()
        => Assert.False(new PluginConfiguration().NativeMode);

    [Theory]
    [InlineData("MediaBrowser Client=\"Medusa\", Device=\"AndroidTV\"", "")]
    [InlineData("", "Medusa")]
    [InlineData("MediaBrowser Client=Medusa, Device=\"AndroidTV\"", "")]
    public void MedusaClient_IsDetectedFromJellyfinHeaders(
        string authorization,
        string client)
    {
        var headers = new HeaderDictionary();
        if (!string.IsNullOrEmpty(authorization))
        {
            headers["Authorization"] = authorization;
        }

        if (!string.IsNullOrEmpty(client))
        {
            headers["X-Emby-Client"] = client;
        }

        Assert.True(CatalogoMediaSourceManager.IsMedusaClient(headers));
    }

    [Theory]
    [InlineData("MediaBrowser Client=\"Jellyfin iOS\", Device=\"iPhone\"", "Jellyfin iOS")]
    [InlineData("MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\"", "Jellyfin Web")]
    [InlineData("", "")]
    public void NativeClients_AreNotTreatedAsMedusa(
        string authorization,
        string client)
    {
        var headers = new HeaderDictionary();
        if (!string.IsNullOrEmpty(authorization))
        {
            headers["Authorization"] = authorization;
        }

        if (!string.IsNullOrEmpty(client))
        {
            headers["X-Emby-Client"] = client;
        }

        Assert.False(CatalogoMediaSourceManager.IsMedusaClient(headers));
    }

    [Fact]
    public void MedusaClient_IsDetectedFromEmbyAuthorizationAlias()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Medusa\", Device=\"AndroidTV\""
        };

        Assert.True(CatalogoMediaSourceManager.IsMedusaClient(headers));
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    public void ShouldPrepare_KeepsLegacyPathAndAddsNativeOnly(
        bool hasVideo,
        bool nativeMode,
        bool compatibilityClient,
        bool expected)
    {
        Assert.Equal(
            expected,
            CatalogoMediaSourceManager.ShouldPrepare(
                hasVideo, nativeMode, compatibilityClient));
    }
}
