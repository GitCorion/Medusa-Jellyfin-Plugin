using System.Text.Json;
using Jellyfin.Plugin.Catalogo.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Api;

/// <summary>
/// Companion API for Catalogo-aware Jellyfin clients.
/// </summary>
[ApiController]
[Authorize]
[Route("Catalogo")]
[Route("Medusa")]
[Produces("application/json")]
public class CatalogoController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly CatalogoProxyClient _proxy;
    private readonly ILogger<CatalogoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoController"/> class.
    /// </summary>
    public CatalogoController(
        ILibraryManager libraryManager,
        CatalogoProxyClient proxy,
        ILogger<CatalogoController> logger)
    {
        _libraryManager = libraryManager;
        _proxy = proxy;
        _logger = logger;
    }

    /// <summary>
    /// Returns plugin capabilities.
    /// </summary>
    [HttpGet("Capabilities")]
    [AllowAnonymous]
    public ActionResult Capabilities()
    {
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString()
            ?? "1.0.0.0";
        return Ok(new
        {
            api_version = 1,
            plugin_version = version,
            name = "Medusa",
            legacy_name = "Catalogo",
            features = new[] { "status", "prepare", "lease", "media-info", "fast-start" },
            proxy_configured = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Configuration.ProxyUrl)
        });
    }

    /// <summary>
    /// Gets the Catalogo status for a Jellyfin item.
    /// </summary>
    [HttpGet("Status/{itemId:guid}")]
    public async Task<ActionResult> Status(
        Guid itemId,
        [FromQuery] int? release,
        CancellationToken cancellationToken)
    {
        var mapped = Map(itemId, release);
        if (mapped is null)
        {
            return Ok(new { is_catalog = false, item_id = itemId });
        }

        try
        {
            var status = await _proxy.GetStatusAsync(
                mapped.Value.Key,
                mapped.Value.ReleaseId,
                cancellationToken);
            return Ok(new
            {
                is_catalog = true,
                item_id = itemId,
                key = mapped.Value.Key,
                release_id = mapped.Value.ReleaseId,
                status
            });
        }
        catch (CatalogoProxyException ex)
        {
            _logger.LogWarning(ex, "Catalogo proxy status failed");
            return StatusCode(
                503,
                new
                {
                    is_catalog = true,
                    item_id = itemId,
                    error = ProxyField(ex.Body, "error") ?? "proxy_unavailable",
                    message = ProxyField(ex.Body, "message")
                        ?? "No se pudo consultar el estado de la descarga.",
                    detail = ex.Message
                });
        }
    }

    /// <summary>
    /// Starts the on-demand download without streaming video.
    /// </summary>
    [HttpPost("Prepare/{itemId:guid}")]
    public async Task<ActionResult> Prepare(
        Guid itemId,
        [FromQuery] int? release,
        CancellationToken cancellationToken)
    {
        var mapped = Map(itemId, release);
        if (mapped is null)
        {
            return Ok(new { is_catalog = false, item_id = itemId });
        }

        try
        {
            var item = _libraryManager.GetItemById(itemId);
            var runtime = item?.RunTimeTicks is long ticks && ticks > 0
                ? (int?)(ticks / TimeSpan.TicksPerSecond)
                : null;
            var prepared = await _proxy.PrepareAsync(
                mapped.Value.Key,
                mapped.Value.ReleaseId,
                runtime,
                cancellationToken);
            return StatusCode(
                202,
                new
                {
                    is_catalog = true,
                    item_id = itemId,
                    key = mapped.Value.Key,
                    release_id = mapped.Value.ReleaseId,
                    prepared
                });
        }
        catch (CatalogoProxyException ex)
        {
            _logger.LogWarning(ex, "Catalogo proxy prepare failed");
            return StatusCode(
                503,
                new
                {
                    is_catalog = true,
                    item_id = itemId,
                    error = ProxyField(ex.Body, "error") ?? "proxy_unavailable",
                    message = ProxyField(ex.Body, "message")
                        ?? "No se pudo preparar la descarga.",
                    detail = ex.Message
                });
        }
    }

    /// <summary>
    /// Renews the playback lease for a catalog item.
    /// </summary>
    [HttpPost("Lease/{itemId:guid}")]
    public async Task<ActionResult> Lease(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var mapped = Map(itemId, null);
        if (mapped is null)
        {
            return Ok(new { is_catalog = false, item_id = itemId });
        }

        try
        {
            var session = Request.Headers["X-Emby-Device-Id"].FirstOrDefault()
                ?? HttpContext.TraceIdentifier;
            var leased = await _proxy.LeaseAsync(
                mapped.Value.Key, $"medusa:{session}", cancellationToken);
            return Ok(new
            {
                is_catalog = true,
                item_id = itemId,
                key = mapped.Value.Key,
                leased
            });
        }
        catch (CatalogoProxyException ex)
        {
            _logger.LogWarning(ex, "Catalogo lease failed");
            return StatusCode(
                503,
                new
                {
                    is_catalog = true,
                    item_id = itemId,
                    error = ProxyField(ex.Body, "error") ?? "proxy_unavailable",
                    message = ProxyField(ex.Body, "message")
                        ?? "No se pudo preparar la descarga.",
                    detail = ex.Message
                });
        }
    }

    /// <summary>
    /// Releases the playback lease for a catalog item.
    /// </summary>
    [HttpDelete("Lease/{itemId:guid}")]
    public async Task<ActionResult> ReleaseLease(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var mapped = Map(itemId, null);
        if (mapped is null)
        {
            return Ok(new { is_catalog = false, item_id = itemId });
        }

        var session = Request.Headers["X-Emby-Device-Id"].FirstOrDefault()
            ?? HttpContext.TraceIdentifier;
        await _proxy.ReleaseLeaseAsync(
            mapped.Value.Key, $"medusa:{session}", cancellationToken);
        return Ok(new
        {
            is_catalog = true,
            item_id = itemId,
            key = mapped.Value.Key,
            released = true
        });
    }

    private static string? ProxyField(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.ToString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private (string Key, int? ReleaseId)? Map(Guid itemId, int? release)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null)
        {
            return null;
        }

        var (key, releaseId) = StrmResolver.Parse(item.Path);
        if (key is null)
        {
            return null;
        }

        return (key, release ?? releaseId);
    }
}
