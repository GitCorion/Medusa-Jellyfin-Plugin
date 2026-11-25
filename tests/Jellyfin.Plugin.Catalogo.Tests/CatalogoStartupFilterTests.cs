using System.Reflection;
using Jellyfin.Plugin.Catalogo.Playback;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class CatalogoStartupFilterTests
{
    [Fact]
    public async Task Configure_RewritesStrmVideosStreamWithoutExtensionToMkv()
    {
        var itemId = Guid.Parse("16f49990e3f8dc731709333e96426991");
        var strmItem = new Movie { Id = itemId, Path = "/mnt/catalogo/movies/Test (2026)/Test (2026).strm", Container = "strm" };
        var filter = new CatalogoStartupFilter(NullLogger<CatalogoStartupFilter>.Instance);
        var app = new ApplicationBuilder(new LibraryServiceProvider(itemId, strmItem));

        var configure = filter.Configure(_ => { });
        configure(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = app.ApplicationServices };
        context.Request.Path = "/Videos/16f49990e3f8dc731709333e96426991/stream";

        await pipeline(context);

        Assert.Equal("/Videos/16f49990e3f8dc731709333e96426991/stream.mkv", context.Request.Path.Value);
    }

    [Fact]
    public async Task Configure_DoesNotRewriteRegularMp4ItemWithoutExtension()
    {
        var itemId = Guid.Parse("16f49990e3f8dc731709333e96426991");
        var mp4Item = new Movie { Id = itemId, Path = "/mnt/library/Movie (2026)/Movie.mp4", Container = "mp4" };
        var filter = new CatalogoStartupFilter(NullLogger<CatalogoStartupFilter>.Instance);
        var app = new ApplicationBuilder(new LibraryServiceProvider(itemId, mp4Item));

        var configure = filter.Configure(_ => { });
        configure(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = app.ApplicationServices };
        context.Request.Path = "/Videos/16f49990e3f8dc731709333e96426991/stream";

        await pipeline(context);

        Assert.Equal("/Videos/16f49990e3f8dc731709333e96426991/stream", context.Request.Path.Value);
    }

    [Fact]
    public async Task Configure_DoesNotRewriteVideosStreamWithExtension()
    {
        var itemId = Guid.Parse("16f49990e3f8dc731709333e96426991");
        var strmItem = new Movie { Id = itemId, Path = "/mnt/catalogo/movies/Test.strm", Container = "strm" };
        var filter = new CatalogoStartupFilter(NullLogger<CatalogoStartupFilter>.Instance);
        var app = new ApplicationBuilder(new LibraryServiceProvider(itemId, strmItem));

        var configure = filter.Configure(_ => { });
        configure(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = app.ApplicationServices };
        context.Request.Path = "/Videos/16f49990e3f8dc731709333e96426991/stream.mp4";

        await pipeline(context);

        Assert.Equal("/Videos/16f49990e3f8dc731709333e96426991/stream.mp4", context.Request.Path.Value);
    }

    [Fact]
    public async Task Configure_DoesNotRewriteOtherPaths()
    {
        var filter = new CatalogoStartupFilter(NullLogger<CatalogoStartupFilter>.Instance);
        var app = new ApplicationBuilder(new LibraryServiceProvider(Guid.Empty, null));

        var configure = filter.Configure(_ => { });
        configure(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = app.ApplicationServices };
        context.Request.Path = "/Items/16f49990e3f8dc731709333e96426991/PlaybackInfo";

        await pipeline(context);

        Assert.Equal("/Items/16f49990e3f8dc731709333e96426991/PlaybackInfo", context.Request.Path.Value);
    }

    private sealed class LibraryServiceProvider : IServiceProvider
    {
        private readonly ILibraryManager _library;

        public LibraryServiceProvider(Guid id, Movie? item)
        {
            var proxy = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            ((LibraryProxy)(object)proxy).Handler = (method, args) =>
                method.Name == nameof(ILibraryManager.GetItemById) && args != null && (Guid)args[0]! == id ? item : null;
            _library = proxy;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILibraryManager) ? _library : null;
    }

    public class LibraryProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler?.Invoke(targetMethod!, args);
    }
}
