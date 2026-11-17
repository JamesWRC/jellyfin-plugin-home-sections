using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows
{
    /// <summary>
    /// A row of titles that mixes library items (opened locally) with titles we do not have (a request/Gelato card).
    /// </summary>
    public abstract class SeerrRowBase : IHomeScreenSection
    {
        private readonly IUserManager m_userManager;
        private readonly IDtoService m_dtoService;
        private readonly ImageCacheService m_imageCache;

        protected SeerrRowsService Rows { get; }

        public abstract string? Section { get; }
        public virtual string? DisplayText { get; set; } = string.Empty;
        public int? Limit => 1;
        public string? Route => null;
        public string? AdditionalData { get; set; }
        public object? OriginalPayload => null;

        protected SeerrRowBase(SeerrRowsService rows, IUserManager userManager, IDtoService dtoService, ImageCacheService imageCache)
        {
            Rows = rows;
            m_userManager = userManager;
            m_dtoService = dtoService;
            m_imageCache = imageCache;
        }

        /// <summary>The titles for this user, in display order; <c>Subtitle</c> is shown under a card that is not local.</summary>
        protected abstract IReadOnlyList<(SeerrEntry Entry, string? Subtitle)> GetEntries(User user, HomeScreenSectionPayload payload);

        protected virtual bool ShowNonLocal(HomeScreenSectionPayload payload) => true;

        protected virtual int MaxItems => 20;

        public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            User? user = m_userManager.GetUserById(payload.UserId);
            if (user == null)
            {
                return new QueryResult<BaseItemDto>();
            }

            IReadOnlyList<(SeerrEntry Entry, string? Subtitle)> entries = GetEntries(user, payload);
            if (entries.Count == 0)
            {
                return new QueryResult<BaseItemDto>();
            }

            var local = Rows.ResolveLocal(user, entries.Select(e => (e.Entry.IsTv, e.Entry.TmdbId)));
            bool nonLocal = ShowNonLocal(payload);
            DtoOptions dtoOptions = new DtoOptions { Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSourceCount } };

            string? root = HomeScreenSectionsPlugin.Instance?.Configuration?.JellyseerrExternalUrl;
            if (string.IsNullOrEmpty(root))
            {
                root = HomeScreenSectionsPlugin.Instance?.Configuration?.JellyseerrUrl ?? string.Empty;
            }

            List<BaseItemDto> items = new();
            HashSet<Guid> seen = new();
            foreach (var (entry, subtitle) in entries)
            {
                if (items.Count >= MaxItems)
                {
                    break;
                }

                if (local.TryGetValue((entry.IsTv, entry.TmdbId), out BaseItem? item))
                {
                    if (seen.Add(item.Id))
                    {
                        items.Add(m_dtoService.GetBaseItemDto(item, dtoOptions, user));
                    }

                    continue;
                }

                if (!nonLocal)
                {
                    continue;
                }

                Dictionary<string, string> ids = new()
                {
                    { "JellyseerrRoot", root },
                    { "Jellyseerr", entry.TmdbId.ToString() },
                    { "JellyseerrPoster", ImageCacheHelper.GetCachedImageUrl(m_imageCache, $"https://image.tmdb.org/t/p/w600_and_h900_bestv2{entry.PosterPath}") },
                    { "HssRemote", "1" }
                };
                if (!string.IsNullOrEmpty(subtitle))
                {
                    ids["HssSubtitle"] = subtitle;
                }

                items.Add(new BaseItemDto
                {
                    Name = entry.Title,
                    SourceType = entry.IsTv ? "tv" : "movie",
                    CommunityRating = entry.Rating > 0 ? entry.Rating : null,
                    PremiereDate = DateTime.TryParse(entry.Date, out DateTime d) ? d : null,
                    ProviderIds = ids
                });
            }

            return new QueryResult<BaseItemDto> { Items = items, StartIndex = 0, TotalRecordCount = items.Count };
        }

        public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
        {
            yield return this;
        }

        protected virtual IEnumerable<PluginConfigurationOption> ExtraOptions() => Enumerable.Empty<PluginConfigurationOption>();

        public HomeScreenSectionInfo GetInfo()
        {
            return new HomeScreenSectionInfo
            {
                Section = Section,
                DisplayText = DisplayText,
                AdditionalData = AdditionalData,
                Route = Route,
                Limit = 1,
                OriginalPayload = OriginalPayload,
                ViewMode = SectionViewMode.Portrait,
                AllowViewModeChange = false,
                PluginConfigurationOptions = ExtraOptions().ToArray()
            };
        }
    }
}
