using System.Net;
using System.Text;
using Jellyfin.Plugin.Catalogo.Services;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class CatalogoProxyClientTests
{
    [Fact]
    public async Task StatusForwardsStreamGeneration()
    {
        const string body = """
            {
              "key": "movie:tmdb:1",
              "stream_generation": 7,
              "stream_switched_at": "2026-11-20T08:00:00Z",
              "stream_switch_reason": "slow_swarm",
              "stream_ready": true
            }
            """;
        var client = new CatalogoProxyClient(
            new HttpClient(new StubHandler(body)));

        var status = await client.GetStatusAsync(
            "movie:tmdb:1", null, CancellationToken.None);

        Assert.Equal(7, status.GetProperty("stream_generation").GetInt64());
        Assert.Equal(
            "slow_swarm",
            status.GetProperty("stream_switch_reason").GetString());
        Assert.True(status.GetProperty("stream_ready").GetBoolean());
    }

    [Fact]
    public async Task MediaInfo_ForwardsKeyAndRelease()
    {
        var handler = new CapturingHandler();
        var client = new CatalogoProxyClient(new HttpClient(handler));

        await client.GetMediaInfoAsync(
            "movie:tmdb:1", 109, CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        var url = handler.Request.RequestUri!.ToString();
        Assert.Contains("/media-info?key=movie%3Atmdb%3A1", url);
        Assert.Contains("release=109", url);
    }

    [Fact]
    public async Task Prepare_ForwardsReleaseAndRuntime()
    {
        var handler = new CapturingHandler();
        var client = new CatalogoProxyClient(new HttpClient(handler));

        await client.PrepareAsync(
            "movie:tmdb:1", 109, 3224, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        var url = handler.Request.RequestUri!.ToString();
        Assert.Contains("/prepare?key=movie%3Atmdb%3A1", url);
        Assert.Contains("release=109", url);
        Assert.Contains("runtime=3224", url);
    }

    [Fact]
    public async Task Lease_UsesPostAndEncodedSession()
    {
        var handler = new CapturingHandler();
        var client = new CatalogoProxyClient(new HttpClient(handler));

        await client.LeaseAsync(
            "movie:tmdb:1", "medusa:device-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Contains(
            "session=medusa%3Adevice-1",
            handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReleaseLease_UsesDelete()
    {
        var handler = new CapturingHandler();
        var client = new CatalogoProxyClient(new HttpClient(handler));

        await client.ReleaseLeaseAsync(
            "movie:tmdb:1", "medusa:device-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.Contains(
            "session=medusa%3Adevice-1",
            handler.Request.RequestUri!.ToString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{}", Encoding.UTF8, "application/json")
                });
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        body, Encoding.UTF8, "application/json")
                });
        }
    }
}
