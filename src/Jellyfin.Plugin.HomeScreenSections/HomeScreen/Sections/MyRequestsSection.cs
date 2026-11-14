using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections
{
    /// <summary>
    /// The user's own Seerr requests, newest first: every request, not only the ones already in the library.
    /// A title the server has opens locally; one still on its way shows its request status.
    /// </summary>
    public class MyRequestsSection : SeerrRowBase
    {
        private readonly IUserManager m_users;

        public override string? Section => "MyJellyseerrRequests";
        public override string? DisplayText { get; set; } = "My Requests";

        public MyRequestsSection(SeerrRowsService rows, IUserManager userManager, IDtoService dtoService, ImageCacheService imageCache)
            : base(rows, userManager, dtoService, imageCache)
        {
            m_users = userManager;
        }

        internal static string Status(int requestStatus, int mediaStatus) => requestStatus switch
        {
            1 => "Awaiting approval",
            3 => "Declined",
            4 => "Failed",
            _ => mediaStatus switch { 5 => "Available", 4 => "Partly available", 3 => "Downloading", _ => "Approved" }
        };

        protected override IReadOnlyList<(SeerrEntry Entry, string? Subtitle)> GetEntries(User user, HomeScreenSectionPayload payload)
        {
            int? seerrId = Rows.GetSeerrUserId(user.Username);
            if (seerrId == null)
            {
                return Array.Empty<(SeerrEntry, string?)>();
            }

            JArray? requests = Rows.GetUserRequests(seerrId.Value);
            if (requests == null)
            {
                return Array.Empty<(SeerrEntry, string?)>();
            }

            var wanted = new List<(bool IsTv, int Tmdb, string Status)>();
            HashSet<(bool, int)> seen = new();
            foreach (JObject r in requests.OfType<JObject>())
            {
                JObject? media = r.Value<JObject>("media");
                int? tmdb = media?.Value<int?>("tmdbId");
                string? type = media?.Value<string>("mediaType") ?? r.Value<string>("type");
                if (tmdb == null || type == null)
                {
                    continue;
                }

                bool isTv = type == "tv";
                if (seen.Add((isTv, tmdb.Value)))
                {
                    wanted.Add((isTv, tmdb.Value, Status(r.Value<int?>("status") ?? 1, media!.Value<int?>("status") ?? 1)));
                }

                if (wanted.Count >= 24)
                {
                    break;
                }
            }

            SeerrEntry?[] details = new SeerrEntry?[wanted.Count];
            Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i => details[i] = Rows.GetDetails(wanted[i].IsTv, wanted[i].Tmdb));

            List<(SeerrEntry, string?)> result = new();
            for (int i = 0; i < wanted.Count; i++)
            {
                if (details[i] != null)
                {
                    result.Add((details[i]!, wanted[i].Status));
                }
            }

            return result;
        }
    }
}
