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
            features = new[] { "status", "prepare" },
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
                    error = "proxy_unavailable",
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
            var prepared = await _proxy.PrepareAsync(
                mapped.Value.Key,
                mapped.Value.ReleaseId,
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
                    error = "proxy_unavailable",
                    detail = ex.Message
                });
        }
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
