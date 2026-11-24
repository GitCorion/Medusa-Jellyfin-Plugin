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
    /// Gets or sets a value indicating whether Jellyfin clients that are not
    /// Medusa may prepare a cold catalog item from PlaybackInfo. Medusa always
    /// keeps its own flow. Disabled by default.
    /// </summary>
    public bool NativeMode { get; set; }
}
