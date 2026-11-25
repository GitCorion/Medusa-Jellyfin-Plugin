using System.Reflection;
using Jellyfin.Plugin.Catalogo.Playback;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Catalogo.Tests;

public class CatalogoStreamContainerFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("strm")]
    public async Task OnActionExecutionAsync_WhenStrmItemAndContainerMissingOrStrm_NormalizesToMkv(string? initialContainer)
    {
        var itemId = Guid.NewGuid();
        var item = new Movie
        {
            Id = itemId,
            Path = "/mnt/movies/Test (2026)/Test (2026).strm",
            Container = initialContainer
        };

        var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)library).Handler =
            (method, args) => method.Name == nameof(ILibraryManager.GetItemById) && args != null && (Guid)args[0]! == itemId
                ? item
                : null;

        var filter = new CatalogoStreamContainerFilter(
            library,
            NullLogger<CatalogoStreamContainerFilter>.Instance);

        var actionArguments = new Dictionary<string, object?>
        {
            ["itemId"] = itemId,
            ["container"] = initialContainer
        };

        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor());

        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            actionArguments,
            controller: new object());

        var nextCalled = false;
        ActionExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()));
        };

        await filter.OnActionExecutionAsync(executingContext, next);

        Assert.True(nextCalled);
        Assert.Equal("mkv", executingContext.ActionArguments["container"]);
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WhenNonStrmContainerProvided_PreservesContainer()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie
        {
            Id = itemId,
            Path = "/mnt/movies/Test (2026)/Test (2026).strm",
            Container = "mkv"
        };

        var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)library).Handler =
            (method, args) => method.Name == nameof(ILibraryManager.GetItemById) && args != null && (Guid)args[0]! == itemId
                ? item
                : null;

        var filter = new CatalogoStreamContainerFilter(
            library,
            NullLogger<CatalogoStreamContainerFilter>.Instance);

        var actionArguments = new Dictionary<string, object?>
        {
            ["itemId"] = itemId,
            ["container"] = "mp4"
        };

        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor());

        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            actionArguments,
            controller: new object());

        var nextCalled = false;
        ActionExecutionDelegate next = () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()));
        };

        await filter.OnActionExecutionAsync(executingContext, next);

        Assert.True(nextCalled);
        Assert.Equal("mp4", executingContext.ActionArguments["container"]);
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(
            MethodInfo? targetMethod, object?[]? args)
            => Handler?.Invoke(targetMethod!, args);
    }
}
