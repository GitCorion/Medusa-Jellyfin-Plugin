using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catalogo.Playback;

/// <summary>
/// Intercepts video and audio stream requests to ensure items that are .strm
/// never transcode with the invalid container "strm", which causes ffmpeg exit code 234.
/// </summary>
public sealed class CatalogoStreamContainerFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CatalogoStreamContainerFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogoStreamContainerFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public CatalogoStreamContainerFilter(
        ILibraryManager libraryManager,
        ILogger<CatalogoStreamContainerFilter> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (context.ActionArguments.TryGetValue("container", out var containerObj))
        {
            var container = containerObj as string;
            if (string.IsNullOrWhiteSpace(container) || string.Equals(container, "strm", StringComparison.OrdinalIgnoreCase))
            {
                if (context.ActionArguments.TryGetValue("itemId", out var itemIdObj)
                    && itemIdObj is not null
                    && (itemIdObj is Guid g || Guid.TryParse(itemIdObj.ToString(), out g)))
                {
                    var item = _libraryManager.GetItemById(g);
                    if (item != null)
                    {
                        var isStrm = item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true
                            || string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase);
                        if (isStrm)
                        {
                            context.ActionArguments["container"] = "mkv";
                            if (string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.Container))
                            {
                                item.Container = "mkv";
                            }

                            _logger.LogInformation(
                                "CatalogoStreamContainerFilter: Normalized container from '{Original}' to 'mkv' for {Item} ({Id})",
                                container,
                                item.Name,
                                g);
                        }
                    }
                }
            }
        }

        await next().ConfigureAwait(false);
    }
}
