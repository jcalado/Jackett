﻿using CsQuery;
using Jackett.Models;
using Jackett.Models.IndexerConfig;
using Jackett.Services;
using Jackett.Utils;
using Jackett.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Jackett.Indexers
{
    public class AnimeBytes : BaseIndexer, IIndexer
    {
        private string LoginUrl { get { return SiteLink + "user/login"; } }
        private string SearchUrl { get { return SiteLink + "torrents.php?filter_cat[1]=1"; } }
        public bool AllowRaws { get; private set; }

        public AnimeBytes(IIndexerManagerService i, IWebClient client, Logger l)
            : base(name: "AnimeBytes",
                link: "https://animebytes.tv/",
                description: "Powered by Tentacles",
                manager: i,
                client: client,
                caps: new TorznabCapabilities(TorznabCategory.Anime),
                logger: l)
        {
        }

        public Task<ConfigurationData> GetConfigurationForSetup()
        {
            return Task.FromResult<ConfigurationData>(new ConfigurationDataBasicLoginAnimeBytes());
        }

        public async Task ApplyConfiguration(JToken configJson)
        {
            var config = new ConfigurationDataBasicLoginAnimeBytes();
            config.LoadValuesFromJson(configJson);

            lock (cache)
            {
                cache.Clear();
            }

            // Get the login form as we need the CSRF Token
            var loginPage = await webclient.GetString(new Utils.Clients.WebRequest()
            {
                Url = LoginUrl
            });

            CQ loginPageDom =loginPage.Content;
            var csrfToken = loginPageDom["input[name=\"csrf_token\"]"].Last();

            // Build login form
            var pairs = new Dictionary<string, string> {
                    { "csrf_token", csrfToken.Attr("value") },
				    { "username", config.Username.Value },
				    { "password", config.Password.Value },
                    { "keeplogged_sent", "true" },
                    { "keeplogged", "on" },
                    { "login", "Log In!" }
			};

            // Do the login
            var request = new Utils.Clients.WebRequest()
            {
                Cookies = loginPage.Cookies,
                PostData = pairs,
                Referer = LoginUrl,
                Type = RequestType.POST,
                Url = LoginUrl
            };
            var response = await RequestLoginAndFollowRedirect(LoginUrl, pairs, loginPage.Cookies, true, null);

            // Follow the redirect
            await FollowIfRedirect(response, request.Url, SearchUrl);

            if (!(response.Content != null && response.Content.Contains("/user/logout")))
            {
                // Their login page appears to be broken and just gives a 500 error.
                throw new ExceptionWithConfigData("Failed to login, 6 failed attempts will get you banned for 6 hours.", (ConfigurationData)config);
            }
            else
            {
                cookieHeader = response.Cookies;
                AllowRaws = config.IncludeRaw.Value;
                var configSaveData = new JObject();
                configSaveData["cookies"] = cookieHeader;
                configSaveData["raws"] = AllowRaws;
                SaveConfig(configSaveData);
                IsConfigured = true;
            }
        }

        public override void LoadFromSavedConfiguration(JToken jsonConfig)
        {
            // The old config used an array - just fail to load it
            if (!(jsonConfig["cookies"] is JArray))
            {
                cookieHeader = (string)jsonConfig["cookies"];
                AllowRaws = jsonConfig["raws"].Value<bool>();
                IsConfigured = true;
            }
        }

        public async Task<ReleaseInfo[]> PerformQuery(TorznabQuery query)
        {
            // The result list
            var releases = new List<ReleaseInfo>();

            foreach (var result in await GetResults(query.SanitizedSearchTerm))
            {
                releases.Add(result);
            }

            return releases.ToArray();
        }

        public async Task<ReleaseInfo[]> GetResults(string searchTerm)
        {
            // This tracker only deals with full seasons so chop off the episode/season number if we have it D:
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var splitindex = searchTerm.LastIndexOf(' ');
                if (splitindex > -1)
                    searchTerm = searchTerm.Substring(0, splitindex);
            }

            // The result list
            var releases = new List<ReleaseInfo>();

            // Check cache first so we don't query the server for each episode when searching for each episode in a series.
            lock (cache)
            {
                // Remove old cache items
                CleanCache();

                var cachedResult = cache.Where(i => i.Query == searchTerm).FirstOrDefault();
                if (cachedResult != null)
                    return cachedResult.Results.Select(s => (ReleaseInfo)s.Clone()).ToArray();
            }

            var queryUrl = SearchUrl;
            // Only include the query bit if its required as hopefully the site caches the non query page
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                queryUrl += "&action=advanced&search_type=title&sort=time_added&way=desc&anime%5Btv_series%5D=1&searchstr=" + WebUtility.UrlEncode(searchTerm);
            }

            // Get the content from the tracker
            var response = await RequestStringWithCookies(queryUrl);
            CQ dom = response.Content;

            // Parse
            try
            {
                var releaseInfo = "S01";
                var root = dom.Find(".anime");
                // We may have got redirected to the series page if we have none of these
                if (root.Count() == 0)
                    root = dom.Find(".torrent_table");

                foreach (var series in root)
                {
                    var seriesCq = series.Cq();

                    var synonyms = new List<string>();
                    var mainTitle = seriesCq.Find(".group_title strong a").First().Text().Trim();

                    var yearStr = seriesCq.Find(".group_title strong").First().Text().Trim().Replace("]", "").Trim();
                    int yearIndex = yearStr.LastIndexOf("[");
                    if (yearIndex > -1)
                        yearStr = yearStr.Substring(yearIndex + 1);

                    int year = 0;
                    if (!int.TryParse(yearStr, out year))
                        year = DateTime.Now.Year;

                    synonyms.Add(mainTitle);

                    // If the title contains a comma then we can't use the synonyms as they are comma seperated
                    if (!mainTitle.Contains(","))
                    {
                        var symnomnNames = string.Empty;
                        foreach (var e in seriesCq.Find(".group_statbox li"))
                        {
                            if (e.FirstChild.InnerText == "Synonyms:")
                            {
                                symnomnNames = e.InnerText;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(symnomnNames))
                        {
                            foreach (var name in symnomnNames.Split(",".ToCharArray(), StringSplitOptions.RemoveEmptyEntries))
                            {
                                var theName = name.Trim();
                                if (!theName.Contains("&#") && !string.IsNullOrWhiteSpace(theName))
                                {
                                    synonyms.Add(theName);
                                }
                            }
                        }
                    }

                    foreach (var title in synonyms)
                    {
                        var releaseRows = seriesCq.Find(".torrent_group tr");

                        // Skip the first two info rows
                        for (int r = 2; r < releaseRows.Count(); r++)
                        {
                            var row = releaseRows.Get(r);
                            var rowCq = row.Cq();
                            if (rowCq.HasClass("edition_info"))
                            {
                                releaseInfo = rowCq.Find("td").Text();

                                if (string.IsNullOrWhiteSpace(releaseInfo))
                                {
                                    // Single episodes alpha - Reported that this info is missing.
                                    // It should self correct when availible
                                    break;
                                }

                                releaseInfo = releaseInfo.Replace("Episode ", "");
                                releaseInfo = releaseInfo.Replace("Season ", "S");
                                releaseInfo = releaseInfo.Trim();
                            }
                            else if (rowCq.HasClass("torrent"))
                            {
                                var links = rowCq.Find("a");
                                // Protect against format changes
                                if (links.Count() != 2)
                                {
                                    continue;
                                }

                                var release = new ReleaseInfo();
                                release.MinimumRatio = 1;
                                release.MinimumSeedTime = 259200;
                                var downloadLink = links.Get(0);

                                // We dont know this so try to fake based on the release year
                                release.PublishDate = new DateTime(year, 1, 1);
                                release.PublishDate = release.PublishDate.AddDays(Math.Min(DateTime.Now.DayOfYear, 365) - 1);

                                var infoLink = links.Get(1);
                                release.Comments = new Uri(SiteLink  + infoLink.Attributes.GetAttribute("href"));
                                release.Guid = new Uri(SiteLink + infoLink.Attributes.GetAttribute("href") + "&nh=" + StringUtil.Hash(title)); // Sonarr should dedupe on this url - allow a url per name.
                                release.Link = new Uri(downloadLink.Attributes.GetAttribute("href"), UriKind.Relative);

                                // We dont actually have a release name >.> so try to create one
                                var releaseTags = infoLink.InnerText.Split("|".ToCharArray(), StringSplitOptions.RemoveEmptyEntries).ToList();
                                for (int i = releaseTags.Count - 1; i >= 0; i--)
                                {
                                    releaseTags[i] = releaseTags[i].Trim();
                                    if (string.IsNullOrWhiteSpace(releaseTags[i]))
                                        releaseTags.RemoveAt(i);
                                }

                                var group = releaseTags.Last();
                                if (group.Contains("(") && group.Contains(")"))
                                {
                                    // Skip raws if set
                                    if (group.ToLowerInvariant().StartsWith("raw") && !AllowRaws)
                                    {
                                        continue;
                                    }

                                    var start = group.IndexOf("(");
                                    group = "[" + group.Substring(start + 1, (group.IndexOf(")") - 1) - start) + "] ";
                                }
                                else
                                {
                                    group = string.Empty;
                                }

                                var infoString = "";

                                for (int i = 0; i + 1 < releaseTags.Count(); i++)
                                {
                                    infoString += "[" + releaseTags[i] + "]";
                                }

                                release.Title = string.Format("{0}{1} {2} {3}", group, title, releaseInfo, infoString);
                                release.Description = title;

                                var size = rowCq.Find(".torrent_size");
                                if (size.Count() > 0)
                                {
                                    release.Size = ReleaseInfo.GetBytes(size.First().Text());
                                }

                                //  Additional 5 hours per GB 
                                release.MinimumSeedTime += (release.Size / 1000000000) * 18000;

                                // Peer info
                                release.Seeders = ParseUtil.CoerceInt(rowCq.Find(".torrent_seeders").Text());
                                release.Peers = release.Seeders + ParseUtil.CoerceInt(rowCq.Find(".torrent_leechers").Text());

                                releases.Add(release);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.Content, ex);
            }

            // Add to the cache
            lock (cache)
            {
                cache.Add(new CachedQueryResult(searchTerm, releases));
            }

            return releases.Select(s => (ReleaseInfo)s.Clone()).ToArray();
        }


        public async override Task<byte[]> Download(Uri link)
        {
            // The urls for this tracker are quite long so append the domain after the incoming request.
            var response = await webclient.GetBytes(new Utils.Clients.WebRequest()
            {
                Url = SiteLink + link.ToString(),
                Cookies = cookieHeader
            });

            return response.Content;
        }
    }
}
