using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Playback;

/// <summary>
/// Startup filter that injects URL rewriting middleware to ensure .strm video stream
/// requests without an explicit container extension get routed with .mkv, preventing
/// ffmpeg from attempting to output to .strm (code 234).
/// </summary>
public sealed class CatalogoStartupFilter : IStartupFilter
{
    private readonly ILogger<CatalogoStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public CatalogoStartupFilter(ILogger<CatalogoStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var path = context.Request.Path.Value;
                if (!string.IsNullOrEmpty(path)
                    && path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase))
                {
                    var idSpan = path.AsSpan("/Videos/".Length, path.Length - "/Videos/".Length - "/stream".Length);
                    if (Guid.TryParse(idSpan, out var itemId))
                    {
                        var library = context.RequestServices?.GetService<ILibraryManager>();
                        var item = library?.GetItemById(itemId);
                        var isStrm = item != null && (
                            item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true
                            || string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase));
                        if (isStrm)
                        {
                            context.Request.Path = new PathString(path + ".mkv");
                            _logger.LogInformation(
                                "CatalogoStartupFilter: Rewrote path from '{Original}' to '{Rewritten}'",
                                path,
                                context.Request.Path.Value);
                        }
                    }
                }

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }
}
