using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    public sealed class SeerrRowsServiceTests
    {
        internal static SeerrRowsService NewService(FakeHandler handler, out string dir, string? existingDir = null)
        {
            var paths = TestPlugin.NewPaths(out dir);
            if (existingDir != null)
            {
                dir = existingDir;
                var shared = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
                shared.SetupGet(p => p.PluginConfigurationsPath).Returns(existingDir);
                paths = shared.Object;
            }

            return new SeerrRowsService(new FakeHttpClientFactory(handler), Mock.Of<ILibraryManager>(), paths, NullLogger<SeerrRowsService>.Instance);
        }

        private static SeerrEntry Entry(int id, string title = "Title") => new(false, id, title, "2026-01-01", 7.5f, "/p.jpg", 0);

        private static JObject Json(string json) => JObject.Parse(json);

        // ---- ParseResult --------------------------------------------------------------------------------------

        [Fact]
        public void ParseResult_maps_a_movie()
        {
            SeerrEntry? e = SeerrRowsService.ParseResult(
                Json("""{"id":603,"title":"The Matrix","releaseDate":"1999-03-31","voteAverage":8.2,"posterPath":"/m.jpg","originalLanguage":"en","mediaInfo":{"status":5}}"""),
                false, new[] { "en" });

            Assert.NotNull(e);
            Assert.False(e!.IsTv);
            Assert.Equal(603, e.TmdbId);
            Assert.Equal("The Matrix", e.Title);
            Assert.Equal("1999-03-31", e.Date);
            Assert.Equal(8.2f, e.Rating, 0.01f);
            Assert.Equal("/m.jpg", e.PosterPath);
            Assert.Equal(5, e.MediaStatus);
        }

        [Fact]
        public void ParseResult_maps_a_show_with_name_and_firstAirDate()
        {
            SeerrEntry? e = SeerrRowsService.ParseResult(
                Json("""{"id":1399,"name":"Game of Thrones","firstAirDate":"2011-04-17","posterPath":"/g.jpg","originalLanguage":"en"}"""),
                true, new[] { "en" });

            Assert.NotNull(e);
            Assert.True(e!.IsTv);
            Assert.Equal("Game of Thrones", e.Title);
            Assert.Equal("2011-04-17", e.Date);
            Assert.Equal(0f, e.Rating);
            Assert.Equal(0, e.MediaStatus);
        }

        [Fact]
        public void ParseResult_falls_back_to_the_other_title_field()
        {
            SeerrEntry? e = SeerrRowsService.ParseResult(Json("""{"id":1,"title":"Only a title","posterPath":"/a.jpg"}"""), true, Array.Empty<string>());
            Assert.Equal("Only a title", e?.Title);
        }

        [Fact]
        public void ParseResult_drops_adult_titles()
        {
            Assert.Null(SeerrRowsService.ParseResult(Json("""{"id":1,"title":"X","posterPath":"/a.jpg","adult":true}"""), false, Array.Empty<string>()));
            Assert.NotNull(SeerrRowsService.ParseResult(Json("""{"id":1,"title":"X","posterPath":"/a.jpg","adult":false}"""), false, Array.Empty<string>()));
        }

        [Fact]
        public void ParseResult_drops_titles_outside_the_preferred_languages()
        {
            const string ja = """{"id":1,"title":"X","posterPath":"/a.jpg","originalLanguage":"ja"}""";
            Assert.Null(SeerrRowsService.ParseResult(Json(ja), false, new[] { "en" }));
            Assert.NotNull(SeerrRowsService.ParseResult(Json(ja), false, new[] { "en", "ja" }));
            Assert.NotNull(SeerrRowsService.ParseResult(Json(ja), false, Array.Empty<string>())); // no preference = everything
            Assert.Null(SeerrRowsService.ParseResult(Json("""{"id":1,"title":"X","posterPath":"/a.jpg"}"""), false, new[] { "en" })); // language unknown
        }

        [Theory]
        [InlineData("""{"title":"X","posterPath":"/a.jpg"}""")]
        [InlineData("""{"id":1,"posterPath":"/a.jpg"}""")]
        [InlineData("""{"id":1,"title":"","posterPath":"/a.jpg"}""")]
        [InlineData("""{"id":1,"title":"X"}""")]
        [InlineData("""{"id":1,"title":"X","posterPath":""}""")]
        [InlineData("""{"id":1,"title":"X","posterPath":null}""")]
        public void ParseResult_drops_titles_missing_id_title_or_poster(string json)
        {
            Assert.Null(SeerrRowsService.ParseResult(Json(json), false, Array.Empty<string>()));
        }

        // ---- Seerr HTTP ---------------------------------------------------------------------------------------

        private static string Page(params string[] results) => "{\"results\":[" + string.Join(",", results) + "]}";

        private static string Movie(int id, string lang = "en") => $$"""{"id":{{id}},"title":"M{{id}}","posterPath":"/{{id}}.jpg","originalLanguage":"{{lang}}"}""";

        [Fact]
        public void FetchDiscover_pages_through_the_endpoint_and_authenticates()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055/"; c.JellyseerrApiKey = "KEY123"; c.JellyseerrPreferredLanguages = "en"; });
            FakeHandler handler = FakeHandler.Json(url => url.Contains("page=1") ? Page(Movie(1), Movie(2)) : Page(Movie(3)));
            SeerrRowsService svc = NewService(handler, out _);

            List<SeerrEntry> list = svc.FetchDiscover(false, "/api/v1/discover/movies?watchProviders=8&watchRegion=US", 2);

            Assert.Equal(new[] { 1, 2, 3 }, list.Select(e => e.TmdbId));
            Assert.Equal(new[] { "/api/v1/discover/movies?watchProviders=8&watchRegion=US&page=1", "/api/v1/discover/movies?watchProviders=8&watchRegion=US&page=2" }, handler.Urls);
            Assert.All(handler.Requests, r =>
            {
                Assert.Equal("seerr.test", r.RequestUri!.Host);
                Assert.Equal("KEY123", Assert.Single(r.Headers.GetValues("X-Api-Key")));
            });
        }

        [Fact]
        public void FetchDiscover_starts_the_query_string_when_the_path_has_none()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055"; c.JellyseerrApiKey = "k"; });
            FakeHandler handler = FakeHandler.Json(_ => Page());
            NewService(handler, out _).FetchDiscover(true, "/api/v1/discover/tv/upcoming", 1);
            Assert.Equal(new[] { "/api/v1/discover/tv/upcoming?page=1" }, handler.Urls);
        }

        [Fact]
        public void FetchDiscover_stops_at_the_first_failed_page_and_keeps_what_it_has()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055"; c.JellyseerrApiKey = "k"; });
            FakeHandler handler = FakeHandler.Json(url => url.Contains("page=1") ? Page(Movie(1)) : null);
            List<SeerrEntry> list = NewService(handler, out _).FetchDiscover(false, "/api/v1/discover/movies", 3);
            Assert.Equal(new[] { 1 }, list.Select(e => e.TmdbId));
            Assert.Equal(2, handler.Urls.Count); // page 3 was never asked for
        }

        [Fact]
        public void FetchDiscover_applies_the_configured_languages()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055"; c.JellyseerrApiKey = "k"; c.JellyseerrPreferredLanguages = "en, ko"; });
            FakeHandler handler = FakeHandler.Json(_ => Page(Movie(1, "en"), Movie(2, "ja"), Movie(3, "ko")));
            List<SeerrEntry> list = NewService(handler, out _).FetchDiscover(false, "/x", 1);
            Assert.Equal(new[] { 1, 3 }, list.Select(e => e.TmdbId));
        }

        [Fact]
        public void Nothing_is_requested_without_a_Seerr_url()
        {
            TestPlugin.Configure(c => c.JellyseerrUrl = "");
            FakeHandler handler = FakeHandler.Json(_ => Page(Movie(1)));
            Assert.Empty(NewService(handler, out _).FetchDiscover(false, "/x", 1));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void GetDetails_caches_a_success_but_not_a_failure()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055"; c.JellyseerrApiKey = "k"; });
            int calls = 0;
            bool up = false;
            FakeHandler handler = FakeHandler.Json(url =>
            {
                calls++;
                return up ? Movie(42) : null;
            });
            SeerrRowsService svc = NewService(handler, out _);

            Assert.Null(svc.GetDetails(false, 42));
            Assert.Null(svc.GetDetails(false, 42));
            Assert.Equal(2, calls); // the failure was retried, not remembered

            up = true;
            Assert.Equal("M42", svc.GetDetails(false, 42)?.Title);
            Assert.Equal("M42", svc.GetDetails(false, 42)?.Title);
            Assert.Equal(3, calls);
            Assert.Equal("/api/v1/movie/42", handler.Urls.Last());
        }

        [Fact]
        public void GetSeerrUserId_matches_the_jellyfin_username_exactly_and_remembers_it()
        {
            TestPlugin.Configure(c => { c.JellyseerrUrl = "http://seerr.test:5055"; c.JellyseerrApiKey = "k"; });
            FakeHandler handler = FakeHandler.Json(_ => """{"results":[{"id":3,"jellyfinUsername":"bobby"},{"id":7,"jellyfinUsername":"bob"}]}""");
            SeerrRowsService svc = NewService(handler, out _);

            Assert.Equal(7, svc.GetSeerrUserId("bob"));
            Assert.Equal(7, svc.GetSeerrUserId("bob"));
            Assert.Single(handler.Requests);
            Assert.Equal("/api/v1/user?q=bob", handler.Urls[0]);
            Assert.Null(svc.GetSeerrUserId("nobody"));
        }

        // ---- list cache ---------------------------------------------------------------------------------------

        private static readonly TimeSpan s_hour = TimeSpan.FromHours(1);
        private static readonly TimeSpan s_alwaysStale = TimeSpan.FromSeconds(-1);

        private static void WaitFor(Func<bool> condition, string what)
        {
            Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "timed out waiting for " + what);
        }

        [Fact]
        public void GetList_serves_a_fresh_list_from_memory()
        {
            SeerrRowsService svc = NewService(FakeHandler.Json(_ => null), out _);
            int fetches = 0;
            Func<List<SeerrEntry>> fetch = () => { fetches++; return new List<SeerrEntry> { Entry(1) }; };

            Assert.Equal(1, svc.GetList("k", s_hour, fetch).Single().TmdbId); // first ever load blocks and fetches
            Assert.Equal(1, svc.GetList("k", s_hour, fetch).Single().TmdbId);
            Assert.Equal(1, fetches);
        }

        [Fact]
        public void GetList_serves_the_stale_list_while_a_background_refresh_runs()
        {
            SeerrRowsService svc = NewService(FakeHandler.Json(_ => null), out _);
            svc.GetList("k", s_hour, () => new List<SeerrEntry> { Entry(1) });

            using ManualResetEventSlim release = new(false);
            int refreshes = 0;
            Func<List<SeerrEntry>> slow = () =>
            {
                Interlocked.Increment(ref refreshes);
                release.Wait(TimeSpan.FromSeconds(10));
                return new List<SeerrEntry> { Entry(2) };
            };

            Task<IReadOnlyList<SeerrEntry>> stale = Task.Run(() => svc.GetList("k", s_alwaysStale, slow));
            Assert.True(stale.Wait(TimeSpan.FromSeconds(5)), "a stale list must not wait for the refresh");
            Assert.Equal(1, stale.Result.Single().TmdbId);

            WaitFor(() => Volatile.Read(ref refreshes) == 1, "the background refresh to start");
            svc.GetList("k", s_alwaysStale, slow); // a second reader must not start a second refresh
            Assert.Equal(1, Volatile.Read(ref refreshes));

            release.Set();
            WaitFor(() => svc.GetList("k", s_hour, () => throw new InvalidOperationException("no fetch expected")).Single().TmdbId == 2, "the refreshed list");
        }

        [Fact]
        public void GetList_reloads_from_disk_on_a_new_instance()
        {
            SeerrRowsService first = NewService(FakeHandler.Json(_ => null), out string dir);
            first.GetList("stream-Netflix-US-both", s_hour, () => new List<SeerrEntry> { Entry(7, "Seven") });

            SeerrRowsService second = NewService(FakeHandler.Json(_ => null), out _, existingDir: dir);
            IReadOnlyList<SeerrEntry> list = second.GetList("stream-Netflix-US-both", s_hour, () => throw new InvalidOperationException("must come from disk"));

            SeerrEntry e = Assert.Single(list);
            Assert.Equal(Entry(7, "Seven"), e);
        }

        [Fact]
        public void GetList_keeps_the_old_list_when_a_refresh_returns_nothing()
        {
            SeerrRowsService svc = NewService(FakeHandler.Json(_ => null), out _);
            svc.GetList("k", s_hour, () => new List<SeerrEntry> { Entry(1) });

            int fetches = 0;
            Func<List<SeerrEntry>> empty = () => { Interlocked.Increment(ref fetches); return new List<SeerrEntry>(); };
            svc.GetList("k", s_alwaysStale, empty);
            WaitFor(() => Volatile.Read(ref fetches) == 1, "the refresh");
            Thread.Sleep(200);

            Assert.Equal(1, svc.GetList("k", TimeSpan.FromHours(24), empty).Single().TmdbId);
            // and it does not hammer Seerr: the retry is scheduled hours out (inside the 24 h window here), not immediately
            Assert.Equal(1, Volatile.Read(ref fetches));
        }

        [Fact]
        public void GetList_with_no_list_and_nothing_to_fetch_is_empty_rather_than_failing()
        {
            SeerrRowsService svc = NewService(FakeHandler.Json(_ => null), out _);
            Assert.Empty(svc.GetList("k", s_hour, () => new List<SeerrEntry>()));
            Assert.Empty(svc.GetList("k2", s_hour, () => throw new InvalidOperationException("seerr down")));
        }

        // ---- stubs --------------------------------------------------------------------------------------------

        [Fact]
        public void IsStub_recognises_remote_items()
        {
            Assert.True(SeerrRowsService.IsStub(TestItems.Remote<Movie>("https://example.test/x")));
            Assert.True(SeerrRowsService.IsStub(TestItems.Make<Movie>(null, LocationType.Remote)));
        }

        [Theory]
        [InlineData("gelato://stub/tmdb:603", true)]
        [InlineData("GELATO://stub/tmdb:603", true)]
        [InlineData("/config/gelato/movies/The Matrix/stub.strm", true)]
        [InlineData("/mnt/media/Movies/The Matrix (1999)/The Matrix.mkv", false)]
        public void IsStub_recognises_Gelato_paths_on_file_items(string path, bool expected)
        {
            Assert.Equal(expected, SeerrRowsService.IsStub(TestItems.File<Movie>(path)));
        }

        [Fact]
        public void IsStub_is_false_for_a_file_item_without_a_path()
        {
            Assert.False(SeerrRowsService.IsStub(TestItems.Make<Movie>(null, LocationType.FileSystem)));
        }
    }
}
