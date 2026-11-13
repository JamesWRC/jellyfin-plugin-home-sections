using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.Services
{
    /// <summary>One title from a Seerr/TMDB list, kept small so a list is a few KB.</summary>
    public sealed record SeerrEntry(bool IsTv, int TmdbId, string Title, string? Date, float Rating, string? PosterPath, int MediaStatus);

    /// <summary>
    /// Shared engine for the Seerr-backed rows (streaming services, coming soon, my requests).
    /// Lists are global (not per user), cached in memory and on disk, and refreshed in the background once
    /// stale, so a page load never waits on Seerr or TMDB. A separate TMDB-id map of the library says which
    /// titles are local.
    /// </summary>
    public sealed class SeerrRowsService
    {
        private sealed class Slot
        {
            public List<SeerrEntry> Items = new();
            public DateTime Fetched = DateTime.MinValue;
            public int Refreshing;
        }

        private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

        private readonly IHttpClientFactory m_http;
        private readonly ILibraryManager m_library;
        private readonly ILogger<SeerrRowsService> m_logger;
        private readonly string m_dir;
        private readonly ConcurrentDictionary<string, Slot> m_slots = new();
        private readonly ConcurrentDictionary<string, (int? Id, DateTime At)> m_seerrUsers = new();
        private readonly ConcurrentDictionary<string, SeerrEntry?> m_details = new();

        private Dictionary<(bool, int), Guid[]> m_map = new();
        private DateTime m_mapBuilt = DateTime.MinValue;
        private volatile bool m_dirty = true;
        private int m_building;
        private readonly object m_mapLock = new();

        public SeerrRowsService(IHttpClientFactory http, ILibraryManager library, IApplicationPaths paths, ILogger<SeerrRowsService> logger)
        {
            m_http = http;
            m_library = library;
            m_logger = logger;
            m_dir = Path.Combine(paths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!, "seerr-rows");
            Directory.CreateDirectory(m_dir);

            m_library.ItemAdded += (_, _) => m_dirty = true;
            m_library.ItemRemoved += (_, _) => m_dirty = true;
        }

        public static string? SeerrUrl => HomeScreenSectionsPlugin.Instance?.Configuration?.JellyseerrUrl;

        // ---- Seerr access -------------------------------------------------------------------------------------

        private JObject? Get(string path, int? asUser = null, int timeoutSeconds = 10)
        {
            var cfg = HomeScreenSectionsPlugin.Instance?.Configuration;
            if (cfg == null || string.IsNullOrEmpty(cfg.JellyseerrUrl))
            {
                return null;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, cfg.JellyseerrUrl.TrimEnd('/') + path);
                req.Headers.Add("X-Api-Key", cfg.JellyseerrApiKey);
                if (asUser != null)
                {
                    req.Headers.Add("X-Api-User", asUser.Value.ToString(CultureInfo.InvariantCulture));
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using HttpResponseMessage res = m_http.CreateClient().SendAsync(req, cts.Token).GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode)
                {
                    m_logger.LogWarning("Seerr {Path} -> {Status}", path, (int)res.StatusCode);
                    return null;
                }

                return JObject.Parse(res.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Seerr {Path} failed: {Message}", path, ex.Message);
                return null;
            }
        }

        public int? GetSeerrUserId(string username)
        {
            if (m_seerrUsers.TryGetValue(username, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(1))
            {
                return hit.Id;
            }

            JObject? users = Get($"/api/v1/user?q={Uri.EscapeDataString(username)}");
            int? id = users?.Value<JArray>("results")?.OfType<JObject>()
                .FirstOrDefault(x => x.Value<string>("jellyfinUsername") == username)?.Value<int>("id");
            if (users != null)
            {
                m_seerrUsers[username] = (id, DateTime.UtcNow);
            }

            return id;
        }

        private static SeerrEntry? ParseResult(JObject r, bool isTv, string[] languages)
        {
            if (r.Value<bool?>("adult") == true)
            {
                return null;
            }

            if (languages.Length > 0 && !languages.Contains(r.Value<string>("originalLanguage") ?? string.Empty))
            {
                return null;
            }

            int? id = r.Value<int?>("id");
            string? title = r.Value<string>(isTv ? "name" : "title") ?? r.Value<string>("title") ?? r.Value<string>("name");
            string? poster = r.Value<string>("posterPath");
            if (id == null || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(poster))
            {
                return null;
            }

            return new SeerrEntry(
                isTv,
                id.Value,
                title,
                r.Value<string>(isTv ? "firstAirDate" : "releaseDate"),
                r.Value<float?>("voteAverage") ?? 0f,
                poster,
                r["mediaInfo"]?.Value<int?>("status") ?? 0);
        }

        private string[] Languages()
        {
            string raw = HomeScreenSectionsPlugin.Instance?.Configuration?.JellyseerrPreferredLanguages ?? string.Empty;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        /// <summary>Up to <paramref name="pages"/> pages of a Seerr discover endpoint.</summary>
        public List<SeerrEntry> FetchDiscover(bool isTv, string pathAndQuery, int pages)
        {
            List<SeerrEntry> list = new();
            string[] langs = Languages();
            string sep = pathAndQuery.Contains('?') ? "&" : "?";
            for (int page = 1; page <= pages; page++)
            {
                JObject? json = Get($"{pathAndQuery}{sep}page={page}");
                if (json == null)
                {
                    break;
                }

                foreach (JObject r in json.Value<JArray>("results")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    SeerrEntry? e = ParseResult(r, isTv, langs);
                    if (e != null)
                    {
                        list.Add(e);
                    }
                }
            }

            return list;
        }

        /// <summary>Title details for a request (Seerr's request objects carry only the TMDB id).</summary>
        public SeerrEntry? GetDetails(bool isTv, int tmdbId)
        {
            string key = (isTv ? "tv" : "movie") + tmdbId;
            if (m_details.TryGetValue(key, out SeerrEntry? cached))
            {
                return cached;
            }

            JObject? json = Get($"/api/v1/{(isTv ? "tv" : "movie")}/{tmdbId}");
            if (json == null)
            {
                return null; // do not cache failures
            }

            SeerrEntry? e = ParseResult(json, isTv, Array.Empty<string>());
            if (e != null)
            {
                m_details[key] = e;
            }

            return e;
        }

        public JArray? GetUserRequests(int seerrUserId)
        {
            return Get($"/api/v1/user/{seerrUserId}/requests?take=40&sort=added", null)?.Value<JArray>("results");
        }

        // ---- Global list cache --------------------------------------------------------------------------------

        /// <summary>
        /// A list, from memory if fresh, else from disk or the stale copy while a background refresh runs.
        /// Only the very first call on a fresh install blocks (up to the Seerr timeout).
        /// </summary>
        public IReadOnlyList<SeerrEntry> GetList(string key, TimeSpan ttl, Func<List<SeerrEntry>> fetch)
        {
            Slot slot = m_slots.GetOrAdd(key, _ => LoadFromDisk(key));
            bool stale = DateTime.UtcNow - slot.Fetched > ttl;
            if (!stale)
            {
                return slot.Items;
            }

            if (slot.Items.Count > 0)
            {
                StartRefresh(key, slot, fetch);
                return slot.Items;
            }

            Refresh(key, slot, fetch); // first ever load
            return slot.Items;
        }

        public void Warm(string key, TimeSpan ttl, Func<List<SeerrEntry>> fetch)
        {
            Slot slot = m_slots.GetOrAdd(key, _ => LoadFromDisk(key));
            if (DateTime.UtcNow - slot.Fetched > ttl)
            {
                StartRefresh(key, slot, fetch);
            }
        }

        private void StartRefresh(string key, Slot slot, Func<List<SeerrEntry>> fetch)
        {
            if (Interlocked.CompareExchange(ref slot.Refreshing, 1, 0) != 0)
            {
                return;
            }

            Task.Run(() => Refresh(key, slot, fetch));
        }

        private void Refresh(string key, Slot slot, Func<List<SeerrEntry>> fetch)
        {
            try
            {
                Interlocked.Exchange(ref slot.Refreshing, 1);
                List<SeerrEntry> items = fetch();
                if (items.Count > 0)
                {
                    slot.Items = items;
                    slot.Fetched = DateTime.UtcNow;
                    File.WriteAllText(DiskPath(key), JsonSerializer.Serialize(new { fetched = slot.Fetched, items }, s_json));
                }
                else
                {
                    // Keep serving the old list; retry in a few minutes rather than hammering Seerr.
                    slot.Fetched = DateTime.UtcNow - TimeSpan.FromHours(23);
                }
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Refresh of {Key} failed: {Message}", key, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref slot.Refreshing, 0);
            }
        }

        private string DiskPath(string key) => Path.Combine(m_dir, string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')) + ".json");

        private Slot LoadFromDisk(string key)
        {
            Slot slot = new();
            try
            {
                string path = DiskPath(key);
                if (File.Exists(path))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    slot.Fetched = doc.RootElement.GetProperty("fetched").GetDateTime();
                    slot.Items = doc.RootElement.GetProperty("items").Deserialize<List<SeerrEntry>>(s_json) ?? new();
                }
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Could not read cached list {Key}: {Message}", key, ex.Message);
            }

            return slot;
        }

        // ---- Library TMDB map ---------------------------------------------------------------------------------

        private void RebuildMap()
        {
            if (Interlocked.CompareExchange(ref m_building, 1, 0) != 0)
            {
                return;
            }

            try
            {
                DateTime started = DateTime.UtcNow;
                IReadOnlyList<BaseItem> items = m_library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Recursive = true,
                    DtoOptions = new DtoOptions(false) { EnableImages = false }
                });

                Dictionary<(bool, int), List<(Guid Id, bool Stub)>> map = new();
                foreach (BaseItem item in items)
                {
                    if (!int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), out int tmdb))
                    {
                        continue;
                    }

                    var key = (item is MediaBrowser.Controller.Entities.TV.Series, tmdb);
                    if (!map.TryGetValue(key, out var list))
                    {
                        map[key] = list = new();
                    }

                    list.Add((item.Id, IsStub(item)));
                }

                m_map = map.ToDictionary(x => x.Key, x => x.Value.OrderBy(v => v.Stub).Select(v => v.Id).Take(4).ToArray());
                if (m_map.Count == 0)
                {
                    // Built before the library had loaded (right after start): try again in about a minute.
                    m_mapBuilt = DateTime.UtcNow - TimeSpan.FromMinutes(4);
                    m_dirty = true;
                }
                else
                {
                    m_mapBuilt = DateTime.UtcNow;
                    m_dirty = false;
                }
                m_logger.LogInformation("Library TMDB map: {Count} titles in {Ms} ms", m_map.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("TMDB map rebuild failed: {Message}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref m_building, 0);
            }
        }

        /// <summary>A Gelato stub (no file of its own): real media is preferred over one when both exist.</summary>
        private static bool IsStub(BaseItem item) =>
            item.Path != null && item.Path.Contains("/gelato", StringComparison.OrdinalIgnoreCase);

        private void EnsureMap()
        {
            bool first = m_mapBuilt == DateTime.MinValue;
            if (first)
            {
                lock (m_mapLock)
                {
                    if (m_mapBuilt == DateTime.MinValue)
                    {
                        RebuildMap();
                    }
                }
            }
            else if (m_dirty && DateTime.UtcNow - m_mapBuilt > TimeSpan.FromMinutes(5))
            {
                Task.Run(RebuildMap);
            }
        }

        /// <summary>The library items this user can see for the given TMDB ids (real media first, Gelato stub second).</summary>
        public Dictionary<(bool, int), BaseItem> ResolveLocal(User user, IEnumerable<(bool IsTv, int Tmdb)> keys)
        {
            EnsureMap();
            Dictionary<(bool, int), Guid[]> map = m_map;
            Dictionary<(bool, int), Guid[]> wanted = new();
            foreach (var k in keys)
            {
                if (map.TryGetValue((k.IsTv, k.Tmdb), out Guid[]? ids))
                {
                    wanted[(k.IsTv, k.Tmdb)] = ids;
                }
            }

            Dictionary<(bool, int), BaseItem> result = new();
            Guid[] all = wanted.Values.SelectMany(x => x).Distinct().ToArray();
            if (all.Length == 0)
            {
                return result; // an empty ItemIds means "no filter" to Jellyfin
            }

            Dictionary<Guid, BaseItem> visible = m_library.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = all,
                Recursive = true,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false) { EnableImages = false }
            }).ToDictionary(x => x.Id);

            foreach (var kv in wanted)
            {
                foreach (Guid id in kv.Value)
                {
                    if (visible.TryGetValue(id, out BaseItem? item))
                    {
                        result[kv.Key] = item;
                        break;
                    }
                }
            }

            return result;
        }

        public void WarmMap() => EnsureMap();

        public bool MapReady => m_map.Count > 0;
    }
}
