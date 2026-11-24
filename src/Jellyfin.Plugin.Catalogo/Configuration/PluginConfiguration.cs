using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Catalogo.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the local Catalogo proxy base URL.
    /// </summary>
    public string ProxyUrl { get; set; } = "http://127.0.0.1:8773";

    /// <summary>
    /// Gets or sets the HTTP timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the timeout for starting a cold prepare. TOD may need to
    /// fetch torrent metadata before answering, so it is longer than the
    /// status timeout. Medusa status calls keep using <see cref="TimeoutSeconds"/>.
    /// </summary>
    public int PrepareTimeoutSeconds { get; set; } = 40;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin clients that are not
    /// Medusa may prepare a cold catalog item from PlaybackInfo. Medusa always
    /// keeps its own flow. Disabled by default.
    /// </summary>
    public bool NativeMode { get; set; }

    /// <summary>
    /// Gets or sets how long a native PlaybackInfo may wait for the stream to
    /// be ready after starting a prepare. Zero disables the wait. Medusa never
    /// waits. Clamped to 0-30 seconds by the playback path.
    /// </summary>
    public int NativePrebufferSeconds { get; set; }
}
