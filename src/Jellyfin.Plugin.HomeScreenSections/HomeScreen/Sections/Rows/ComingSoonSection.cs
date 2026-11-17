using Jellyfin.Plugin.HomeScreenSections.Configuration;
using System.Globalization;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows
{
    /// <summary>Upcoming movies and shows from Seerr, merged and ordered by release date.</summary>
    public sealed class ComingSoonSection : SeerrRowBase
    {
        public override string? Section => "ComingSoon";
        public override string? DisplayText { get; set; } = "Coming Soon";

        public ComingSoonSection(SeerrRowsService rows, IUserManager userManager, IDtoService dtoService, ImageCacheService imageCache)
            : base(rows, userManager, dtoService, imageCache)
        {
        }

        public (string Key, Func<List<SeerrEntry>> Fetch) List() => ("coming-soon", () =>
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Rows.FetchDiscover(false, "/api/v1/discover/movies/upcoming", 3)
                .Concat(Rows.FetchDiscover(true, "/api/v1/discover/tv/upcoming", 3))
                .Where(e => e.MediaStatus < 4 && string.CompareOrdinal(e.Date ?? string.Empty, today) >= 0)
                .OrderBy(e => e.Date, StringComparer.Ordinal)
                .Take(40)
                .ToList();
        });

        protected override IReadOnlyList<(SeerrEntry Entry, string? Subtitle)> GetEntries(User user, HomeScreenSectionPayload payload)
        {
            var (key, fetch) = List();
            return Rows.GetList(key, TimeSpan.FromHours(6), fetch)
                .Select(e => (e, DateTime.TryParse(e.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? "Releases " + d.ToString("d MMM", CultureInfo.InvariantCulture) : null))
                .ToList();
        }
    }
}
