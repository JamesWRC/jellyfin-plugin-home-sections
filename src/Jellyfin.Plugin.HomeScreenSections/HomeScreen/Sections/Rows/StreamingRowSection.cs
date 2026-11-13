using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows
{
    public sealed record StreamingService(string Slug, string Name, string ProviderIds, string DefaultRegion);

    /// <summary>"Popular on &lt;service&gt;": a Seerr/TMDB watch-provider list, library items first-class.</summary>
    public sealed class StreamingRowSection : SeerrRowBase
    {
        public static readonly StreamingService[] Catalogue =
        {
            new("Netflix", "Netflix", "8", "US"),
            new("Prime", "Prime Video", "9|119", "US"),
            new("Disney", "Disney+", "337", "US"),
            new("Stan", "Stan", "21", "AU"),
            new("HBOMax", "HBO Max", "1899", "US"),
            new("Paramount", "Paramount+", "531", "US"),
            new("AppleTV", "Apple TV+", "350", "US"),
        };

        private static readonly Dictionary<string, string> Regions = new()
        {
            { "US", "United States" }, { "AU", "Australia" }, { "GB", "United Kingdom" }, { "CA", "Canada" }, { "NZ", "New Zealand" }
        };

        private readonly StreamingService m_service;

        public override string? Section => "Streaming_" + m_service.Slug;

        public StreamingRowSection(StreamingService service, SeerrRowsService rows, IUserManager userManager, IDtoService dtoService, ImageCacheService imageCache)
            : base(rows, userManager, dtoService, imageCache)
        {
            m_service = service;
            DisplayText = "Popular on " + service.Name;
        }

        private string Region(HomeScreenSectionPayload? payload) =>
            HomeScreenSectionPayload.GetEffectiveStringConfig(Section!, "region", m_service.DefaultRegion);

        private string Kind(HomeScreenSectionPayload? payload) =>
            HomeScreenSectionPayload.GetEffectiveStringConfig(Section!, "kind", "both");

        /// <summary>The admin default (what a user who has not changed it sees), for background warming.</summary>
        public string AdminRegion() => AdminValue("region", m_service.DefaultRegion);

        public string AdminKind() => AdminValue("kind", "both");

        private string AdminValue(string key, string fallback)
        {
            var entry = HomeScreenSectionsPlugin.Instance?.Configuration?.SectionSettings
                .FirstOrDefault(x => x.SectionId == Section)?.PluginConfigurations.FirstOrDefault(x => x.Key == key);
            return string.IsNullOrEmpty(entry?.Value) ? fallback : entry!.Value!;
        }

        protected override bool ShowNonLocal(HomeScreenSectionPayload payload) =>
            HomeScreenSectionPayload.GetEffectiveStringConfig(Section!, "nonLocal", "yes") != "no";

        /// <summary>The cached list for this service, region and kind (the same entry serves every user).</summary>
        public (string Key, Func<List<SeerrEntry>> Fetch) ListFor(string region, string kind)
        {
            string key = $"stream-{m_service.Slug}-{region}-{kind}";
            string q = $"watchProviders={Uri.EscapeDataString(m_service.ProviderIds)}&watchRegion={region}&sortBy=popularity.desc";
            return (key, () =>
            {
                List<SeerrEntry> movies = kind == "tv" ? new() : Rows.FetchDiscover(false, "/api/v1/discover/movies?" + q, 2);
                List<SeerrEntry> shows = kind == "movies" ? new() : Rows.FetchDiscover(true, "/api/v1/discover/tv?" + q, 2);
                List<SeerrEntry> mixed = new();
                for (int i = 0; i < Math.Max(movies.Count, shows.Count); i++)
                {
                    if (i < movies.Count) { mixed.Add(movies[i]); }
                    if (i < shows.Count) { mixed.Add(shows[i]); }
                }

                return mixed.Take(40).ToList();
            });
        }

        protected override IReadOnlyList<(SeerrEntry Entry, string? Subtitle)> GetEntries(User user, HomeScreenSectionPayload payload)
        {
            var (key, fetch) = ListFor(Region(payload), Kind(payload));
            return Rows.GetList(key, TimeSpan.FromHours(24), fetch).Select(e => (e, (string?)null)).ToList();
        }

        protected override IEnumerable<PluginConfigurationOption> ExtraOptions()
        {
            yield return PluginConfigurationHelper.CreateDropdown("region", "Region", "Which country's catalogue to rank.", Regions, "StreamingRegion", m_service.DefaultRegion, true);
            yield return PluginConfigurationHelper.CreateDropdown("kind", "Content", "Movies, shows or both.", new Dictionary<string, string> { { "both", "Movies and shows" }, { "movies", "Movies only" }, { "tv", "Shows only" } }, "StreamingKind", "both", true);
            yield return PluginConfigurationHelper.CreateDropdown("nonLocal", "Titles not on this server", "Show titles the server does not have (open via Gelato where allowed, otherwise request).", new Dictionary<string, string> { { "yes", "Show" }, { "no", "Hide" } }, "StreamingNonLocal", "yes", true);
        }
    }
}
