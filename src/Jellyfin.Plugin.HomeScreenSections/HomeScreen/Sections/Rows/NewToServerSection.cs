using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Services;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows
{
    /// <summary>
    /// Movies and shows in one row, newest import first. A show counts when its newest episode arrived; an
    /// episode never appears on its own. Gelato stubs (no file of their own) are left out: they are not "new media".
    /// </summary>
    public sealed class NewToServerSection : IHomeScreenSection
    {
        private const int c_limit = 16;
        private const int c_page = 200;
        private const int c_maxPages = 10;

        private readonly IUserManager m_users;
        private readonly ILibraryManager m_library;
        private readonly IDtoService m_dto;

        public string? Section => "NewToServer";
        public string? DisplayText { get; set; } = "New to the Server";
        public int? Limit => 1;
        public string? Route => null;
        public string? AdditionalData { get; set; }
        public object? OriginalPayload => null;

        public NewToServerSection(IUserManager users, ILibraryManager library, IDtoService dto)
        {
            m_users = users;
            m_library = library;
            m_dto = dto;
        }

        public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            User? user = m_users.GetUserById(payload.UserId);
            if (user == null)
            {
                return new QueryResult<BaseItemDto>();
            }

            DtoOptions paging = new DtoOptions(false) { EnableImages = false, Fields = new[] { ItemFields.Path } };
            List<BaseItem> picked = new();
            HashSet<Guid> seen = new();

            for (int page = 0; page < c_maxPages && picked.Count < c_limit; page++)
            {
                IReadOnlyList<BaseItem> batch = m_library.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
                    Recursive = true,
                    IsMissing = false,
                    IsVirtualItem = false,
                    StartIndex = page * c_page,
                    Limit = c_page,
                    EnableTotalRecordCount = false,
                    DtoOptions = paging
                });

                foreach (BaseItem item in batch)
                {
                    if (SeerrRowsService.IsStub(item))
                    {
                        continue;
                    }

                    // The paged query skips images, so a title is read again by id (16 at most) to get its posters.
                    BaseItem? target = item is Episode { SeriesId: var seriesId }
                        ? (seriesId == Guid.Empty ? null : m_library.GetItemById(seriesId))
                        : m_library.GetItemById(item.Id);
                    if (target != null && SeerrRowsService.IsStub(target))
                    {
                        continue;
                    }

                    if (target != null && seen.Add(target.Id))
                    {
                        picked.Add(target);
                        if (picked.Count >= c_limit)
                        {
                            break;
                        }
                    }
                }

                if (batch.Count < c_page)
                {
                    break;
                }
            }

            DtoOptions dtoOptions = new DtoOptions { Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSourceCount } };
            return new QueryResult<BaseItemDto>(m_dto.GetBaseItemDtos(picked, dtoOptions, user));
        }

        public IEnumerable<IHomeScreenSection> CreateInstances(Guid? userId, int instanceCount)
        {
            yield return this;
        }

        public HomeScreenSectionInfo GetInfo() => new()
        {
            Section = Section,
            DisplayText = DisplayText,
            AdditionalData = AdditionalData,
            Route = Route,
            Limit = 1,
            OriginalPayload = OriginalPayload,
            ViewMode = SectionViewMode.Portrait,
            AllowViewModeChange = true,
            AllowHideWatched = false,
            PluginConfigurationOptions = Array.Empty<PluginConfigurationOption>()
        };
    }
}
