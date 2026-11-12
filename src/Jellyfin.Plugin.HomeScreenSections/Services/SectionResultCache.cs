using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HomeScreenSections.Services
{
    /// <summary>
    /// Short-lived, per-user cache of section results, so that going back to the home page (or opening it on a second device)
    /// does not recompute every section. Entries are keyed by user, section and the request parameters, so nothing is ever
    /// shared between users. An entry stops being used when it is older than the configured time, when any library item that
    /// can appear in a section is added, removed or changed, or when that user's own watch data changes (so Continue Watching
    /// is never stale).
    /// </summary>
    public sealed class SectionResultCache : IDisposable
    {
        private const int c_maxEntries = 4000;
        private const int c_externalSectionSeconds = 30;

        private sealed class Entry
        {
            public Entry(Lazy<QueryResult<BaseItemDto>> value, DateTime created, long libraryGeneration, long userGeneration)
            {
                Value = value;
                Created = created;
                LibraryGeneration = libraryGeneration;
                UserGeneration = userGeneration;
            }

            public Lazy<QueryResult<BaseItemDto>> Value { get; }

            public DateTime Created { get; }

            public long LibraryGeneration { get; }

            public long UserGeneration { get; }
        }

        private readonly ConcurrentDictionary<string, Entry> m_entries = new();
        private readonly ConcurrentDictionary<Guid, long> m_userGenerations = new();
        private readonly ILibraryManager m_libraryManager;
        private readonly IUserDataManager m_userDataManager;
        private readonly ILogger m_logger;
        private long m_libraryGeneration;

        public SectionResultCache(ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<SectionResultCache> logger)
        {
            m_libraryManager = libraryManager;
            m_userDataManager = userDataManager;
            m_logger = logger;

            m_libraryManager.ItemAdded += OnLibraryItemChanged;
            m_libraryManager.ItemRemoved += OnLibraryItemChanged;
            m_libraryManager.ItemUpdated += OnLibraryItemChanged;
            m_userDataManager.UserDataSaved += OnUserDataSaved;
        }

        public QueryResult<BaseItemDto> GetOrCompute(string sectionKey, Guid userId, IQueryCollection query, int ttlSeconds, Func<QueryResult<BaseItemDto>> compute)
        {
            // Sections that show state held outside Jellyfin (Seerr, Sonarr/Radarr) get a short life whatever is configured.
            bool external = sectionKey.StartsWith("Discover", StringComparison.Ordinal)
                || sectionKey.StartsWith("Upcoming", StringComparison.Ordinal)
                || sectionKey == "MyJellyseerrRequests";
            TimeSpan lifetime = TimeSpan.FromSeconds(external ? Math.Min(ttlSeconds, c_externalSectionSeconds) : ttlSeconds);

            string key = BuildKey(sectionKey, userId, query);
            long libraryGeneration = Interlocked.Read(ref m_libraryGeneration);
            long userGeneration = m_userGenerations.TryGetValue(userId, out long g) ? g : 0;
            DateTime now = DateTime.UtcNow;

            if (m_entries.TryGetValue(key, out Entry? existing)
                && existing.LibraryGeneration == libraryGeneration
                && existing.UserGeneration == userGeneration
                && now - existing.Created < lifetime)
            {
                try
                {
                    return existing.Value.Value;
                }
                catch
                {
                    m_entries.TryRemove(new KeyValuePair<string, Entry>(key, existing));
                    throw;
                }
            }

            // Lazy with ExecutionAndPublication: two identical requests at the same moment compute once.
            Entry fresh = new Entry(new Lazy<QueryResult<BaseItemDto>>(compute, LazyThreadSafetyMode.ExecutionAndPublication), now, libraryGeneration, userGeneration);
            m_entries[key] = fresh;

            try
            {
                QueryResult<BaseItemDto> result = fresh.Value.Value;
                Trim();
                return result;
            }
            catch
            {
                m_entries.TryRemove(new KeyValuePair<string, Entry>(key, fresh)); // never keep a failure
                throw;
            }
        }

        public void Dispose()
        {
            m_libraryManager.ItemAdded -= OnLibraryItemChanged;
            m_libraryManager.ItemRemoved -= OnLibraryItemChanged;
            m_libraryManager.ItemUpdated -= OnLibraryItemChanged;
            m_userDataManager.UserDataSaved -= OnUserDataSaved;
        }

        private static string BuildKey(string sectionKey, Guid userId, IQueryCollection query)
        {
            // Every query parameter is part of the key, except that NextUpDateCutoff is "now minus N days" with a time of day,
            // which would make every request unique: only its date counts.
            IEnumerable<string> parts = query
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key.Equals("NextUpDateCutoff", StringComparison.OrdinalIgnoreCase)
                    ? $"{x.Key}={x.Value.ToString().Split('T')[0]}"
                    : $"{x.Key}={x.Value}");

            return $"{userId:N}|{sectionKey}|{string.Join('&', parts)}";
        }

        private void OnLibraryItemChanged(object? sender, ItemChangeEventArgs e)
        {
            // Only items that can appear in a home section. Live TV programmes, people, genres and so on change constantly
            // and would otherwise empty the cache all the time.
            if (e.Item is Movie or Series or Season or Episode or BoxSet or Folder)
            {
                Interlocked.Increment(ref m_libraryGeneration);
            }
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            m_userGenerations.AddOrUpdate(e.UserId, 1, (_, current) => current + 1);
        }

        private void Trim()
        {
            if (m_entries.Count <= c_maxEntries)
            {
                return;
            }

            long libraryGeneration = Interlocked.Read(ref m_libraryGeneration);
            foreach (KeyValuePair<string, Entry> pair in m_entries.OrderBy(x => x.Value.Created).Take(m_entries.Count - (c_maxEntries * 3 / 4)))
            {
                m_entries.TryRemove(pair.Key, out _);
            }

            m_logger.LogDebug("Section result cache trimmed to {Count} entries (library generation {Generation})", m_entries.Count, libraryGeneration);
        }
    }
}
