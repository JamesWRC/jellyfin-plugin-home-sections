using System.Diagnostics;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Rows;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Latest;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Persons;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.RecentlyAdded;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Upcoming;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen
{
    /// <summary>
    /// Manager for the Modular Home Screen.
    /// </summary>
    public class HomeScreenManager : IHomeScreenManager
    {
        private Dictionary<string, IHomeScreenSection> m_delegates = new Dictionary<string, IHomeScreenSection>();
        private Dictionary<Guid, bool> m_userFeatureEnabledStates = new Dictionary<Guid, bool>();

        private readonly IServiceProvider m_serviceProvider;
        private readonly IApplicationPaths m_applicationPaths;
        private readonly ILogger m_logger;
        private readonly SectionResultCache m_resultCache;

        private const string c_settingsFile = "ModularHomeSettings.json";

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="serviceProvider">Instance of the <see cref="IServiceProvider"/> interface.</param>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        public HomeScreenManager(IServiceProvider serviceProvider, IApplicationPaths applicationPaths, ILogger<HomeScreenManager> logger, ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<SectionResultCache> cacheLogger)
        {
            m_logger = logger;
            m_resultCache = new SectionResultCache(libraryManager, userDataManager, cacheLogger);
            m_serviceProvider = serviceProvider;
            m_applicationPaths = applicationPaths;

            string userFeatureEnabledPath = Path.Combine(m_applicationPaths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!, "userFeatureEnabled.json");
            if (File.Exists(userFeatureEnabledPath))
            {
                m_userFeatureEnabledStates = JsonConvert.DeserializeObject<Dictionary<Guid, bool>>(File.ReadAllText(userFeatureEnabledPath)) ?? new Dictionary<Guid, bool>();
            }
        }
        
        public void RegisterBuiltInResultsDelegates()
        {
            RegisterResultsDelegate<MyMediaSection>();
            
            RegisterResultsDelegate<ContinueWatchingSection>();
            RegisterResultsDelegate<NextUpSection>();
            RegisterResultsDelegate<ContinueWatchingNextUpSection>();
            
            RegisterResultsDelegate<RecentlyAddedMoviesSection>();
            RegisterResultsDelegate<RecentlyAddedShowsSection>();
            RegisterResultsDelegate<RecentlyAddedAlbumsSection>();
            RegisterResultsDelegate<RecentlyAddedArtistsSection>();
            RegisterResultsDelegate<RecentlyAddedBooksSection>();
            RegisterResultsDelegate<RecentlyAddedAudioBooksSection>();
            RegisterResultsDelegate<RecentlyAddedMusicVideosSection>();
            RegisterResultsDelegate<RecentlyAddedInLibrarySection>();
            
            RegisterResultsDelegate<LatestMoviesSection>();
            RegisterResultsDelegate<LatestShowsSection>();
            RegisterResultsDelegate<LatestAlbumsSection>();
            RegisterResultsDelegate<LatestBooksSection>();
            RegisterResultsDelegate<LatestAudioBooksSection>();
            RegisterResultsDelegate<LatestMusicVideoSection>();
            
            RegisterResultsDelegate<BecauseYouWatchedSection>();
            RegisterResultsDelegate<LiveTvSection>();
            RegisterResultsDelegate<MyListSection>();
            RegisterResultsDelegate<WatchAgainSection>();
            RegisterResultsDelegate<CollectionsSection>();
            
            RegisterResultsDelegate<DiscoverSection>();
            RegisterResultsDelegate<DiscoverMoviesSection>();
            RegisterResultsDelegate<DiscoverTvSection>();
            
            RegisterResultsDelegate<UpcomingShowsSection>();
            RegisterResultsDelegate<UpcomingMoviesSection>();
            RegisterResultsDelegate<UpcomingMusicSection>();
            RegisterResultsDelegate<UpcomingBooksSection>();
            
            RegisterResultsDelegate<GenreSection>();
            RegisterResultsDelegate<MyRequestsSection>();
            RegisterResultsDelegate<NewToServerSection>();
            RegisterResultsDelegate<ComingSoonSection>();
            foreach (StreamingService service in StreamingRowSection.Catalogue)
            {
                RegisterResultsDelegate(ActivatorUtilities.CreateInstance<StreamingRowSection>(m_serviceProvider, service));
            }

            RegisterResultsDelegate<DirectedBySection>();
            RegisterResultsDelegate<StarringSection>();

            RegisterResultsDelegate<HiddenGemsSection>();
            RegisterResultsDelegate<StudioSection>();
            RegisterResultsDelegate<DecadeSection>();
            RegisterResultsDelegate<FinishTheseCollectionsSection>();

            //RegisterResultsDelegate<TopTenSection>();

            StartRowsWarmLoop();
        }

        /// <summary>
        /// Keeps the global Seerr lists (streaming services, coming soon) and the library TMDB map fresh in the background,
        /// so a page load never waits on Seerr or TMDB.
        /// </summary>
        private void StartRowsWarmLoop()
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(45));
                SeerrRowsService rows = m_serviceProvider.GetRequiredService<SeerrRowsService>();
                while (true)
                {
                    try
                    {
                        rows.WarmMap();
                        if (!string.IsNullOrEmpty(HomeScreenSectionsPlugin.Instance?.Configuration?.JellyseerrUrl))
                        {
                            var coming = (ComingSoonSection)m_delegates["ComingSoon"];
                            var (cKey, cFetch) = coming.List();
                            rows.Warm(cKey, TimeSpan.FromHours(6), cFetch);

                            foreach (StreamingRowSection streaming in m_delegates.Values.OfType<StreamingRowSection>())
                            {
                                var (key, fetch) = streaming.ListFor(streaming.AdminRegion(), streaming.AdminKind());
                                rows.Warm(key, TimeSpan.FromHours(24), fetch);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        m_logger.LogWarning("Rows warm-up failed: {Message}", ex.Message);
                    }

                    await Task.Delay(rows.MapReady ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(1));
                }
            });
        }

        /// <inheritdoc/>
        public IEnumerable<IHomeScreenSection> GetSectionTypes()
        {
            return m_delegates.Values;
        }

        public IHomeScreenSection? GetSection(string sectionName)
        {
            return m_delegates.GetValueOrDefault(sectionName);
        }

        /// <inheritdoc/>
        public QueryResult<BaseItemDto> InvokeResultsDelegate(string key, HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            if (m_delegates.TryGetValue(key, out IHomeScreenSection? section))
            {
                int cacheSeconds = Math.Clamp(HomeScreenSectionsPlugin.Instance?.Configuration?.SectionCacheSeconds ?? 0, 0, 3600);

                // Sections that take a second or more are logged with how long the work itself took (as opposed to waiting for a
                // thread or the database), to tell a heavy section from a busy server.
                Stopwatch requested = Stopwatch.StartNew();
                long computeMs = -1;
                Func<QueryResult<BaseItemDto>> compute = () =>
                {
                    Stopwatch work = Stopwatch.StartNew();
                    QueryResult<BaseItemDto> result = section.GetResults(payload, queryCollection);
                    computeMs = work.ElapsedMilliseconds;
                    return result;
                };

                QueryResult<BaseItemDto> answer = cacheSeconds <= 0
                    ? compute()
                    : m_resultCache.GetOrCompute(key, payload.UserId, queryCollection, cacheSeconds, section is PluginDefinedSection, compute);

                if (requested.ElapsedMilliseconds >= 1000)
                {
                    m_logger.LogInformation("[hss-slow] {Section} total={Total} ms compute={Compute} ms ({Kind}) items={Count}",
                        key, requested.ElapsedMilliseconds, computeMs, computeMs < 0 ? "cache or shared" : "computed", answer.Items?.Count ?? 0);
                }

                return answer;
            }

            return new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>());
        }

        /// <inheritdoc/>
        public void RegisterResultsDelegate<T>() where T : IHomeScreenSection
        {
            T handler = ActivatorUtilities.CreateInstance<T>(m_serviceProvider);

            RegisterResultsDelegate(handler);
        }

        public void RegisterResultsDelegate<T>(T handler) where T : IHomeScreenSection
        {
            if (handler.Section != null)
            {
                m_delegates[handler.Section] = handler;
            }
        }

        public void RegisterResultsDelegate(Type homeScreenSectionType)
        {
            IHomeScreenSection handler = (IHomeScreenSection)ActivatorUtilities.CreateInstance(m_serviceProvider, homeScreenSectionType);

            if (handler.Section != null)
            {
                if (!m_delegates.ContainsKey(handler.Section))
                {
                    m_delegates.Add(handler.Section, handler);
                }
                else
                {
                    throw new Exception($"Section type '{handler.Section}' has already been registered to type '{m_delegates[handler.Section].GetType().FullName}'.");
                }
            }
        }

        /// <inheritdoc/>
        public bool GetUserFeatureEnabled(Guid userId)
        {
            if (m_userFeatureEnabledStates.ContainsKey(userId))
            {
                return m_userFeatureEnabledStates[userId];
            }

            m_userFeatureEnabledStates.Add(userId, false);

            return false;
        }

        /// <inheritdoc/>
        public void SetUserFeatureEnabled(Guid userId, bool enabled)
        {
            if (!m_userFeatureEnabledStates.ContainsKey(userId))
            {
                m_userFeatureEnabledStates.Add(userId, enabled);
            }

            m_userFeatureEnabledStates[userId] = enabled;

            string userFeatureEnabledPath = Path.Combine(m_applicationPaths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!, "userFeatureEnabled.json");
            new FileInfo(userFeatureEnabledPath).Directory?.Create();
            File.WriteAllText(userFeatureEnabledPath, JObject.FromObject(m_userFeatureEnabledStates).ToString(Formatting.Indented));
        }

        /// <inheritdoc/>
        public ModularHomeUserSettings? GetUserSettings(Guid userId)
        {
            string pluginSettings = Path.Combine(m_applicationPaths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!, c_settingsFile);

            IEnumerable<SectionSettings> adminLockedSections =
                HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.Where(x => !x.AllowUserOverride);
            IEnumerable<SectionSettings> defaultEnabledSections =
                HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.Where(x => x.Enabled);
            
            ModularHomeUserSettings? settings = new ModularHomeUserSettings
            {
                UserId = userId,
                LockedSections = adminLockedSections.Select(x => x.SectionId).ToList(),
                DefaultEnabledSections = defaultEnabledSections.Select(x => x.SectionId).ToList()
            };
            if (File.Exists(pluginSettings))
            {
                JArray settingsArray = JArray.Parse(File.ReadAllText(pluginSettings));

                if (settingsArray.Select(x => JsonConvert.DeserializeObject<ModularHomeUserSettings>(x.ToString())).Any(x => x != null && x.UserId.Equals(userId)))
                {
                    settings = settingsArray.Select(x => JsonConvert.DeserializeObject<ModularHomeUserSettings>(x.ToString())).First(x => x != null && x.UserId.Equals(userId));
                }
            }

            // If there are none enabled by the user then add all the default enabled settings.
            if (settings?.EnabledSections.Count == 0)
            {
                settings.EnabledSections.AddRange(HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.Where(x => x.Enabled).Select(x => x.SectionId));
            }

            if (settings != null)
            {
                IEnumerable<SectionSettings> forcedSectionSettings = HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings.Where(x => !x.AllowUserOverride);

                foreach (SectionSettings sectionSettings in forcedSectionSettings)
                {
                    if (sectionSettings.Enabled && !settings.EnabledSections.Contains(sectionSettings.SectionId))
                    {
                        settings.EnabledSections.Add(sectionSettings.SectionId);
                    }
                    else if (!sectionSettings.Enabled && settings.EnabledSections.Contains(sectionSettings.SectionId))
                    {
                        settings.EnabledSections.Remove(sectionSettings.SectionId);
                    }
                }
            }
            
            return settings;
        }

        /// <inheritdoc/>
        public bool UpdateUserSettings(Guid userId, ModularHomeUserSettings userSettings)
        {
            m_logger.LogInformation($"Updating user settings for user {userId}");
            m_logger.LogInformation($"Json of user settings received from browser: {JsonConvert.SerializeObject(userSettings)}");
            
            string pluginSettings = Path.Combine(m_applicationPaths.PluginConfigurationsPath, typeof(HomeScreenSectionsPlugin).Namespace!, c_settingsFile);
            m_logger.LogInformation($"Plugin settings file: {pluginSettings}");
            
            FileInfo fInfo = new FileInfo(pluginSettings);
            
            m_logger.LogInformation($"Creating directory: '{fInfo.Directory?.FullName}' if it doesn't exist.");
            fInfo.Directory?.Create();

            JArray settings = new JArray();
            List<ModularHomeUserSettings?> newSettings = new List<ModularHomeUserSettings?>();

            m_logger.LogInformation($"Checking if user settings already exist for user {userId} and reading it if so.");
            if (File.Exists(pluginSettings))
            {
                m_logger.LogInformation($"User settings file exists.");
                settings = JArray.Parse(File.ReadAllText(pluginSettings));
                
                m_logger.LogInformation($"Parsed user settings: {settings.ToString(Formatting.None)}");
                newSettings = settings.Select(x => JsonConvert.DeserializeObject<ModularHomeUserSettings>(x.ToString())).ToList()!;
                
                m_logger.LogInformation($"Removing all existing user settings for user {userId} and adding the new one.");
                newSettings.RemoveAll(x => x != null && x.UserId.Equals(userId));

                newSettings.Add(userSettings);

                settings.Clear();
            }

            m_logger.LogInformation($"Adding user settings for user {userId} to the settings array.");
            foreach (ModularHomeUserSettings? userSetting in newSettings)
            {
                settings.Add(JObject.FromObject(userSetting ?? new ModularHomeUserSettings()));
            }

            m_logger.LogInformation($"Writing user settings to file: {pluginSettings}");
            File.WriteAllText(pluginSettings, settings.ToString(Formatting.Indented));

            m_logger.LogInformation($"Content of written settings json: {File.ReadAllText(pluginSettings)}");
            
            m_logger.LogInformation($"User settings updated.");
            return true;
        }
    }
}
