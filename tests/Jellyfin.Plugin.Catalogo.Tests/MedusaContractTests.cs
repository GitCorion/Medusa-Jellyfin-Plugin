using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Catalogo.Api;
using Jellyfin.Plugin.Catalogo.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

/// <summary>
/// Congela el contrato que consume Medusa. Estos tests no deben cambiar de
/// comportamiento al añadir funciones nativas: si fallan, se ha roto la API.
/// </summary>
public class MedusaContractTests
{
    private const string StatusBody = """
        {
          "key": "movie:tmdb:1",
          "release_id": 109,
          "stream_generation": 7,
          "stream_switched_at": "2026-11-20T08:00:00Z",
          "stream_switch_reason": "slow_swarm",
          "stream_ready": false,
          "state": "DOWNLOADING",
          "progress": 0.28
        }
        """;

    [Fact]
    public void Capabilities_KeepMedusaContract()
    {
        var (controller, _, _) = CreateController(StatusBody);

        var result = Assert.IsType<OkObjectResult>(controller.Capabilities());
        var json = ToJson(result.Value);

        Assert.Equal(1, json.GetProperty("api_version").GetInt32());
        var features = json.GetProperty("features")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        Assert.Contains("status", features);
        Assert.Contains("prepare", features);
        Assert.Equal("Medusa", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("native_mode").GetBoolean());
    }

