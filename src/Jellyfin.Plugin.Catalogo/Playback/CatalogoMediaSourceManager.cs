using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Catalogo.Configuration;
using Jellyfin.Plugin.Catalogo.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Playback;

/// <summary>
/// Wraps Jellyfin's media source manager so Catalogo .strm items skip the
/// remote ffprobe that otherwise waits for the whole torrent.
/// </summary>
public sealed class CatalogoMediaSourceManager : IMediaSourceManager, IDisposable
{
    private readonly IMediaSourceManager _inner;
    private readonly bool _ownsInner;
    private readonly IServiceProvider _services;
    private readonly ILogger<CatalogoMediaSourceManager> _logger;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly IHttpContextAccessor? _httpContext;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoMediaSourceManager"/> class.
    /// </summary>
    /// <param name="inner">Wrapped media source manager.</param>
    /// <param name="ownsInner">Whether this instance must dispose the inner manager.</param>
    /// <param name="services">Service provider.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="configuration">Returns the current plugin configuration.</param>
    /// <param name="httpContext">Current Jellyfin request, when available.</param>
    public CatalogoMediaSourceManager(
        IMediaSourceManager inner,
        bool ownsInner,
        IServiceProvider services,
        ILogger<CatalogoMediaSourceManager> logger,
        Func<PluginConfiguration?> configuration,
        IHttpContextAccessor? httpContext = null)
    {
        _inner = inner;
        _ownsInner = ownsInner;
        _services = services;
        _logger = logger;
        _configuration = configuration;
        _httpContext = httpContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!allowMediaProbe || !IsCatalogoItem(item))
        {
            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var streams = item.GetMediaStreams();
            var hasVideo = HasVideo(streams);
            // Medusa ya prepara antes de pedir PlaybackInfo. Los clientes
            // nativos no, así que en modo nativo también se prepara cuando el
            // ítem ya tiene streams guardados pero el fichero sigue frío.
            var nativeMode = IsNativeMode();
            var compatibility = IsCompatibilityClient();
            var prepare = ShouldPrepare(hasVideo, nativeMode, compatibility);
            var waitSeconds = nativeMode && !compatibility
                ? Math.Clamp(
                    _configuration()?.NativePrebufferSeconds ?? 0, 0, 30)
                : 0;
            var (info, complete) = await ResolveMediaInfoAsync(
                    item, prepare, waitSeconds, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<MediaStream>? mappedStreams = null;
            if (!hasVideo && info is not null)
            {
                var mapped = CatalogoMediaStreams.FromJson(info.Value);
                if (mapped.Any(stream => stream.Type == MediaStreamType.Video))
                {
                    SaveStreams(item, mapped, info, cancellationToken);
                    streams = mapped;
                    mappedStreams = mapped;
                    _logger.LogInformation(
                        "Medusa fast start: saved {Count} streams for {Item}",
                        mapped.Count,
                        item.Name);
                }
            }

            EnsureShortcut(item);

            if (HasVideo(streams))
            {
                var sources = await _inner.GetPlaybackMediaSources(
                    item, user, false, enablePathSubstitution, cancellationToken)
                    .ConfigureAwait(false);
                Decorate(sources, info, mappedStreams ?? streams);
                // Con la descarga a medias, Jellyfin extrae los subtítulos de
                // texto con ffmpeg leyendo el fichero entero: se queda colgado
                // y reintenta. Se ocultan hasta que TOD tenga el fichero entero.
                return complete ? sources : HideTextSubtitles(sources);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Medusa fast start failed for {Item}; using Jellyfin's probe",
                item.Name);
        }

        return await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _inner.AddParts(providers);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _inner.GetMediaStreams(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        => _inner.GetMediaStreams(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _inner.GetMediaAttachments(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(
        MediaAttachmentQuery query)
        => _inner.GetMediaAttachments(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user)
        => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
        => _inner.GetMediaSource(
            item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    /// <inheritdoc />
    public Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStream(
        string id,
        CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
        string id,
        CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    /// <inheritdoc />
    public ILiveStream? GetLiveStreamInfo(string id)
        => _inner.GetLiveStreamInfo(id);

    /// <inheritdoc />
    public ILiveStream? GetLiveStreamInfoByUniqueId(string uniqueId)
        => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    /// <inheritdoc />
    public Task CloseLiveStream(string id)
        => _inner.CloseLiveStream(id);

    /// <inheritdoc />
    public bool SupportsDirectStream(string path, MediaProtocol protocol)
        => _inner.SupportsDirectStream(path, protocol);

    /// <inheritdoc />
    public MediaProtocol GetPathProtocol(string path)
        => _inner.GetPathProtocol(path);

    /// <inheritdoc />
    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    /// <inheritdoc />
    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static bool HasVideo(IReadOnlyList<MediaStream> streams)
        => streams.Any(stream => stream.Type == MediaStreamType.Video);

    private bool IsNativeMode()
        => _configuration()?.NativeMode == true;

    /// <summary>
    /// Decides whether a catalog PlaybackInfo may start a prepare.
    /// Medusa and the legacy path only prepare when the item has no video
    /// streams. Native mode also prepares when streams exist but the file is
    /// still cold.
    /// </summary>
    /// <param name="hasVideo">Whether the item already has video streams.</param>
    /// <param name="nativeMode">Whether native mode is enabled.</param>
    /// <param name="compatibilityClient">Whether the client needs the legacy path.</param>
    /// <returns><c>true</c> when prepare should be attempted.</returns>
    public static bool ShouldPrepare(
        bool hasVideo,
        bool nativeMode,
        bool compatibilityClient)
        => !hasVideo || (nativeMode && !compatibilityClient);

    private bool IsCompatibilityClient()
    {
        var context = _httpContext?.HttpContext;
        // Sin petición HTTP no hay forma de distinguir al cliente. Se aplica
        // el camino de compatibilidad para no cambiar el de Medusa.
        if (context is null)
        {
            return true;
        }

        return IsMedusaClient(context.Request.Headers);
    }

    /// <summary>
    /// Detects Medusa from the Jellyfin client headers. Medusa sends
    /// <c>Client="Medusa"</c> in Authorization and X-Emby-Authorization.
    /// </summary>
    /// <param name="headers">Current request headers.</param>
    /// <returns><c>true</c> when the request belongs to Medusa.</returns>
    public static bool IsMedusaClient(IHeaderDictionary headers)
    {
        var client = headers["X-Emby-Client"].FirstOrDefault();
        if (client?.Contains("Medusa", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var auth = headers["Authorization"].FirstOrDefault()
            ?? headers["X-Emby-Authorization"].FirstOrDefault();
        return auth?.Contains(
                "Client=\"Medusa\"",
                StringComparison.OrdinalIgnoreCase) == true
            || auth?.Contains(
                "Client=Medusa",
                StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsCatalogoItem(BaseItem? item)
    {
        if (item is null || !item.IsShortcut)
        {
            return false;
        }

        var (key, _) = StrmResolver.Parse(item.Path);
        return !string.IsNullOrWhiteSpace(key);
    }

    private async Task<(JsonElement? Info, bool Complete)> ResolveMediaInfoAsync(
        BaseItem item,
        bool prepare,
        int waitSeconds,
        CancellationToken cancellationToken)
    {
        var proxy = _services.GetRequiredService<CatalogoProxyClient>();
        var (key, releaseId) = StrmResolver.Parse(item.Path);
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, true);
        }

        var complete = true;
        int? resolved = releaseId;
        try
        {
            var status = await proxy.GetStatusAsync(key, resolved, cancellationToken)
                .ConfigureAwait(false);
            resolved = ReleaseId(status) ?? resolved;
            complete = IsComplete(status);
            if (prepare && (resolved is null || !IsReady(status)))
            {
                var runtime = item.RunTimeTicks is long ticks
                    ? (int)(ticks / TimeSpan.TicksPerSecond)
                    : (int?)null;
                try
                {
                    await proxy.PrepareAsync(key, resolved, runtime, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CatalogoProxyException)
                {
                    // La reproducción seguirá por el auto-prepare de /play.
                }

                status = await proxy.GetStatusAsync(key, resolved, cancellationToken)
                    .ConfigureAwait(false);
                resolved = ReleaseId(status) ?? resolved;
                complete = IsComplete(status);
                if (waitSeconds > 0 && !IsReady(status) && IsWaitable(status))
                {
                    status = await WaitForReadyAsync(
                            proxy, key, resolved, status, waitSeconds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    resolved = ReleaseId(status) ?? resolved;
                    complete = IsComplete(status);
                }
            }
        }
        catch (CatalogoProxyException)
        {
            return (null, true);
        }

        try
        {
            var info = await proxy.GetMediaInfoAsync(key, resolved, cancellationToken)
                .ConfigureAwait(false);
            return (info, complete);
        }
        catch (CatalogoProxyException ex)
        {
            _logger.LogDebug(
                "Medusa media-info unavailable for {Key}: {Message}",
                key,
                ex.Message);
            return (null, true);
        }
    }

    /// <summary>
    /// Indica si TOD tiene el fichero completo. Sin datos se asume que sí,
    /// para no ocultar subtítulos por un fallo puntual del proxy.
    /// </summary>
    /// <param name="status">Respuesta de /status de TOD.</param>
    /// <returns><c>true</c> si el fichero está completo.</returns>
    public static bool IsComplete(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (status.TryGetProperty("progress", out var progress)
            && progress.ValueKind == JsonValueKind.Number)
        {
            return progress.GetDouble() >= 0.999;
        }

        if (status.TryGetProperty("state", out var state)
            && state.ValueKind == JsonValueKind.String)
        {
            var value = state.GetString();
            return value is "SEEDING" or "AVAILABLE"
                or "SEEDING_LOCAL" or "AVAILABLE_LOCAL";
        }

        return true;
    }

    /// <summary>
    /// Quita los subtítulos de texto embebidos de las fuentes. Jellyfin los
    /// extrae con ffmpeg leyendo el fichero entero; con un torrent a medias
    /// esa extracción se queda esperando piezas y reintenta en bucle.
    /// </summary>
    /// <param name="sources">Fuentes de reproducción.</param>
    /// <returns>Las mismas fuentes sin subtítulos de texto.</returns>
    public static IReadOnlyList<MediaSourceInfo> HideTextSubtitles(
        IReadOnlyList<MediaSourceInfo> sources)
    {
        foreach (var source in sources)
        {
            var streams = source.MediaStreams;
            if (streams is null || streams.Count == 0)
            {
                continue;
            }

            var kept = streams
                .Where(stream => !(stream.Type == MediaStreamType.Subtitle
                    && stream.IsTextSubtitleStream))
                .ToList();
            if (kept.Count != streams.Count)
            {
                source.MediaStreams = kept;
            }
        }

        return sources;
    }

    private void SaveStreams(
        BaseItem item,
        IReadOnlyList<MediaStream> streams,
        JsonElement? info,
        CancellationToken cancellationToken)
    {
        var repository = _services.GetRequiredService<IMediaStreamRepository>();
        repository.SaveMediaStreams(item.Id, streams, cancellationToken);
        if (info is not null)
        {
            var ticks = CatalogoMediaStreams.DurationTicks(info.Value);
            if (ticks.HasValue)
            {
                item.RunTimeTicks = ticks;
            }
        }

        EnsureShortcut(item);
    }

    private static void EnsureShortcut(BaseItem item)
    {
        if (!item.IsShortcut || string.IsNullOrWhiteSpace(item.Path)
            || !string.IsNullOrWhiteSpace(item.ShortcutPath))
        {
            return;
        }

        var line = File.ReadLines(item.Path)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0 && !value.StartsWith('#'));
        if (!string.IsNullOrWhiteSpace(line))
        {
            item.ShortcutPath = line;
        }
    }

    private static int? ReleaseId(JsonElement status)
    {
        if (!status.TryGetProperty("release_id", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            return null;
        }

        return number;
    }

    private static bool IsReady(JsonElement status)
        => status.TryGetProperty("stream_ready", out var value)
            && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Indica si merece la pena esperar a que TOD arranque el stream. Los
    /// estados terminales no deben bloquear el PlaybackInfo nativo.
    /// </summary>
    /// <param name="status">Respuesta de /status de TOD.</param>
    /// <returns><c>true</c> si el estado todavía puede avanzar.</returns>
    public static bool IsWaitable(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return state.GetString() is
            "COLD" or "DOWNLOADING" or "PAUSED" or "SEEDING"
            or "AVAILABLE" or "SEEDING_LOCAL" or "AVAILABLE_LOCAL";
    }

    private static async Task<JsonElement> WaitForReadyAsync(
        CatalogoProxyClient proxy,
        string key,
        int? releaseId,
        JsonElement status,
        int waitSeconds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline
            && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                status = await proxy.GetStatusAsync(
                        key, releaseId, cancellationToken)
                    .ConfigureAwait(false);
                releaseId = ReleaseId(status) ?? releaseId;
                if (IsReady(status) || !IsWaitable(status))
                {
                    return status;
                }
            }
            catch (OperationCanceledException)
            {
                return status;
            }
            catch (CatalogoProxyException)
            {
                return status;
            }
        }

        return status;
    }

    private static IReadOnlyList<MediaSourceInfo> Decorate(
        IReadOnlyList<MediaSourceInfo> sources,
        JsonElement? info,
        IReadOnlyList<MediaStream>? streams)
    {
        if (info is null || sources.Count == 0)
        {
            return sources;
        }

        var container = CatalogoMediaStreams.Container(info.Value);
        var ticks = CatalogoMediaStreams.DurationTicks(info.Value);
        var size = CatalogoMediaStreams.MediaSize(info.Value);
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(container))
            {
                source.Container = container;
            }

            if (ticks.HasValue)
            {
                source.RunTimeTicks ??= ticks;
            }

            if (size.HasValue)
            {
                // El tamaño del .strm no es el del vídeo; el cliente lo usa
                // para la barra de progreso y para calcular seeks.
                source.Size = size;
            }

            if (streams is not null
                && (source.MediaStreams is null || source.MediaStreams.Count == 0))
            {
                source.MediaStreams = streams.ToList();
            }
        }

        return sources;
    }
}
