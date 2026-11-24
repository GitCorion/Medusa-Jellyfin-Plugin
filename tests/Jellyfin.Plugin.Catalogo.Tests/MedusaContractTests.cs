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
