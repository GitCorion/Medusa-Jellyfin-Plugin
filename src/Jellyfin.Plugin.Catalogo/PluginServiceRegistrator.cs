using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.Catalogo.Services;

namespace Jellyfin.Plugin.Catalogo;

/// <summary>
/// Registers plugin services and controllers.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<CatalogoProxyClient>();
        serviceCollection.AddHostedService<PlaybackLeaseService>();
        serviceCollection
            .AddControllers()
            .AddApplicationPart(typeof(Plugin).Assembly);
    }
}
