using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Jellyfin.Plugin.HomeScreenSections;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

// The plugin keeps its configuration in a process-wide static (HomeScreenSectionsPlugin.Instance), so tests that set it must not
// run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    /// <summary>
    /// Creates the real plugin object once (its constructor only needs mockable services) so that code under test sees
    /// <c>HomeScreenSectionsPlugin.Instance.Configuration</c> exactly as it does in Jellyfin. Each call to
    /// <see cref="Configure"/> installs a fresh PluginConfiguration.
    /// </summary>
    internal static class TestPlugin
    {
        private static readonly object s_lock = new();
        private static string? s_root;

        public static PluginConfiguration Configure(Action<PluginConfiguration>? edit = null)
        {
            lock (s_lock)
            {
                if (s_root == null)
                {
                    s_root = Path.Combine(Path.GetTempPath(), "hss-unit-plugin-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(s_root);
                    Mock<IApplicationPaths> paths = new();
                    paths.SetupGet(p => p.PluginConfigurationsPath).Returns(s_root);
                    paths.SetupGet(p => p.PluginsPath).Returns(s_root);
                    paths.SetupGet(p => p.DataPath).Returns(s_root);
                    paths.SetupGet(p => p.CachePath).Returns(s_root);
                    // BasePlugin reads its configuration through the serializer: hand it a default one.
                    Mock<IXmlSerializer> xml = new();
                    xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(() => new PluginConfiguration());
                    _ = new HomeScreenSectionsPlugin(
                        paths.Object,
                        xml.Object,
                        Mock.Of<IServerConfigurationManager>(),
                        Mock.Of<IServiceProvider>(),
                        Mock.Of<IHomeScreenManager>(),
                        Mock.Of<ITranslationManager>());
                }

                PluginConfiguration cfg = new();
                edit?.Invoke(cfg);
                HomeScreenSectionsPlugin.Instance.UpdateConfiguration(cfg);
                return HomeScreenSectionsPlugin.Instance.Configuration;
            }
        }

        public static IApplicationPaths NewPaths(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "hss-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Mock<IApplicationPaths> paths = new();
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(dir);
            paths.SetupGet(p => p.CachePath).Returns(dir);
            paths.SetupGet(p => p.DataPath).Returns(dir);
            return paths.Object;
        }
    }

    /// <summary>An HttpMessageHandler that answers from a delegate and records every request it saw.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> m_respond;

        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        /// <summary>Path and query of each request, in order.</summary>
        public IReadOnlyList<string> Urls => Requests.Select(r => r.RequestUri!.PathAndQuery).ToList();

        public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => m_respond = respond;

        public static FakeHandler Json(Func<string, string?> byPathAndQuery) => new(req =>
        {
            string? body = byPathAndQuery(req.RequestUri!.PathAndQuery);
            return body == null ? (HttpStatusCode.NotFound, "{}") : (HttpStatusCode.OK, body);
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            (HttpStatusCode status, string body) = m_respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    internal sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler m_handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => m_handler = handler;

        public HttpClient CreateClient(string name) => new(m_handler, disposeHandler: false);
    }

    /// <summary>Logger that keeps what was written, for tests that assert on (or against) log output.</summary>
    internal sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }
}

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    /// <summary>A SeerrRowsService plus the other constructor dependencies of the Seerr-backed rows.</summary>
    internal sealed class RowHarness
    {
        public const string SeerrHost = "seerr.test";

        public FakeHandler Handler { get; }

        public Services.SeerrRowsService Rows { get; }

        public Mock<MediaBrowser.Controller.Library.IUserManager> Users { get; } = new();

        public Mock<MediaBrowser.Controller.Dto.IDtoService> Dto { get; } = new();

        public Services.ImageCacheService Images { get; }

        public Mock<MediaBrowser.Controller.Library.ILibraryManager> Library { get; } = new();

        public RowHarness(FakeHandler handler, Action<PluginConfiguration>? config = null)
        {
            TestPlugin.Configure(c =>
            {
                c.JellyseerrUrl = "http://" + SeerrHost + ":5055";
                c.JellyseerrApiKey = "test-key";
                c.JellyseerrPreferredLanguages = "en";
                config?.Invoke(c);
            });
            Handler = handler;
            Library.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
                .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());
            IApplicationPaths paths = TestPlugin.NewPaths(out _);
            Rows = new Services.SeerrRowsService(new FakeHttpClientFactory(handler), Library.Object, paths, new ListLogger<Services.SeerrRowsService>());
            // Poster downloads answer 404 so the image cache hands the original URL back.
            Images = new Services.ImageCacheService(new ListLogger<Services.ImageCacheService>(), paths,
                new HttpClient(new FakeHandler(_ => (HttpStatusCode.NotFound, "")), disposeHandler: false));
        }

        /// <summary>Makes <see cref="Users"/> know a user and returns the payload that asks for their rows.</summary>
        public Model.Dto.HomeScreenSectionPayload PayloadFor(string username)
        {
            Jellyfin.Database.Implementations.Entities.User user = new(username, "auth", "reset") { Id = Guid.NewGuid() };
            Users.Setup(u => u.GetUserById(user.Id)).Returns(user);
            return new Model.Dto.HomeScreenSectionPayload { UserId = user.Id };
        }
    }
}
