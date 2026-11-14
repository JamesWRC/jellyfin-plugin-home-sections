using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    public sealed class SectionResultCacheTests : IDisposable
    {
        private static readonly Guid s_user = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid s_otherUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

        private readonly Mock<ILibraryManager> m_library = new();
        private readonly Mock<IUserDataManager> m_userData = new();
        private readonly SectionResultCache m_cache;
        private DateTime m_now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        private int m_computes;

        public SectionResultCacheTests()
        {
            SectionResultCache.Clock = () => m_now;
            m_cache = new SectionResultCache(m_library.Object, m_userData.Object, NullLogger<SectionResultCache>.Instance);
        }

        public void Dispose()
        {
            m_cache.Dispose();
            SectionResultCache.Clock = () => DateTime.UtcNow;
        }

        private static IQueryCollection Query(params (string Key, string Value)[] pairs) =>
            new QueryCollection(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Value)));

        private QueryResult<BaseItemDto> Compute()
        {
            int n = Interlocked.Increment(ref m_computes);
            return new QueryResult<BaseItemDto>(new[] { new BaseItemDto { Name = "compute-" + n } });
        }

        private QueryResult<BaseItemDto> Get(string section = "RecentlyAddedMovies", Guid? user = null, IQueryCollection? query = null, int ttl = 3600, bool shortLived = false)
            => m_cache.GetOrCompute(section, user ?? s_user, query ?? Query(), ttl, shortLived, Compute);

        private void RaiseAdded(BaseItem item) =>
            m_library.Raise(l => l.ItemAdded += null, m_library.Object, new ItemChangeEventArgs { Item = item });

        private void RaiseRemoved(BaseItem item) =>
            m_library.Raise(l => l.ItemRemoved += null, m_library.Object, new ItemChangeEventArgs { Item = item });

        private void RaiseUserData(Guid user) =>
            m_userData.Raise(u => u.UserDataSaved += null, m_userData.Object, new UserDataSaveEventArgs { UserId = user });

        // ---- keys ---------------------------------------------------------------------------------------------

        [Fact]
        public void Identical_requests_are_served_from_the_cache()
        {
            QueryResult<BaseItemDto> a = Get();
            QueryResult<BaseItemDto> b = Get();
            Assert.Equal(1, m_computes);
            Assert.Same(a, b);
        }

        [Fact]
        public void Users_never_share_an_entry()
        {
            Get(user: s_user);
            Get(user: s_otherUser);
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void Sections_never_share_an_entry()
        {
            Get("RecentlyAddedMovies");
            Get("RecentlyAddedShows");
            Assert.Equal(2, m_computes);
        }

        [Theory]
        [InlineData("AdditionalData")]
        [InlineData("Language")]
        [InlineData("EnableRewatching")]
        public void Whitelisted_parameters_make_separate_entries(string parameter)
        {
            Get(query: Query((parameter, "a")));
            Get(query: Query((parameter, "b")));
            Assert.Equal(2, m_computes);
            Get(query: Query((parameter, "a")));
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void Junk_parameters_share_one_entry()
        {
            Get(query: Query(("cachebuster", "1")));
            Get(query: Query(("cachebuster", "2"), ("userId", "x")));
            Get(query: Query());
            Assert.Equal(1, m_computes);
        }

        [Fact]
        public void NextUpDateCutoff_counts_only_its_date()
        {
            Get("NextUp", query: Query(("NextUpDateCutoff", "2026-11-01T10:00:00.0000000Z")));
            Get("NextUp", query: Query(("NextUpDateCutoff", "2026-11-01T23:59:59.9999999Z")));
            Assert.Equal(1, m_computes);
            Get("NextUp", query: Query(("NextUpDateCutoff", "2026-11-02T00:00:00.0000000Z")));
            Assert.Equal(2, m_computes);
        }

        // ---- library changes ----------------------------------------------------------------------------------

        [Fact]
        public void Adding_or_removing_a_library_item_invalidates_ordinary_sections()
        {
            Get();
            RaiseAdded(TestItems.File<Movie>());
            Get();
            Assert.Equal(2, m_computes);
            RaiseRemoved(TestItems.File<Movie>());
            Get();
            Assert.Equal(3, m_computes);
        }

        private static BaseItem IgnoredItem(string kind) => kind switch
        {
            "LiveTvProgram" => new LiveTvProgram(),
            "LiveTvChannel" => new LiveTvChannel(),
            "Person" => new Person(),
            "Genre" => new Genre(),
            "MusicGenre" => new MusicGenre(),
            "Studio" => new Studio(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        [Theory]
        [InlineData("LiveTvProgram")]
        [InlineData("LiveTvChannel")]
        [InlineData("Person")]
        [InlineData("Genre")]
        [InlineData("MusicGenre")]
        [InlineData("Studio")]
        public void Items_that_never_appear_in_a_row_do_not_invalidate(string kind)
        {
            BaseItem item = IgnoredItem(kind);
            Get();
            RaiseAdded(item);
            RaiseRemoved(item);
            Get();
            Assert.Equal(1, m_computes);
        }

        [Fact]
        public void Remote_items_such_as_Gelato_stubs_do_not_invalidate()
        {
            Get();
            RaiseAdded(TestItems.Remote<Movie>());
            RaiseRemoved(TestItems.Remote<Movie>());
            Get();
            Assert.Equal(1, m_computes);
        }

        [Fact]
        public void Metadata_updates_do_not_invalidate_because_ItemUpdated_is_not_subscribed()
        {
            Get();
            m_library.Raise(l => l.ItemUpdated += null, m_library.Object, new ItemChangeEventArgs { Item = TestItems.File<Movie>() });
            Get();
            Assert.Equal(1, m_computes);
            // and nothing was ever hooked up to it
            m_library.VerifyAdd(l => l.ItemUpdated += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Never);
        }

        [Fact]
        public void The_cache_listens_to_additions_removals_and_user_data_only()
        {
            m_library.VerifyAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);
            m_library.VerifyAdd(l => l.ItemRemoved += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);
            m_userData.VerifyAdd(u => u.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
        }

        [Fact]
        public void After_Dispose_events_are_ignored()
        {
            Get();
            m_cache.Dispose();
            RaiseAdded(TestItems.File<Movie>());
            RaiseUserData(s_user);
            Get();
            Assert.Equal(1, m_computes);
        }

        // ---- per-user sections --------------------------------------------------------------------------------

        [Theory]
        [InlineData("ContinueWatching")]
        [InlineData("NextUp")]
        [InlineData("ContinueWatchingNextUp")]
        [InlineData("WatchAgain")]
        [InlineData("MyList")]
        public void Per_user_sections_ignore_library_changes_but_follow_the_users_own_data(string section)
        {
            Get(section);
            RaiseAdded(TestItems.File<Movie>());
            RaiseRemoved(TestItems.File<Movie>());
            Get(section);
            Assert.Equal(1, m_computes);

            RaiseUserData(s_otherUser); // somebody else watching something is irrelevant
            Get(section);
            Assert.Equal(1, m_computes);

            RaiseUserData(s_user);
            Get(section);
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void Ordinary_sections_follow_the_users_own_data_too_but_not_other_users()
        {
            Get();
            RaiseUserData(s_otherUser);
            Get();
            Assert.Equal(1, m_computes);
            RaiseUserData(s_user);
            Get();
            Assert.Equal(2, m_computes);
        }

        // ---- lifetime -----------------------------------------------------------------------------------------

        [Fact]
        public void An_entry_expires_after_the_configured_time()
        {
            Get(ttl: 120);
            m_now += TimeSpan.FromSeconds(119);
            Get(ttl: 120);
            Assert.Equal(1, m_computes);
            m_now += TimeSpan.FromSeconds(2);
            Get(ttl: 120);
            Assert.Equal(2, m_computes);
        }

        public static IEnumerable<object[]> ShortLivedSections() => new[]
        {
            new object[] { "DiscoverMovies" },
            new object[] { "DiscoverTV" },
            new object[] { "Discover" },
            new object[] { "UpcomingMovies" },
            new object[] { "UpcomingShows" },
            new object[] { "MyJellyseerrRequests" },
            new object[] { "LiveTV" },
        };

        [Theory]
        [MemberData(nameof(ShortLivedSections))]
        public void Sections_showing_outside_state_are_capped_at_30_seconds(string section)
        {
            Get(section, ttl: 3600);
            m_now += TimeSpan.FromSeconds(29);
            Get(section, ttl: 3600);
            Assert.Equal(1, m_computes);
            m_now += TimeSpan.FromSeconds(2);
            Get(section, ttl: 3600);
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void The_30_second_cap_never_lengthens_a_shorter_configured_time()
        {
            Get("DiscoverMovies", ttl: 10);
            m_now += TimeSpan.FromSeconds(11);
            Get("DiscoverMovies", ttl: 10);
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void The_always_short_lived_flag_caps_any_section()
        {
            Get("SomeOtherPluginsSection", ttl: 3600, shortLived: true);
            m_now += TimeSpan.FromSeconds(31);
            Get("SomeOtherPluginsSection", ttl: 3600, shortLived: true);
            Assert.Equal(2, m_computes);
        }

        [Fact]
        public void Ordinary_sections_keep_their_configured_time()
        {
            Get("RecentlyAddedMovies", ttl: 3600);
            m_now += TimeSpan.FromSeconds(600);
            Get("RecentlyAddedMovies", ttl: 3600);
            Assert.Equal(1, m_computes);
        }

        [Fact]
        public void InvalidateAll_drops_every_entry()
        {
            Get("RecentlyAddedMovies");
            Get("ContinueWatching");
            SectionResultCache.InvalidateAll();
            Get("RecentlyAddedMovies");
            Get("ContinueWatching");
            Assert.Equal(4, m_computes);
        }

        // ---- concurrency and failures -------------------------------------------------------------------------

        [Fact]
        public async Task Concurrent_identical_requests_compute_once()
        {
            using ManualResetEventSlim gate = new(false);
            int calls = 0;
            int entered = 0;
            Func<QueryResult<BaseItemDto>> slow = () =>
            {
                Interlocked.Increment(ref calls);
                gate.Wait(TimeSpan.FromSeconds(10));
                return new QueryResult<BaseItemDto>(new[] { new BaseItemDto { Name = "slow" } });
            };

            Task<QueryResult<BaseItemDto>>[] tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                Interlocked.Increment(ref entered);
                return m_cache.GetOrCompute("RecentlyAddedMovies", s_user, Query(), 3600, false, slow);
            })).ToArray();

            SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 12, TimeSpan.FromSeconds(10));
            await Task.Delay(150);
            gate.Set();
            QueryResult<BaseItemDto>[] results = await Task.WhenAll(tasks);

            Assert.Equal(1, calls);
            Assert.All(results, r => Assert.Same(results[0], r));
        }

        [Fact]
        public void A_failing_compute_is_not_cached()
        {
            Assert.Throws<InvalidOperationException>(() =>
                m_cache.GetOrCompute("RecentlyAddedMovies", s_user, Query(), 3600, false, () => throw new InvalidOperationException("boom")));

            QueryResult<BaseItemDto> ok = Get();
            Assert.Equal("compute-1", ok.Items[0].Name);
            Get();
            Assert.Equal(1, m_computes); // the success, once, is then cached
        }
    }
}
