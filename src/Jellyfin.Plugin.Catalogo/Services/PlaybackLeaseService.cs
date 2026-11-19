using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Services;

/// <summary>
/// Keeps a Catalogo playback lease alive while Jellyfin reports playback.
/// This prevents the mover from sending a pack to 1fichier mid-episode.
/// </summary>
public sealed class PlaybackLeaseService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly CatalogoProxyClient _proxy;
    private readonly ILogger<PlaybackLeaseService> _logger;
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackLeaseService"/> class.
    /// </summary>
    public PlaybackLeaseService(
        ISessionManager sessions,
        CatalogoProxyClient proxy,
        ILogger<PlaybackLeaseService> logger)
    {
        _sessions = sessions;
        _proxy = proxy;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackProgress += OnPlaybackProgress;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _subscribed = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackProgress -= OnPlaybackProgress;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _subscribed = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackProgress -= OnPlaybackProgress;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _subscribed = false;
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs args)
        => _ = LeaseAsync(args.Item?.Path, args.Session);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
        => _ = LeaseAsync(args.Item?.Path, args.Session);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
        => _ = ReleaseAsync(args.Item?.Path, args.Session);

    private async Task LeaseAsync(string? path, SessionInfo? session)
    {
        var (key, _) = StrmResolver.Parse(path);
        if (key is null)
        {
            return;
        }

        try
        {
            await _proxy.LeaseAsync(key, SessionId(session), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo renovar el lease de {Key}", key);
        }
    }

    private async Task ReleaseAsync(string? path, SessionInfo? session)
    {
        var (key, _) = StrmResolver.Parse(path);
        if (key is null)
        {
            return;
        }

        try
        {
            await _proxy.ReleaseLeaseAsync(
                key, SessionId(session), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo liberar el lease de {Key}", key);
        }
    }

    private static string SessionId(SessionInfo? session)
        => $"jellyfin:{session?.Id ?? session?.DeviceId ?? "unknown"}";
}
