using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    public sealed class StreamingRowSectionTests
    {
        private static string Movies(params int[] ids) => "{\"results\":[" + string.Join(",", ids.Select(i => $$"""{"id":{{i}},"title":"M{{i}}","posterPath":"/m{{i}}.jpg","originalLanguage":"en"}""")) + "]}";

        private static string Shows(params int[] ids) => "{\"results\":[" + string.Join(",", ids.Select(i => $$"""{"id":{{i}},"name":"T{{i}}","posterPath":"/t{{i}}.jpg","originalLanguage":"en"}""")) + "]}";

        private static StreamingService Service(string slug) => StreamingRowSection.Catalogue.Single(s => s.Slug == slug);

        private static StreamingRowSection Section(RowHarness h, string slug) => new(Service(slug), h.Rows, h.Users.Object, h.Dto.Object, h.Images);

        [Theory]
        [InlineData("Netflix", "Netflix", "8", "US")]
        [InlineData("Prime", "Prime Video", "9|119", "US")]
        [InlineData("Disney", "Disney+", "337", "US")]
        [InlineData("Stan", "Stan", "21", "AU")]
        [InlineData("HBOMax", "HBO Max", "1899", "US")]
        [InlineData("Paramount", "Paramount+", "531|2303|2616", "US")]
        [InlineData("AppleTV", "Apple TV+", "350", "US")]
        public void The_catalogue_has_the_provider_ids_we_rely_on(string slug, string name, string providerIds, string region)
        {
            StreamingService s = Service(slug);
            Assert.Equal(name, s.Name);
            Assert.Equal(providerIds, s.ProviderIds);
            Assert.Equal(region, s.DefaultRegion);
        }

        [Fact]
        public void The_catalogue_slugs_are_unique_so_section_ids_and_cache_keys_cannot_collide()
        {
            Assert.Equal(StreamingRowSection.Catalogue.Length, StreamingRowSection.Catalogue.Select(s => s.Slug).Distinct().Count());
        }

        [Fact]
        public void A_section_is_named_after_its_service()
        {
            RowHarness h = new(FakeHandler.Json(_ => null));
            StreamingRowSection s = Section(h, "Disney");
            Assert.Equal("Streaming_Disney", s.Section);
            Assert.Equal("Popular on Disney+", s.DisplayText);
        }

        [Fact]
        public void ListFor_keys_the_list_by_service_region_and_kind()
        {
            RowHarness h = new(FakeHandler.Json(_ => null));
            Assert.Equal("stream-Netflix-US-both", Section(h, "Netflix").ListFor("US", "both").Key);
            Assert.Equal("stream-Stan-AU-tv", Section(h, "Stan").ListFor("AU", "tv").Key);
            Assert.Equal("stream-Paramount-GB-movies", Section(h, "Paramount").ListFor("GB", "movies").Key);
        }

        [Fact]
        public void ListFor_asks_Seerr_for_two_pages_of_movies_then_shows_with_url_encoded_providers()
        {
            RowHarness h = new(FakeHandler.Json(_ => Movies()));
            Section(h, "Paramount").ListFor("US", "both").Fetch();

            const string q = "watchProviders=531%7C2303%7C2616&watchRegion=US&sortBy=popularity.desc";
            Assert.Equal(new[]
            {
                $"/api/v1/discover/movies?{q}&page=1",
                $"/api/v1/discover/movies?{q}&page=2",
                $"/api/v1/discover/tv?{q}&page=1",
                $"/api/v1/discover/tv?{q}&page=2",
            }, h.Handler.Urls);
        }

        [Fact]
        public void ListFor_uses_the_region_it_is_given()
        {
            RowHarness h = new(FakeHandler.Json(_ => Movies()));
            Section(h, "Stan").ListFor("AU", "both").Fetch();
            Assert.All(h.Handler.Urls, u => Assert.Contains("watchRegion=AU", u));
            Assert.All(h.Handler.Urls, u => Assert.Contains("watchProviders=21&", u));
        }

        [Fact]
        public void ListFor_interleaves_movies_and_shows()
        {
            RowHarness h = new(FakeHandler.Json(url => url.Contains("page=2") ? Movies() : url.StartsWith("/api/v1/discover/movies") ? Movies(1, 2, 3) : Shows(101, 102)));
            List<SeerrEntry> list = Section(h, "Netflix").ListFor("US", "both").Fetch();
            Assert.Equal(new[] { 1, 101, 2, 102, 3 }, list.Select(e => e.TmdbId));
            Assert.Equal(new[] { false, true, false, true, false }, list.Select(e => e.IsTv));
        }

        [Fact]
        public void ListFor_movies_only_never_asks_for_shows()
        {
            RowHarness h = new(FakeHandler.Json(_ => Movies(1, 2)));
            List<SeerrEntry> list = Section(h, "Netflix").ListFor("US", "movies").Fetch();
            Assert.All(h.Handler.Urls, u => Assert.StartsWith("/api/v1/discover/movies?", u));
            Assert.All(list, e => Assert.False(e.IsTv));
        }

        [Fact]
        public void ListFor_shows_only_never_asks_for_movies()
        {
            RowHarness h = new(FakeHandler.Json(_ => Shows(1, 2)));
            List<SeerrEntry> list = Section(h, "Netflix").ListFor("US", "tv").Fetch();
            Assert.All(h.Handler.Urls, u => Assert.StartsWith("/api/v1/discover/tv?", u));
            Assert.All(list, e => Assert.True(e.IsTv));
        }

        [Fact]
        public void ListFor_is_capped_at_40_titles()
        {
            RowHarness h = new(FakeHandler.Json(url => url.StartsWith("/api/v1/discover/movies")
                ? Movies(Enumerable.Range(1, 30).ToArray())
                : Shows(Enumerable.Range(1001, 30).ToArray())));
            Assert.Equal(40, Section(h, "Netflix").ListFor("US", "both").Fetch().Count);
        }

        [Fact]
        public void GetResults_for_a_title_the_server_lacks_returns_a_request_card()
        {
            RowHarness h = new(FakeHandler.Json(url => url.Contains("page=1") && url.StartsWith("/api/v1/discover/movies") ? Movies(603) : Movies()));
            StreamingRowSection s = Section(h, "Netflix");
            QueryResult<BaseItemDto> result = s.GetResults(h.PayloadFor("bob"), new QueryCollection());

            BaseItemDto card = Assert.Single(result.Items);
            Assert.Equal("M603", card.Name);
            Assert.Equal("603", card.ProviderIds["Jellyseerr"]);
            Assert.Equal("1", card.ProviderIds["HssRemote"]);
            Assert.Equal("movie", card.SourceType);
        }
    }

    public sealed class ComingSoonSectionTests
    {
        private static string Day(int offset) => DateTime.UtcNow.Date.AddDays(offset).ToString("yyyy-MM-dd");

        private static string Movie(int id, string date, int status = 0) =>
            "{\"id\":" + id + ",\"title\":\"M" + id + "\",\"releaseDate\":\"" + date + "\",\"posterPath\":\"/p.jpg\",\"originalLanguage\":\"en\",\"mediaInfo\":{\"status\":" + status + "}}";

        private static string Show(int id, string date, int status = 0) =>
            "{\"id\":" + id + ",\"name\":\"T" + id + "\",\"firstAirDate\":\"" + date + "\",\"posterPath\":\"/p.jpg\",\"originalLanguage\":\"en\",\"mediaInfo\":{\"status\":" + status + "}}";

        private static string Page(params string[] results) => "{\"results\":[" + string.Join(",", results) + "]}";

        private static FakeHandler Upcoming() => FakeHandler.Json(url => url.Contains("page=1")
            ? (url.StartsWith("/api/v1/discover/movies/upcoming")
                ? Page(Movie(1, Day(10)), Movie(2, Day(-1)), Movie(3, Day(0)), Movie(4, Day(5), status: 4), Movie(5, Day(6), status: 5), Movie(6, Day(7), status: 3))
                : Page(Show(11, Day(2)), Show(12, Day(-3)), Show(13, Day(20), status: 2)))
            : Page());

        [Fact]
        public void The_list_is_cached_under_its_own_key()
        {
            RowHarness h = new(Upcoming());
            Assert.Equal("coming-soon", new ComingSoonSection(h.Rows, h.Users.Object, h.Dto.Object, h.Images).List().Key);
        }

        [Fact]
        public void Only_unreleased_titles_that_are_not_yet_available_are_kept_in_date_order()
        {
            RowHarness h = new(Upcoming());
            List<SeerrEntry> list = new ComingSoonSection(h.Rows, h.Users.Object, h.Dto.Object, h.Images).List().Fetch();

            // dropped: released yesterday (2) and three days ago (12); status 4/5 = partly/fully available (4, 5)
            Assert.Equal(new[] { 3, 11, 6, 1, 13 }, list.Select(e => e.TmdbId));
            Assert.Equal(list.Select(e => e.Date).OrderBy(d => d, StringComparer.Ordinal), list.Select(e => e.Date));
            Assert.All(list, e => Assert.True(e.MediaStatus < 4));
        }

        [Fact]
        public void Upcoming_endpoints_are_read_three_pages_deep()
        {
            RowHarness h = new(Upcoming());
            new ComingSoonSection(h.Rows, h.Users.Object, h.Dto.Object, h.Images).List().Fetch();
            Assert.Equal(new[]
            {
                "/api/v1/discover/movies/upcoming?page=1", "/api/v1/discover/movies/upcoming?page=2", "/api/v1/discover/movies/upcoming?page=3",
                "/api/v1/discover/tv/upcoming?page=1", "/api/v1/discover/tv/upcoming?page=2", "/api/v1/discover/tv/upcoming?page=3",
            }, h.Handler.Urls);
        }

        [Fact]
        public void Each_card_says_when_it_releases()
        {
            RowHarness h = new(FakeHandler.Json(url => url.Contains("page=1") && url.StartsWith("/api/v1/discover/movies/upcoming")
                ? Page(Movie(1, DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd"))) : Page()));
            ComingSoonSection s = new(h.Rows, h.Users.Object, h.Dto.Object, h.Images);
            QueryResult<BaseItemDto> result = s.GetResults(h.PayloadFor("bob"), new QueryCollection());

            string expected = "Releases " + DateTime.UtcNow.Date.AddDays(30).ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(expected, Assert.Single(result.Items).ProviderIds["HssSubtitle"]);
        }
    }

    public sealed class MyRequestsSectionTests
    {
        [Theory]
        [InlineData(1, 1, "Awaiting approval")]
        [InlineData(1, 5, "Awaiting approval")]
        [InlineData(3, 1, "Declined")]
        [InlineData(3, 5, "Declined")]
        [InlineData(4, 1, "Failed")]
        [InlineData(2, 5, "Available")]
        [InlineData(2, 4, "Partly available")]
        [InlineData(2, 3, "Downloading")]
        [InlineData(2, 2, "Approved")]
        [InlineData(2, 1, "Approved")]
        [InlineData(2, 0, "Approved")]
        [InlineData(0, 5, "Available")]
        public void Status_combines_the_request_and_media_status(int request, int media, string expected)
        {
            Assert.Equal(expected, MyRequestsSection.Status(request, media));
        }

        private static string Movie(int id) => $$"""{"id":{{id}},"title":"Movie {{id}}","posterPath":"/p.jpg","originalLanguage":"en"}""";

        private static string Show(int id) => $$"""{"id":{{id}},"name":"Show {{id}}","posterPath":"/p.jpg","originalLanguage":"en"}""";

        private static FakeHandler Seerr() => FakeHandler.Json(url =>
        {
            if (url.StartsWith("/api/v1/user?q=")) return """{"results":[{"id":7,"jellyfinUsername":"bob"}]}""";
            if (url.StartsWith("/api/v1/user/7/requests")) return """
                {"results":[
                  {"status":2,"media":{"tmdbId":11,"mediaType":"movie","status":5}},
                  {"status":1,"media":{"tmdbId":22,"mediaType":"tv","status":1}},
                  {"status":2,"media":{"tmdbId":11,"mediaType":"movie","status":5}},
                  {"status":3,"media":{"tmdbId":33,"mediaType":"movie","status":1}},
                  {"status":2}
                ]}
                """;
            if (url == "/api/v1/movie/11") return Movie(11);
            if (url == "/api/v1/movie/33") return Movie(33);
            if (url == "/api/v1/tv/22") return Show(22);
            return null;
        });

        [Fact]
        public void The_request_list_url_has_no_sort_parameter_because_Seerr_answers_400_to_it()
        {
            RowHarness h = new(Seerr());
            h.Rows.GetUserRequests(7);

            string url = Assert.Single(h.Handler.Urls);
            Assert.Equal("/api/v1/user/7/requests?take=40", url);
            Assert.DoesNotContain("sort=", url);
        }

        [Fact]
        public void My_requests_lists_each_title_once_with_its_status()
        {
            RowHarness h = new(Seerr());
            MyRequestsSection s = new(h.Rows, h.Users.Object, h.Dto.Object, h.Images);
            QueryResult<BaseItemDto> result = s.GetResults(h.PayloadFor("bob"), new QueryCollection());

            Assert.Equal(new[] { "Movie 11", "Show 22", "Movie 33" }, result.Items.Select(i => i.Name));
            Assert.Equal(new[] { "Available", "Awaiting approval", "Declined" }, result.Items.Select(i => i.ProviderIds["HssSubtitle"]));
            Assert.Equal(new[] { "movie", "tv", "movie" }, result.Items.Select(i => i.SourceType));
            Assert.DoesNotContain(h.Handler.Urls, u => u.Contains("sort="));
        }

        [Fact]
        public void A_user_unknown_to_Seerr_gets_an_empty_row()
        {
            RowHarness h = new(FakeHandler.Json(url => url.StartsWith("/api/v1/user?q=") ? """{"results":[]}""" : null));
            MyRequestsSection s = new(h.Rows, h.Users.Object, h.Dto.Object, h.Images);
            Assert.Empty(s.GetResults(h.PayloadFor("stranger"), new QueryCollection()).Items);
        }
    }
}
