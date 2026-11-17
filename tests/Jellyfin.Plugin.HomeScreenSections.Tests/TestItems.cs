using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Moq;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    /// <summary>
    /// Library items for tests. <c>BaseItem.LocationType</c> is derived from <c>Path</c> through a static file system that only
    /// exists inside a running server, so these pin it (and the path) explicitly.
    /// </summary>
    internal static class TestItems
    {
        public static T File<T>(string path = "/media/movies/Film (2020)/Film.mkv") where T : BaseItem, new() => Make<T>(path, LocationType.FileSystem);

        public static T Remote<T>(string path = "gelato://stub/tmdb:603") where T : BaseItem, new() => Make<T>(path, LocationType.Remote);

        public static T Make<T>(string? path, LocationType location) where T : BaseItem, new()
        {
            Mock<T> mock = new() { CallBase = true };
            mock.SetupGet(i => i.LocationType).Returns(location);
            mock.Object.Path = path;
            mock.Object.Id = Guid.NewGuid();
            return mock.Object;
        }
    }
}
