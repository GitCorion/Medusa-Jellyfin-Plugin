using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Catalogo.Configuration;
using Jellyfin.Plugin.Catalogo.Playback;
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
        serviceCollection.AddSingleton<MedusaDataStore>();
        serviceCollection.AddHttpClient<TmdbProxyClient>();
        serviceCollection.AddHttpClient<TraktTokenService>();
        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddHostedService<PlaybackLeaseService>();
        serviceCollection
            .AddControllers()
            .AddApplicationPart(typeof(Plugin).Assembly);

        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, CatalogoStartupFilter>();
        serviceCollection.AddScoped<CatalogoStreamContainerFilter>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.Add<CatalogoStreamContainerFilter>();
        });

        DecorateMediaSourceManager(serviceCollection);
    }

    /// <summary>
    /// Wraps the server's <see cref="IMediaSourceManager"/> so Catalogo
    /// .strm items can skip the remote ffprobe.
    /// </summary>
    /// <param name="serviceCollection">Service collection.</param>
    internal static void DecorateMediaSourceManager(IServiceCollection serviceCollection)
    {
        var descriptor = serviceCollection.LastOrDefault(
            item => item.ServiceType == typeof(IMediaSourceManager));
        if (descriptor is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            provider =>
            {
                var (inner, _) = CreateInner(provider, descriptor);
                return new CatalogoMediaSourceManager(
                    inner,
                    false,
                    provider,
                    provider.GetRequiredService<ILogger<CatalogoMediaSourceManager>>(),
                    GetConfiguration,
                    provider.GetService<IHttpContextAccessor>());
            },
            descriptor.Lifetime));
    }

    private static PluginConfiguration? GetConfiguration()
    {
        try
        {
            return Plugin.Instance?.Configuration;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static (IMediaSourceManager Inner, bool OwnsInner) CreateInner(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IMediaSourceManager instance)
        {
            return (instance, false);
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return ((IMediaSourceManager)descriptor.ImplementationFactory(provider), false);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (
                (IMediaSourceManager)ActivatorUtilities.CreateInstance(
                    provider, descriptor.ImplementationType),
                true);
        }

        throw new InvalidOperationException(
            "The original IMediaSourceManager registration cannot be resolved.");
    }
}
