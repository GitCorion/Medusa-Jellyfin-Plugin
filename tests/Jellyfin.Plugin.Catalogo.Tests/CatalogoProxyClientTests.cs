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
