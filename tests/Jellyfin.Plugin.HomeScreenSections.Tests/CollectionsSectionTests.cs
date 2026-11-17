using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    public sealed class CollectionsSectionTests
    {
        [Fact]
        public void With_no_box_sets_the_section_is_empty_and_never_runs_an_ItemIds_query()
        {
            // An empty ItemIds means "no filter" to Jellyfin: reaching that query with nothing to ask for loads the whole library.
            Mock<ILibraryManager> library = new();
            library.Setup(l => l.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());
            library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns(new QueryResult<BaseItem>());
            Mock<IDtoService> dto = new();
            CollectionsSection section = new(Mock.Of<IUserManager>(), dto.Object, library.Object);

            QueryResult<BaseItemDto> result = section.GetResults(new HomeScreenSectionPayload { UserId = Guid.NewGuid() }, new QueryCollection());

            Assert.Empty(result.Items);
            library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
            dto.VerifyNoOtherCalls();
        }
    }
}
