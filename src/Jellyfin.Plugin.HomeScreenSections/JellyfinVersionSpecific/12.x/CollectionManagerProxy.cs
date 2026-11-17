using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific
{
    public class CollectionManagerProxy
    {
        private readonly ICollectionManager m_collectionManager;

        public CollectionManagerProxy(ICollectionManager collectionManager)
        {
            m_collectionManager = collectionManager;
        }

        public IEnumerable<BoxSet> GetCollections(User user)
        {
            return ReflectionHelper.GetCollections(m_collectionManager, user);
        }
    }
}