    [Fact]
    public async Task Status_ForCatalogItem_ForwardsTodStatus()
    {
        var (controller, handler, path) = CreateController(StatusBody);
        try
        {
            var itemId = Guid.NewGuid();

            var result = await controller.Status(
                itemId, release: null, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.True(json.GetProperty("is_catalog").GetBoolean());
            Assert.Equal(
                "movie:tmdb:1", json.GetProperty("key").GetString());
            Assert.Equal(109, json.GetProperty("release_id").GetInt32());
            var status = json.GetProperty("status");
            Assert.Equal(7, status.GetProperty("stream_generation").GetInt64());
            Assert.False(status.GetProperty("stream_ready").GetBoolean());

            var request = Assert.Single(handler.Requests);
            Assert.Contains("/status?key=movie%3Atmdb%3A1", request.RequestUri!.ToString());
            Assert.Contains("release=109", request.RequestUri!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_ForNonCatalogItem_ReturnsIsCatalogFalse()
    {
        var (controller, handler, _) = CreateController(
            StatusBody,
            strmUrl: "http://127.0.0.1:8773/healthz");
        var itemId = Guid.NewGuid();

        var result = await controller.Status(
            itemId, release: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = ToJson(ok.Value);
        Assert.False(json.GetProperty("is_catalog").GetBoolean());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Prepare_ForwardsReleaseAndRuntime()
    {
        var (controller, handler, path) = CreateController(
            """{"prepared":true,"status":{"stream_ready":false}}""");
        try
        {
            var result = await controller.Prepare(
                Guid.NewGuid(), release: null, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(202, objectResult.StatusCode);
            var json = ToJson(objectResult.Value);
            Assert.True(json.GetProperty("is_catalog").GetBoolean());
            Assert.True(
                json.GetProperty("prepared").GetProperty("prepared").GetBoolean());

            var request = Assert.Single(handler.Requests);
            var url = request.RequestUri!.ToString();
            Assert.Contains("/prepare?key=movie%3Atmdb%3A1", url);
            Assert.Contains("release=109", url);
            Assert.Contains("runtime=3224", url);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_ProxyError_KeepsServerMessage()
    {
        var (controller, _, path) = CreateController(
            """
            {"error":"sin_fuente","message":"No hay ninguna fuente reproducible para este título."}
            """,
            HttpStatusCode.ServiceUnavailable);
        try
        {
            var result = await controller.Status(
                Guid.NewGuid(), release: null, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, objectResult.StatusCode);
            var json = ToJson(objectResult.Value);
            Assert.Equal("sin_fuente", json.GetProperty("error").GetString());
            Assert.Equal(
                "No hay ninguna fuente reproducible para este título.",
                json.GetProperty("message").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Lease_UsesJellyfinDeviceIdAsMedusaSession()
    {
        var (controller, handler, path) = CreateController(
            """{"leased":true}""");
        try
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
            controller.Request.Headers["X-Emby-Device-Id"] = "device-1";

            var result = await controller.Lease(
                Guid.NewGuid(), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.True(
                json.GetProperty("leased").GetProperty("leased").GetBoolean());
            var request = Assert.Single(handler.Requests);
            Assert.Contains(
                "session=medusa%3Adevice-1",
                request.RequestUri!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_ForwardsClientVideoCapabilities_AndCapabilitiesAdvertiseNegotiation()
    {
        const string jsonBody = "{\"key\":\"movie:tmdb:1\",\"release_id\":109,\"recommended_release_id\":109,\"versions\":[{\"release_id\":109,\"label\":\"1080p Cast\",\"available\":true,\"compatible\":true}],\"stream_ready\":true}";
        var (controller, handler, path) = CreateController(jsonBody);
        try
        {
            var capsResult = Assert.IsType<OkObjectResult>(controller.Capabilities());
            var capsJson = ToJson(capsResult.Value);
            var features = capsJson.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Contains("versions", features);
            Assert.Contains("version-negotiation", features);

            var statusResult = await controller.Status(
                Guid.NewGuid(),
                release: null,
                CancellationToken.None,
                maxWidth: 1920,
                supports4k: false,
                supportsDv: false);
            var ok = Assert.IsType<OkObjectResult>(statusResult);
            var json = ToJson(ok.Value);
            Assert.Equal(109, json.GetProperty("recommended_release_id").GetInt32());
            var reqUrl = Assert.Single(handler.Requests).RequestUri!.ToString();
            Assert.Contains("max_width=1920", reqUrl);
            Assert.Contains("supports_4k=0", reqUrl);
            Assert.Contains("supports_dv=0", reqUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_WhenPinnedReleaseUnavailableAfterSwitch_FallsBackToActiveAlternative()
    {
        const string deadPinnedBody = """
            {
              "key": "movie:tmdb:200085",
              "release_id": 64605,
              "stream_generation": 1,
              "stream_switch_reason": "buffer_stalled",
              "stream_ready": false,
              "state": "UNAVAILABLE",
              "playback_state": "unavailable",
              "source": "unavailable",
              "reason": "stream_bad_swarm",
              "progress": 0.0,
              "speed_bps": 0,
              "recommended_release_id": 64604,
              "versions": [
                {"release_id": 64604, "label": "1080p Cast", "available": true, "compatible": true},
                {"release_id": 64605, "label": "1080p Cast", "available": false, "compatible": true}
              ]
            }
            """;
        const string activeFallbackBody = """
            {
              "key": "movie:tmdb:200085",
              "release_id": 64604,
              "stream_generation": 1,
              "stream_switch_reason": "buffer_stalled",
              "stream_ready": true,
              "state": "DOWNLOADING",
              "playback_state": "ready",
              "source": "torrent",
              "progress": 0.35,
              "speed_bps": 75972137,
              "recommended_release_id": 64604,
              "versions": [
                {"release_id": 64604, "label": "1080p Cast", "available": true, "compatible": true},
                {"release_id": 64605, "label": "1080p Cast", "available": false, "compatible": true}
              ]
            }
            """;

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.strm");
        File.WriteAllText(path, "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A200085&release=64605&download=0\n");
        try
        {
            var handler = new SequenceHandler(
                (HttpStatusCode.OK, deadPinnedBody),
                (HttpStatusCode.OK, activeFallbackBody));
            var proxy = new CatalogoProxyClient(new HttpClient(handler));
            var item = new Movie
            {
                Path = path,
                RunTimeTicks = 7320L * TimeSpan.TicksPerSecond
            };
            var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
            ((LibraryManagerProxy)(object)library).Handler =
                (method, _) => method.Name == nameof(ILibraryManager.GetItemById)
                    ? item
                    : throw new NotSupportedException(method.Name);

            var controller = new CatalogoController(
                library, proxy, NullLogger<CatalogoController>.Instance);

            var result = await controller.Status(
                Guid.NewGuid(),
                release: 64605,
                CancellationToken.None,
                maxWidth: 1920,
                supports4k: false,
                supportsDv: false);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.Equal(64604, json.GetProperty("release_id").GetInt32());
            Assert.Equal(64604, json.GetProperty("recommended_release_id").GetInt32());
            var status = json.GetProperty("status");
            Assert.Equal("DOWNLOADING", status.GetProperty("state").GetString());
            Assert.True(status.GetProperty("stream_ready").GetBoolean());
            Assert.Equal(64604, status.GetProperty("release_id").GetInt32());

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("release=64605", handler.Requests[0].RequestUri!.ToString());
            Assert.DoesNotContain("release=", handler.Requests[1].RequestUri!.ToString());
            Assert.Contains("supports_4k=0", handler.Requests[1].RequestUri!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_WhenTodRedirectsPinnedRelease_RecommendsActiveRelease()
    {
        const string redirectedBody = """
            {
              "key": "movie:tmdb:200085",
              "release_id": 64604,
              "recommended_release_id": 64604,
              "stream_generation": 1,
              "stream_switch_reason": "buffer_stalled",
              "stream_ready": true,
              "state": "AVAILABLE",
              "playback_state": "idle",
              "source": "local",
              "reason": "auto_local_seed",
              "progress": 1.0,
              "tail_ready": false,
              "versions": [
                {"release_id": 64604, "label": "1080p Cast", "available": true, "compatible": true},
                {"release_id": 64605, "label": "1080p Cast", "available": false, "compatible": true}
              ]
            }
            """;
        var (controller, handler, path) = CreateController(
            redirectedBody,
            strmUrl: "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A200085&release=64605&download=0");
        try
        {
            var result = await controller.Status(
                Guid.NewGuid(), release: 64605, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.Equal(64604, json.GetProperty("release_id").GetInt32());
            Assert.Equal(64604, json.GetProperty("recommended_release_id").GetInt32());
            var status = json.GetProperty("status");
            Assert.True(status.GetProperty("stream_ready").GetBoolean());
            Assert.False(status.GetProperty("tail_ready").GetBoolean());

            var request = Assert.Single(handler.Requests).RequestUri!.ToString();
            Assert.Contains("release=64605", request);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_WhenPinnedReleaseIsCold_DoesNotFallback()
    {
        const string coldBody = """
            {
              "key": "movie:tmdb:200085",
              "release_id": 64605,
              "stream_generation": 0,
              "stream_ready": false,
              "state": "COLD",
              "playback_state": "idle",
              "source": "torrent",
              "progress": 0.0,
              "speed_bps": 0,
              "recommended_release_id": 64604
            }
            """;
        var (controller, handler, path) = CreateController(
            coldBody,
            strmUrl: "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A200085&release=64605&download=0");
        try
        {
            var result = await controller.Status(
                Guid.NewGuid(), release: 64605, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.Equal(64605, json.GetProperty("release_id").GetInt32());
            Assert.Equal("COLD", json.GetProperty("status").GetProperty("state").GetString());
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Status_WhenFallbackLookupFails_KeepsOriginalStatus()
    {
        const string deadPinnedBody = """
            {
              "key": "movie:tmdb:200085",
              "release_id": 64605,
              "stream_ready": false,
              "state": "UNAVAILABLE",
              "source": "unavailable",
              "reason": "stream_bad_swarm"
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.strm");
        File.WriteAllText(path, "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A200085&release=64605&download=0\n");
        try
        {
            var handler = new SequenceHandler(
                (HttpStatusCode.OK, deadPinnedBody),
                (HttpStatusCode.NotFound, """{"error":"sin_estado"}"""));
            var proxy = new CatalogoProxyClient(new HttpClient(handler));
            var item = new Movie { Path = path };
            var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
            ((LibraryManagerProxy)(object)library).Handler =
                (method, _) => method.Name == nameof(ILibraryManager.GetItemById)
                    ? item
                    : throw new NotSupportedException(method.Name);

            var controller = new CatalogoController(
                library, proxy, NullLogger<CatalogoController>.Instance);

            var result = await controller.Status(
                Guid.NewGuid(), release: 64605, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value);
            Assert.Equal("UNAVAILABLE", json.GetProperty("status").GetProperty("state").GetString());
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Prepare_UsesPreparedReleaseIdWhenTodFallsBack()
    {
        var (controller, _, path) = CreateController(
            """{"prepared":true,"release_id":64604,"status":{"stream_ready":false,"release_id":64604}}""");
        try
        {
            var result = await controller.Prepare(
                Guid.NewGuid(), release: 64605, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(202, objectResult.StatusCode);
            var json = ToJson(objectResult.Value);
            Assert.Equal(64604, json.GetProperty("release_id").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (CatalogoController Controller, RecordingHandler Handler, string Path)
        CreateController(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string strmUrl =
                "http://127.0.0.1:8773/play?key=movie%3Atmdb%3A1&release=109&download=0")
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"{Guid.NewGuid():N}.strm");
        File.WriteAllText(path, strmUrl + "\n");

        var handler = new RecordingHandler(body, statusCode);
        var proxy = new CatalogoProxyClient(new HttpClient(handler));
        var item = new Movie
        {
            Path = path,
            RunTimeTicks = 3224L * TimeSpan.TicksPerSecond
        };
        var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)library).Handler =
            (method, _) => method.Name == nameof(ILibraryManager.GetItemById)
                ? item
                : throw new NotSupportedException(method.Name);

        var controller = new CatalogoController(
            library, proxy, NullLogger<CatalogoController>.Instance);
        return (controller, handler, path);
    }

    private static JsonElement ToJson(object? value)
    {
        Assert.NotNull(value);
        return JsonSerializer.SerializeToElement(value, value!.GetType());
    }

    private sealed class SequenceHandler(
        params (HttpStatusCode StatusCode, string Body)[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var pos = Math.Min(_index++, responses.Length - 1);
            var (statusCode, body) = responses[pos];
            return Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        body, Encoding.UTF8, "application/json")
                });
        }
    }

    private sealed class RecordingHandler(
        string body,
        HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        body, Encoding.UTF8, "application/json")
                });
        }
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(
            MethodInfo? targetMethod, object?[]? args)
            => Handler?.Invoke(targetMethod!, args);
    }
}
