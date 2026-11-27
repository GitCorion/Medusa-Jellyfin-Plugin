using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Catalogo.Api;
using Jellyfin.Plugin.Catalogo.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

/// <summary>
/// Tests aditivos del almacén Medusa y del proxy de TMDB. No tocan el flujo
/// de reproducción.
/// </summary>
public class MedusaSyncTests
{
    [Fact]
    public async Task Store_UpsertGetUpdateAndDelete()
    {
        var path = NewPath();
        try
        {
            var store = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var userId = Guid.NewGuid().ToString();

            Assert.Null(await store.GetAsync(userId));

            var (created, conflict) = await store.UpsertAsync(
                userId, Credentials("token-1", "client-1"), null);
            Assert.False(conflict);
            Assert.Equal(1, created.Revision);
            Assert.False(string.IsNullOrWhiteSpace(created.UpdatedAtUtc));

            var read = await store.GetAsync(userId);
            Assert.NotNull(read);
            Assert.Equal("token-1", read!.AccessToken);
            Assert.Equal("client-1", read.ClientId);
            Assert.Equal(1, read.Revision);

            var updatedInput = Credentials("token-2", "client-1");
            var (updated, secondConflict) = await store.UpsertAsync(
                userId, updatedInput, baseRevision: 1);
            Assert.False(secondConflict);
            Assert.Equal(2, updated.Revision);
            Assert.Equal("token-2", (await store.GetAsync(userId))!.AccessToken);
            // El store no debe mutar el objeto de entrada.
            Assert.Equal(0, updatedInput.Revision);

            Assert.True(await store.DeleteAsync(userId, baseRevision: 2));
            Assert.Null(await store.GetAsync(userId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Store_ConflictingRevision_DoesNotWrite()
    {
        var path = NewPath();
        try
        {
            var store = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var userId = Guid.NewGuid().ToString();
            await store.UpsertAsync(userId, Credentials("token-1", "client-1"), null);

            var (current, conflict) = await store.UpsertAsync(
                userId, Credentials("token-2", "client-1"), baseRevision: 99);

            Assert.True(conflict);
            Assert.Equal(1, current.Revision);
            Assert.Equal("token-1", current.AccessToken);
            Assert.Equal("token-1", (await store.GetAsync(userId))!.AccessToken);
            Assert.False(await store.DeleteAsync(userId, baseRevision: 99));
            Assert.NotNull(await store.GetAsync(userId));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Store_PersistsAcrossInstancesWithSamePath()
    {
        var path = NewPath();
        try
        {
            var userId = Guid.NewGuid().ToString();
            var first = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            await first.UpsertAsync(userId, Credentials("token-1", "client-1"), null);

            var second = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var loaded = await second.GetAsync(userId);
            Assert.NotNull(loaded);
            Assert.Equal("token-1", loaded!.AccessToken);

            var (updated, _) = await second.UpsertAsync(
                userId, Credentials("token-2", "client-1"), baseRevision: loaded.Revision);
            Assert.Equal(2, updated.Revision);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Store_CorruptFile_StartsEmptyAndRepairsOnWrite()
    {
        var path = NewPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ this is not json");
            var store = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var userId = Guid.NewGuid().ToString();

            Assert.Null(await store.GetAsync(userId));
            var (created, conflict) = await store.UpsertAsync(
                userId, Credentials("token-1", "client-1"), null);
            Assert.False(conflict);
            Assert.Equal(1, created.Revision);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(document.RootElement.TryGetProperty("traktByUser", out var users));
            Assert.True(users.TryGetProperty(userId, out _));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TmdbProxy_InjectsServerKeyAndForwardsOnlyAllowedQuery()
    {
        var handler = new CapturingHandler("""{"page":2,"results":[]}""");
        var client = new TmdbProxyClient(
            new HttpClient(handler),
            apiKeyProvider: () => "server-key",
            timeoutProvider: () => 15);
        var query = new Dictionary<string, string?>
        {
            ["api_key"] = "client-key",
            ["page"] = "2",
            ["language"] = "es-ES",
            ["with_genres"] = "28",
            ["secret"] = "nope",
            ["vote_count.gte"] = "10"
        };

        var (status, body) = await client.GetAsync(
            "trending/movie/week", query, CancellationToken.None);

        Assert.Equal(200, status);
        Assert.Equal("{\"page\":2,\"results\":[]}", body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        var url = request.RequestUri!.ToString();
        Assert.StartsWith("https://api.themoviedb.org/3/trending/movie/week?", url);
        Assert.Contains("api_key=server-key", url);
        Assert.DoesNotContain("client-key", url);
        Assert.DoesNotContain("secret=nope", url);
        Assert.Contains("page=2", url);
        Assert.Contains("language=es-ES", url);
        Assert.Contains("with_genres=28", url);
        Assert.Contains("vote_count.gte=10", url);
    }

    [Theory]
    [InlineData("account/watchlist")]
    [InlineData("../../movie/1")]
    [InlineData("https://evil.example/movie/1")]
    [InlineData("movie/1?api_key=leak")]
    public async Task TmdbProxy_RejectsPathsOutsideAllowlist(string path)
    {
        var handler = new CapturingHandler("{}");
        var client = new TmdbProxyClient(
            new HttpClient(handler), apiKeyProvider: () => "server-key");

        var (status, body) = await client.GetAsync(
            path, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(400, status);
        Assert.Contains("invalid_path", body);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TmdbProxy_HttpFailureBecomes502()
    {
        var handler = new CapturingHandler("{}") { ThrowHttpRequestException = true };
        var client = new TmdbProxyClient(
            new HttpClient(handler), apiKeyProvider: () => "server-key");

        var (status, body) = await client.GetAsync(
            "discover/movie", new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(502, status);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task TmdbProxy_ReturnsUpstreamStatusAndBodyVerbatim()
    {
        const string upstream = """{"status_code":34,"status_message":"Not found"}""";
        var handler = new CapturingHandler(upstream, HttpStatusCode.NotFound);
        var client = new TmdbProxyClient(
            new HttpClient(handler), apiKeyProvider: () => "server-key");

        var (status, body) = await client.GetAsync(
            "movie/999999", new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(404, status);
        Assert.Equal(upstream, body);
    }


    [Fact]
    public async Task Controller_TraktCredentials_IncludesConfiguredFlagAndNullEmptyUsername()
    {
        var path = NewPath();
        try
        {
            var store = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var http = new HttpClient(new CapturingHandler("{}"));
            var trakt = new TraktTokenService(http, store, NullLogger<TraktTokenService>.Instance);
            var proxy = new CatalogoProxyClient(http);
            var library = System.Reflection.DispatchProxy.Create<ILibraryManager, MedusaContractTests.LibraryManagerProxy>();
            var controller = new CatalogoController(
                library,
                proxy,
                NullLogger<CatalogoController>.Instance,
                store: store,
                trakt: trakt);
            var userId = Guid.NewGuid();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("Jellyfin-UserId", userId.ToString())
                    }))
                }
            };

            var emptyGet = Assert.IsType<OkObjectResult>(
                await controller.GetTraktCredentials(CancellationToken.None));
            var emptyJson = JsonSerializer.SerializeToElement(emptyGet.Value!, emptyGet.Value!.GetType());
            Assert.False(emptyJson.GetProperty("configured").GetBoolean());

            var putResult = Assert.IsType<OkObjectResult>(
                await controller.PutTraktCredentials(new TraktCredentialsInput
                {
                    AccessToken = "tok-1",
                    ClientId = "cid-1",
                    Username = ""
                }));
            var putJson = JsonSerializer.SerializeToElement(putResult.Value!, putResult.Value!.GetType());
            Assert.True(putJson.GetProperty("configured").GetBoolean());
            Assert.Equal("tok-1", putJson.GetProperty("access_token").GetString());
            Assert.Equal(JsonValueKind.Null, putJson.GetProperty("username").ValueKind);

            var cfgResult = Assert.IsType<OkObjectResult>(await controller.GetConfig());
            var cfgJson = JsonSerializer.SerializeToElement(cfgResult.Value!, cfgResult.Value!.GetType());
            Assert.True(cfgJson.GetProperty("trakt_synced").GetBoolean());
            Assert.Equal(JsonValueKind.Null, cfgJson.GetProperty("trakt_username").ValueKind);

            var getResult = Assert.IsType<OkObjectResult>(
                await controller.GetTraktCredentials(CancellationToken.None));
            var getJson = JsonSerializer.SerializeToElement(getResult.Value!, getResult.Value!.GetType());
            Assert.True(getJson.GetProperty("configured").GetBoolean());
            Assert.Equal("tok-1", getJson.GetProperty("access_token").GetString());
            Assert.Equal(1, getJson.GetProperty("revision").GetInt32());
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task TraktTokenService_SkipsRefreshWhenRefreshTokenIsEmpty()
    {
        var path = NewPath();
        try
        {
            var store = new MedusaDataStore(path, NullLogger<MedusaDataStore>.Instance);
            var handler = new CapturingHandler("{}");
            var service = new TraktTokenService(
                new HttpClient(handler), store, NullLogger<TraktTokenService>.Instance);
            var creds = new TraktCredentials
            {
                AccessToken = "manual-token",
                RefreshToken = "",
                ExpiresIn = 3600,
                CreatedAt = 1,
                ClientId = "cid-1",
                Revision = 1
            };

            var result = await service.EnsureFreshAsync("u1", creds, CancellationToken.None);

            Assert.Equal("manual-token", result.AccessToken);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Delete(path);
        }
    }


    [Fact]
    public void ConfigPage_EmbeddedResourceContainsTmdbApiKeyInput()
    {
        var asm = typeof(Jellyfin.Plugin.Catalogo.Plugin).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "Jellyfin.Plugin.Catalogo.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();
        Assert.Contains("id=\"TmdbApiKey\"", html);
        Assert.Contains("config.TmdbApiKey", html);
        var scriptIdx = html.IndexOf("<script", StringComparison.Ordinal);
        var lastDivIdx = html.LastIndexOf("</div>", StringComparison.Ordinal);
        Assert.True(scriptIdx > 0 && scriptIdx < lastDivIdx, "script must live inside data-role=page");
    }

    private static TraktCredentials Credentials(string accessToken, string clientId) => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh",
        ExpiresIn = 7200,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ClientId = clientId,
        ClientSecret = "secret",
        Username = "medusa-user"
    };

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(), $"medusa-sync-{Guid.NewGuid():N}.json");

    private static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    private sealed class CapturingHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public bool ThrowHttpRequestException { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (ThrowHttpRequestException)
            {
                throw new HttpRequestException("boom");
            }

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
