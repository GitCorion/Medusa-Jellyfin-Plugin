using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Catalogo.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Api;

/// <summary>
/// Companion API for Medusa-aware Jellyfin clients.
/// </summary>
[ApiController]
[Authorize]
// Alias legacy para la app estable 2.4.1; se puede retirar cuando la
// estable use /Medusa. La beta 2.4.2-beta.3+ ya usa /Medusa.
[Route("Catalogo")]
[Route("Medusa")]
[Produces("application/json")]
public class CatalogoController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly CatalogoProxyClient _proxy;
    private readonly ILogger<CatalogoController> _logger;
    private readonly MedusaDataStore? _store;
    private readonly TmdbProxyClient? _tmdb;
    private readonly TraktTokenService? _trakt;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoController"/> class.
    /// Los servicios nuevos son opcionales para conservar el constructor que
    /// usan los tests de contrato; DI los inyecta cuando están registrados.
    /// </summary>
    public CatalogoController(
        ILibraryManager libraryManager,
        CatalogoProxyClient proxy,
        ILogger<CatalogoController> logger,
        MedusaDataStore? store = null,
        TmdbProxyClient? tmdb = null,
        TraktTokenService? trakt = null)
    {
        _libraryManager = libraryManager;
        _proxy = proxy;
        _logger = logger;
        _store = store;
        _tmdb = tmdb;
        _trakt = trakt;
    }

    private MedusaDataStore Store => _store
        ?? throw new InvalidOperationException("MedusaDataStore is not registered.");

    private TmdbProxyClient TmdbClient => _tmdb
        ?? throw new InvalidOperationException("TmdbProxyClient is not registered.");

    private TraktTokenService Trakt => _trakt
        ?? throw new InvalidOperationException("TraktTokenService is not registered.");

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
            features = new[]
            {
                "status",
                "prepare",
                "lease",
                "media-info",
                "fast-start",
                "versions",
                "version-negotiation",
                "tmdb-proxy",
                "trakt-sync"
            },
            native_mode = Plugin.Instance?.Configuration.NativeMode ?? false,
            proxy_configured = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Configuration.ProxyUrl),
            tmdb_configured = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Configuration.TmdbApiKey)
        });
    }

    /// <summary>
    /// Gets the additive Medusa sync configuration for the authenticated user.
    /// Nunca devuelve secretos ni la clave TMDB.
    /// </summary>
    [HttpGet("Config")]
    public async Task<ActionResult> GetConfig()
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { error = "unauthorized", message = "Se requiere un usuario de Jellyfin." });
        }

        var credentials = await Store.GetAsync(userId.Value.ToString());
        return Ok(new
        {
            tmdb_configured = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Configuration.TmdbApiKey),
            trakt_synced = credentials is not null,
            trakt_username = credentials?.Username ?? string.Empty,
            trakt_revision = credentials?.Revision ?? 0
        });
    }

    /// <summary>
    /// Gets the Trakt credentials of the authenticated user, refreshing the
    /// access token on a best-effort basis.
    /// </summary>
    [HttpGet("Trakt/Credentials")]
    public async Task<ActionResult> GetTraktCredentials(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { error = "unauthorized", message = "Se requiere un usuario de Jellyfin." });
        }

        var key = userId.Value.ToString();
        var credentials = await Store.GetAsync(key);
        if (credentials is null)
        {
            return Ok(new { configured = false });
        }

        var fresh = await Trakt.EnsureFreshAsync(key, credentials, cancellationToken);
        return Ok(TraktDto(fresh));
    }

    /// <summary>
    /// Stores the Trakt credentials of the authenticated user.
    /// </summary>
    [HttpPut("Trakt/Credentials")]
    public async Task<ActionResult> PutTraktCredentials([FromBody] TraktCredentialsInput? body)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { error = "unauthorized", message = "Se requiere un usuario de Jellyfin." });
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.AccessToken)
            || string.IsNullOrWhiteSpace(body.ClientId))
        {
            return BadRequest(new
            {
                error = "invalid_credentials",
                message = "access_token y client_id son obligatorios."
            });
        }

        var input = new TraktCredentials
        {
            AccessToken = body.AccessToken,
            RefreshToken = body.RefreshToken ?? string.Empty,
            ExpiresIn = body.ExpiresIn,
            CreatedAt = body.CreatedAt,
            ClientId = body.ClientId,
            ClientSecret = body.ClientSecret ?? string.Empty,
            Username = body.Username ?? string.Empty
        };
        var (stored, conflict) = await Store.UpsertAsync(
            userId.Value.ToString(), input, body.BaseRevision);
        if (conflict)
        {
            return StatusCode(409, TraktDto(stored));
        }

        return Ok(TraktDto(stored));
    }

    /// <summary>
    /// Deletes the Trakt credentials of the authenticated user.
    /// </summary>
    [HttpDelete("Trakt/Credentials")]
    public async Task<ActionResult> DeleteTraktCredentials([FromQuery] int? baseRevision = null)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { error = "unauthorized", message = "Se requiere un usuario de Jellyfin." });
        }

        var key = userId.Value.ToString();
        var deleted = await Store.DeleteAsync(key, baseRevision);
        if (!deleted && baseRevision.HasValue)
        {
            var current = await Store.GetAsync(key);
            return StatusCode(409, TraktDto(current ?? new TraktCredentials()));
        }

        return Ok(new { deleted = true });
    }

    /// <summary>
    /// Read-only proxy to the TMDB API v3 for Medusa clients.
    /// </summary>
    [HttpGet("Tmdb/{**path}")]
    public async Task<ActionResult> Tmdb(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey))
        {
            return StatusCode(503, new
            {
                error = "tmdb_not_configured",
                message = "Configura la clave API de TMDB (v3) en el plugin."
            });
        }

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Request.Query)
        {
            query[pair.Key] = pair.Value.Count > 0
                ? pair.Value[pair.Value.Count - 1]
                : null;
        }

        var (status, body) = await TmdbClient.GetAsync(path, query, cancellationToken);
        return new ContentResult
        {
            StatusCode = status,
            Content = body,
            ContentType = "application/json"
        };
    }

    /// <summary>
    /// Gets the Catalogo status for a Jellyfin item.
    /// </summary>
    [HttpGet("Status/{itemId:guid}")]
    public async Task<ActionResult> Status(
        Guid itemId,
        [FromQuery] int? release,
        CancellationToken cancellationToken = default,
        [FromQuery] int? maxWidth = null,
        [FromQuery] bool? supports4k = null,
        [FromQuery] bool? supportsDv = null)
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
                maxWidth,
                supports4k,
                supportsDv,
                cancellationToken);
            var (recommendedMediaSourceId, recommendedReleaseId, versions) =
                NegotiateVersions(
                    itemId,
                    mapped.Value.ReleaseId,
                    status,
                    maxWidth,
                    supports4k,
                    supportsDv);
            return Ok(new
            {
                is_catalog = true,
                item_id = itemId,
                key = mapped.Value.Key,
                release_id = mapped.Value.ReleaseId,
                status,
                recommended_media_source_id = recommendedMediaSourceId,
                recommended_release_id = recommendedReleaseId,
                versions
            });
        }
        catch (CatalogoProxyException ex)
        {
            _logger.LogWarning(ex, "Medusa proxy status failed");
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
        CancellationToken cancellationToken = default,
        [FromQuery] int? maxWidth = null,
        [FromQuery] bool? supports4k = null,
        [FromQuery] bool? supportsDv = null)
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
                maxWidth,
                supports4k,
                supportsDv,
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
            _logger.LogWarning(ex, "Medusa proxy prepare failed");
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
            _logger.LogWarning(ex, "Medusa lease failed");
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

    private (string? RecommendedMediaSourceId, int? RecommendedReleaseId, List<object> Versions) NegotiateVersions(
        Guid itemId,
        int? requestedReleaseId,
        JsonElement status,
        int? maxWidth,
        bool? supports4k,
        bool? supportsDv)
    {
        var item = _libraryManager.GetItemById(itemId);
        IReadOnlyList<MediaSourceInfo> jellyfinSources;
        try
        {
            jellyfinSources = item?.GetMediaSources(false) ?? Array.Empty<MediaSourceInfo>();
        }
        catch
        {
            jellyfinSources = Array.Empty<MediaSourceInfo>();
        }

        var sourceByReleaseId = new Dictionary<int, MediaSourceInfo>();
        var candidateSources = new List<(MediaSourceInfo Source, int? ReleaseId, bool Compatible, bool Is4k, bool IsDv)>();
        foreach (var ms in jellyfinSources)
        {
            int? msRelId = null;
            if (!string.IsNullOrWhiteSpace(ms.Path))
            {
                var (_, parsedRel) = ms.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                    ? StrmResolver.Parse(ms.Path)
                    : StrmResolver.ParseUrl(ms.Path);
                msRelId = parsedRel;
                if (parsedRel is not null && !sourceByReleaseId.ContainsKey(parsedRel.Value))
                {
                    sourceByReleaseId[parsedRel.Value] = ms;
                }
            }

            var nameLower = (ms.Name ?? string.Empty).ToLowerInvariant();
            var videoStream = ms.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var is4k = nameLower.Contains("4k", StringComparison.Ordinal)
                || nameLower.Contains("uhd", StringComparison.Ordinal)
                || nameLower.Contains("2160", StringComparison.Ordinal)
                || (videoStream?.Width ?? 0) >= 3840;
            var isDv = nameLower.Contains("dolby", StringComparison.Ordinal)
                || nameLower.Contains("dovi", StringComparison.Ordinal)
                || videoStream?.VideoRangeType == VideoRangeType.DOVI;
            var compatible = !((supports4k == false || (maxWidth is > 0 and < 3840)) && is4k)
                && !(supportsDv == false && isDv);
            candidateSources.Add((ms, msRelId, compatible, is4k, isDv));
        }

        int? todRecommendedRelease = requestedReleaseId;
        if (todRecommendedRelease is null
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("recommended_release_id", out var recProp)
            && recProp.ValueKind == JsonValueKind.Number
            && recProp.TryGetInt32(out var recVal))
        {
            todRecommendedRelease = recVal;
        }

        string? recommendedMediaSourceId = null;
        if (todRecommendedRelease is not null && sourceByReleaseId.TryGetValue(todRecommendedRelease.Value, out var matchedMs))
        {
            recommendedMediaSourceId = matchedMs.Id;
        }

        var todVersions = new List<JsonElement>();
        if (status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("versions", out var verProp)
            && verProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in verProp.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Object)
                {
                    todVersions.Add(v);
                }
            }
        }

        // If TOD recommended a pack sibling release_id (e.g. 62523) while Jellyfin's merged .strm
        // carries the parent pack release_id (e.g. 62517), match by compatibility/label!
        if (recommendedMediaSourceId is null && candidateSources.Count > 0)
        {
            var recVersion = todRecommendedRelease is not null
                ? todVersions.FirstOrDefault(v =>
                    v.TryGetProperty("release_id", out var rProp)
                    && rProp.ValueKind == JsonValueKind.Number
                    && rProp.GetInt32() == todRecommendedRelease.Value)
                : default;
            var recLabel = recVersion.ValueKind == JsonValueKind.Object && recVersion.TryGetProperty("label", out var lblProp)
                ? lblProp.GetString()
                : null;

            var byLabel = !string.IsNullOrWhiteSpace(recLabel)
                ? candidateSources.FirstOrDefault(c =>
                    c.Compatible
                    && !string.IsNullOrWhiteSpace(c.Source.Name)
                    && c.Source.Name.Contains(recLabel, StringComparison.OrdinalIgnoreCase))
                : default;
            if (byLabel.Source is not null)
            {
                recommendedMediaSourceId = byLabel.Source.Id;
                todRecommendedRelease ??= byLabel.ReleaseId;
            }
            else
            {
                var compatibleCandidates = candidateSources.Where(c => c.Compatible).ToList();
                var chosen = supports4k == true
                    ? (compatibleCandidates.FirstOrDefault(c => c.Is4k).Source
                        ?? compatibleCandidates.FirstOrDefault(c => c.ReleaseId is not null).Source
                        ?? compatibleCandidates.FirstOrDefault().Source)
                    : (compatibleCandidates.FirstOrDefault(c =>
                            c.ReleaseId is not null
                            && ((c.Source.Name ?? string.Empty).Contains("1080", StringComparison.OrdinalIgnoreCase)
                                || (c.Source.Name ?? string.Empty).Contains("fhd", StringComparison.OrdinalIgnoreCase))).Source
                        ?? compatibleCandidates.FirstOrDefault(c => c.ReleaseId is not null).Source
                        ?? compatibleCandidates.FirstOrDefault().Source);
                if (chosen is not null)
                {
                    recommendedMediaSourceId = chosen.Id;
                    if (todRecommendedRelease is null)
                    {
                        todRecommendedRelease = candidateSources.FirstOrDefault(c => c.Source.Id == chosen.Id).ReleaseId;
                    }
                }
            }
        }

        var merged = new List<object>();
        if (todVersions.Count > 0)
        {
            foreach (var v in todVersions)
            {
                var relId = v.TryGetProperty("release_id", out var rProp) && rProp.ValueKind == JsonValueKind.Number
                    ? rProp.GetInt32()
                    : (int?)null;
                var label = v.TryGetProperty("label", out var lProp) ? lProp.GetString() : null;
                var compatible = v.TryGetProperty("compatible", out var cProp) && cProp.ValueKind != JsonValueKind.False;
                var available = v.TryGetProperty("available", out var aProp) && aProp.ValueKind == JsonValueKind.True;
                var isDv = v.TryGetProperty("dolby_vision", out var dProp) && dProp.ValueKind == JsonValueKind.True;
                MediaSourceInfo? msMatch = null;
                if (relId is not null && sourceByReleaseId.TryGetValue(relId.Value, out var exact))
                {
                    msMatch = exact;
                }
                else if (!string.IsNullOrWhiteSpace(label))
                {
                    msMatch = candidateSources
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Source.Name)
                            && c.Source.Name.Contains(label, StringComparison.OrdinalIgnoreCase))
                        .Source;
                }

                var isRec = (recommendedMediaSourceId is not null && msMatch?.Id == recommendedMediaSourceId)
                    || (recommendedMediaSourceId is null && todRecommendedRelease is not null && relId == todRecommendedRelease);
                merged.Add(new
                {
                    media_source_id = msMatch?.Id,
                    release_id = relId,
                    label,
                    title = v.TryGetProperty("title", out var tProp) ? tProp.GetString() : null,
                    quality = v.TryGetProperty("quality", out var qProp) ? qProp.GetString() : null,
                    codec = v.TryGetProperty("codec", out var cdProp) ? cdProp.GetString() : null,
                    audio = v.TryGetProperty("audio", out var auProp) ? auProp.GetString() : null,
                    width = v.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number ? wProp.GetInt32() : (int?)null,
                    height = v.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number ? hProp.GetInt32() : (int?)null,
                    size_bytes = v.TryGetProperty("size_bytes", out var sProp) && sProp.ValueKind == JsonValueKind.Number ? sProp.GetInt64() : (long?)null,
                    dolby_vision = isDv,
                    available,
                    compatible,
                    recommended = isRec
                });
            }
        }
        else
        {
            foreach (var c in candidateSources)
            {
                merged.Add(new
                {
                    media_source_id = c.Source.Id,
                    release_id = c.ReleaseId,
                    label = c.Source.Name,
                    title = c.Source.Name,
                    quality = c.Is4k ? "4K" : "1080p",
                    codec = c.Source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec,
                    audio = (string?)null,
                    width = c.Source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Width,
                    height = c.Source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Height,
                    size_bytes = c.Source.Size,
                    dolby_vision = c.IsDv,
                    available = true,
                    compatible = c.Compatible,
                    recommended = recommendedMediaSourceId == c.Source.Id
                });
            }
        }

        return (recommendedMediaSourceId, todRecommendedRelease, merged);
    }

    private Guid? CurrentUserId()
    {
        if (HttpContext is null)
        {
            return null;
        }

        var raw = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(raw, out var userId) ? userId : null;
    }

    private static object TraktDto(TraktCredentials credentials) => new
    {
        access_token = credentials.AccessToken,
        refresh_token = credentials.RefreshToken,
        expires_in = credentials.ExpiresIn,
        created_at = credentials.CreatedAt,
        client_id = credentials.ClientId,
        client_secret = credentials.ClientSecret,
        username = credentials.Username,
        revision = credentials.Revision,
        updated_at_utc = credentials.UpdatedAtUtc
    };

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

/// <summary>
/// Cuerpo snake_case aceptado por PUT /Medusa/Trakt/Credentials.
/// </summary>
public sealed class TraktCredentialsInput
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("base_revision")]
    public int? BaseRevision { get; set; }
}
