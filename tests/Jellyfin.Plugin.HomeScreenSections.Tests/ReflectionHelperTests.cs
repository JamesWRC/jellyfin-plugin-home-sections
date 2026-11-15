using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.JellyfinVersionSpecific;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    /// <summary>
    /// The by-name reflection into Jellyfin internals. Beta (Gelato installed) found two silent failures: ICollectionManager
    /// resolves to Gelato's decorator, which has no GetCollections, so every collection-based row got no collections; and
    /// PublishedServerUrl is private on ApplicationHost while the runtime type is the derived CoreAppHost.
    /// </summary>
    public sealed class ReflectionHelperTests
    {
        private static User NewUser() => new("tester", "auth", "reset");

        private abstract class CollectionManagerBase : ICollectionManager
        {
            public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated { add { } remove { } }
            public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection { add { } remove { } }
            public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection { add { } remove { } }
            public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options) => throw new NotSupportedException();
            public Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) => throw new NotSupportedException();
            public Task RemoveFromCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) => throw new NotSupportedException();
            public IEnumerable<BaseItem> CollapseItemsWithinBoxSets(IEnumerable<BaseItem> items, User user) => throw new NotSupportedException();
            public virtual Task<Folder?> GetCollectionsFolder(bool createIfNeeded) => Task.FromResult<Folder?>(null);
            public IEnumerable<BoxSet> GetCollectionsContainingItem(User user, Guid itemId) => throw new NotSupportedException();
        }

        /// <summary>Stands for Jellyfin's CollectionManager: GetCollections is private.</summary>
        private sealed class RealManager : CollectionManagerBase
        {
            public readonly BoxSet[] Sets;
            public RealManager(params BoxSet[] sets) => Sets = sets;
            private IEnumerable<BoxSet> GetCollections(User user) => Sets;
        }

        /// <summary>Stands for Gelato's CollectionManagerDecorator: a primary-constructor wrapper, no GetCollections of its own.</summary>
        private sealed class Decorator(ICollectionManager inner) : CollectionManagerBase
        {
            public ICollectionManager Inner => inner;
        }

        /// <summary>A real manager subclass: the private method is declared on a base class.</summary>
        private sealed class DerivedManager : RealManagerBase
        {
            public DerivedManager(params BoxSet[] sets) : base(sets) { }
        }

        private abstract class RealManagerBase : CollectionManagerBase
        {
            private readonly BoxSet[] m_sets;
            protected RealManagerBase(BoxSet[] sets) => m_sets = sets;
            private IEnumerable<BoxSet> GetCollections(User user) => m_sets;
        }

        private static BoxSet Set(string name) => new() { Name = name, Id = Guid.NewGuid() };

        [Fact]
        public void GetCollections_FromThePlainManager()
        {
            BoxSet a = Set("A"), b = Set("B");
            Assert.Equal(new[] { a, b }, new CollectionManagerProxy(new RealManager(a, b)).GetCollections(NewUser()));
        }

        [Fact]
        public void GetCollections_ThroughADecorator()
        {
            BoxSet a = Set("Top Ten");
            ICollectionManager decorated = new Decorator(new RealManager(a));
            Assert.Equal(new[] { a }, new CollectionManagerProxy(decorated).GetCollections(NewUser()));
            Assert.Equal(new[] { a }, decorated.GetCollections(NewUser()));
        }

        [Fact]
        public void GetCollections_ThroughTwoDecorators()
        {
            BoxSet a = Set("A");
            ICollectionManager decorated = new Decorator(new Decorator(new RealManager(a)));
            Assert.Equal(new[] { a }, decorated.GetCollections(NewUser()));
        }

        [Fact]
        public void GetCollections_PrivateMethodOnABaseClass()
        {
            BoxSet a = Set("A");
            Assert.Equal(new[] { a }, new DerivedManager(a).GetCollections(NewUser()));
        }

        [Fact]
        public void GetCollections_NoMethodAndNoFolder_IsEmptyNotAnError()
        {
            Assert.Empty(new Decorator(new Decorator(new PlainManager())).GetCollections(NewUser()));
        }

        private sealed class PlainManager : CollectionManagerBase { }

        private class FakeBaseHost
        {
            private string? PublishedServerUrl { get; } = "https://jf.example";
        }

        private sealed class FakeDerivedHost : FakeBaseHost
        {
            public int HttpPort => 8096;
        }

        private sealed class FakeHostWithOwnProperty
        {
            private string PublishedServerUrl => "https://own.example";
        }

        private sealed class FakeHostWithout { }

        [Fact]
        public void PublishedServerUrl_PrivateOnTheBaseClass()
        {
            Assert.Equal("https://jf.example", ReflectionHelper.GetPublishedServerUrl(new FakeDerivedHost()));
        }

        [Fact]
        public void PublishedServerUrl_PrivateOnTheTypeItself()
        {
            Assert.Equal("https://own.example", ReflectionHelper.GetPublishedServerUrl(new FakeHostWithOwnProperty()));
        }

        [Fact]
        public void PublishedServerUrl_MissingIsNull()
        {
            Assert.Null(ReflectionHelper.GetPublishedServerUrl(new FakeHostWithout()));
            Assert.Null(ReflectionHelper.GetPublishedServerUrl(null));
        }
    }
}
