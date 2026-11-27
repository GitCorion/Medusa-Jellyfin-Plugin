using Jellyfin.Plugin.Catalogo.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Catalogo;

/// <summary>
/// Medusa companion plugin: exposes the TOD proxy state to Medusa clients.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("50c67d89-41de-4a95-84e2-4511775d4965");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the Jellyfin directory where plugin configuration files live.
    /// ApplicationPaths is protected in the base class, so services reach it
    /// through this property.
    /// </summary>
    public string PluginConfigurationsDirectory => ApplicationPaths.PluginConfigurationsPath;

    /// <inheritdoc />
    public override string Name => "Medusa";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Medusa companion API for the TOD on-demand torrent proxy.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "medusa",
                EmbeddedResourcePath =
                    "Jellyfin.Plugin.Catalogo.Configuration.configPage.html"
            }
        };
    }
}
