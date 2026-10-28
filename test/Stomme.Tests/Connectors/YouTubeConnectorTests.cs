#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Connectors.YouTube;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - YouTube (n8n's YouTube node: channels, playlists, playlist items, videos and video categories): the refresh token
    /// exchanged at the token endpoint, the access token sent as a bearer; channels, playlists, a playlist's items and videos page
    /// by page by YouTube's token up to the most asked for, a page no bigger than what is left - the account's own by default, or
    /// by ids, a handle, a user name, a channel, a search; each a record, counts as numbers, a duration in seconds too, the largest
    /// thumbnail; a channel's branding, a playlist and a video changed with what is not given kept; a playlist made and deleted, a
    /// video put in one and taken out, a video rated and deleted; a region's categories; what could go wrong refused before
    /// anything is sent - not even a token asked for; YouTube's refusal its reason and message; a test naming the channel.
    /// </summary>
    public sealed class YouTubeConnectorTests
    {
        private const string Api = "https://youtube.example.test/youtube/v3";

        private const string Channel = "UC_x5XG1OV2P6uZZ5FSM9Ttw";

        private const string Playlist = "PLBCF2DAC6FFB574DE";

        private const string Video = "dQw4w9WgXcQ";

        private const string Item = "UExCQ0YyREFDNkZGQjU3NERFLjU2QjQ0RjZEMTA1NTdDQzY=";

        private const string ChannelParts = "snippet,statistics,contentDetails,brandingSettings,status";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> YouTube { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = YouTube(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Calls =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), Api, operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static YouTubeConnector Connector() => new(new OAuth2TokenProvider());

        private static string ChannelJson(int n) =>
            "{\"kind\":\"youtube#channel\",\"id\":\"UC_x5XG1OV2P6uZZ5FSM9T" + n.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "\",\"snippet\":{\"title\":\"Café <b>" + n + "</b>\",\"description\":\"Recipes\",\"customUrl\":\"@cafe" + n + "\",\"publishedAt\":\"2020-01-02T03:04:05Z\","
            + "\"thumbnails\":{\"default\":{\"url\":\"https://yt.example/d.jpg\"},\"high\":{\"url\":\"https://yt.example/h.jpg\"}},\"country\":\"SE\"},"
            + "\"statistics\":{\"viewCount\":\"1234567\",\"subscriberCount\":\"890\",\"hiddenSubscriberCount\":false,\"videoCount\":\"42\"},\"contentDetails\":{\"relatedPlaylists\":{\"likes\":\"\",\"uploads\":\"UU_x5XG1OV2P6uZZ5FSM9Ttw\"}},"
            + "\"brandingSettings\":{\"channel\":{\"title\":\"Café\",\"keywords\":\"café \\\"slow food\\\"\",\"unsubscribedTrailer\":\"" + Video + "\",\"defaultLanguage\":\"sv\"}},\"status\":{\"privacyStatus\":\"public\",\"madeForKids\":false}}";

        private static string Page(Func<int, string> one, int from, int count, string? next) =>
            "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(one)) + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";

        private static string SearchJson(int n) =>
            "{\"kind\":\"youtube#searchResult\",\"id\":{\"kind\":\"youtube#video\",\"videoId\":\"vid" + n.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture) + "\"},\"snippet\":{\"publishedAt\":\"2026-10-01T09:00:00Z\",\"channelId\":\"" + Channel + "\","
            + "\"title\":\"Menu " + n + "\",\"description\":\"Café &amp; more\",\"thumbnails\":{\"medium\":{\"url\":\"https://yt.example/m.jpg\"}},\"channelTitle\":\"Café\",\"liveBroadcastContent\":\"none\"}}";

        private const string VideoJson = "{\"id\":\"" + Video + "\",\"snippet\":{\"publishedAt\":\"2026-10-01T09:00:00Z\",\"channelId\":\"" + Channel + "\",\"title\":\"Café menu\",\"description\":\"Fika & bullar\",\"tags\":[\"café\",\"fika\"],\"categoryId\":\"26\","
            + "\"channelTitle\":\"Café\",\"defaultLanguage\":\"sv\",\"thumbnails\":{\"maxres\":{\"url\":\"https://yt.example/x.jpg\"},\"default\":{\"url\":\"https://yt.example/d.jpg\"}}},"
            + "\"contentDetails\":{\"duration\":\"PT1H4M13S\",\"definition\":\"hd\",\"caption\":\"true\"},\"statistics\":{\"viewCount\":\"1000\",\"likeCount\":\"50\",\"commentCount\":\"7\"},"
            + "\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"private\",\"publishAt\":\"2026-10-20T07:00:00Z\",\"license\":\"youtube\",\"embeddable\":true,\"publicStatsViewable\":false,\"madeForKids\":false,\"selfDeclaredMadeForKids\":false}}";

        [Fact]
        public async Task Channels_and_videos_are_read_page_by_page_the_accounts_own_by_default()
        {
            var google = new Google { YouTube = (_, url) => (HttpStatusCode.OK, url.Contains("pageToken=CDIQ%2BA", StringComparison.Ordinal) ? Page(ChannelJson, 51, 20, null) : Page(ChannelJson, 1, 50, "CDIQ+A")) };
            var channels = await Contexts.DrainAsync(Connector(), Context(google, YouTubeConnector.ChannelsOperation, new()));

            Assert.Equal(70, channels.Count);
            Assert.All(google.Calls, call => Assert.Equal(("GET", "Bearer access-1"), (call.Method.Method, call.Authorization)));
            Assert.Equal(new[] { $"{Api}/channels?part={ChannelParts}&mine=true&maxResults=50", $"{Api}/channels?part={ChannelParts}&mine=true&maxResults=50&pageToken=CDIQ%2BA" }, google.Calls.Select(call => call.Url));
            var first = channels[0].Fields;
            Assert.Equal(("UC_x5XG1OV2P6uZZ5FSM9T01", "Café <b>1</b>", "Recipes", "@cafe1", "2020-01-02T03:04:05Z", "SE", "sv"), ((string?)first["id"], (string?)first["title"], (string?)first["description"], (string?)first["handle"], (string?)first["published_at"], (string?)first["country"], (string?)first["default_language"]));
            Assert.Equal(("café \"slow food\"", Video, 890L, 1234567L, 42L, "UU_x5XG1OV2P6uZZ5FSM9Ttw", "public", false, "https://yt.example/h.jpg"),
                ((string?)first["keywords"], (string?)first["trailer"], (long?)first["subscribers"], (long?)first["views"], (long?)first["videos"], (string?)first["uploads"], (string?)first["privacy"], (bool?)first["made_for_kids"], (string?)first["thumbnail"])); // counts as numbers, the largest thumbnail

            var few = new Google { YouTube = (_, _) => (HttpStatusCode.OK, Page(ChannelJson, 1, 50, "next")) };
            Assert.Equal(60, (await Contexts.DrainAsync(Connector(), Context(few, YouTubeConnector.ChannelsOperation, new() { ["id"] = Channel + ", UC_x5XG1OV2P6uZZ5FSM9T02," + Channel, ["max"] = "60" }))).Count);
            Assert.Equal(new[] { $"{Api}/channels?part={ChannelParts}&id={Channel},UC_x5XG1OV2P6uZZ5FSM9T02&maxResults=50", $"{Api}/channels?part={ChannelParts}&id={Channel},UC_x5XG1OV2P6uZZ5FSM9T02&maxResults=10&pageToken=next" }, few.Calls.Select(call => call.Url)); // each id once; a page no bigger than what is left
            await Contexts.DrainAsync(Connector(), Context(few, YouTubeConnector.ChannelsOperation, new() { ["handle"] = "cafeshop", ["max"] = "1" }));
            Assert.Equal($"{Api}/channels?part={ChannelParts}&forHandle=%40cafeshop&maxResults=1", few.Calls[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(few, YouTubeConnector.ChannelsOperation, new() { ["handle"] = "@cafe.shop", ["max"] = "1" }));
            Assert.Equal($"{Api}/channels?part={ChannelParts}&forHandle=%40cafe.shop&maxResults=1", few.Calls[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(few, YouTubeConnector.ChannelsOperation, new() { ["username"] = "Café & Shop", ["max"] = "1" }));
            Assert.Equal($"{Api}/channels?part={ChannelParts}&forUsername=Caf%C3%A9%20%26%20Shop&maxResults=1", few.Calls[^1].Url);

            var search = new Google { YouTube = (_, url) => (HttpStatusCode.OK, url.Contains("pageToken=", StringComparison.Ordinal) ? Page(SearchJson, 51, 30, null) : Page(SearchJson, 1, 50, "CDIQAA")) };
            var videos = await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new()));
            Assert.Equal(80, videos.Count);
            Assert.Equal($"{Api}/search?part=snippet&type=video&forMine=true&maxResults=50", search.Calls[0].Url); // the account's own by default
            Assert.Equal(("vid00000001", "Menu 1", "Café &amp; more", Channel, "Café", "2026-10-01T09:00:00Z", "none", "https://yt.example/m.jpg"),
                ((string?)videos[0].Fields["id"], (string?)videos[0].Fields["title"], (string?)videos[0].Fields["description"], (string?)videos[0].Fields["channel_id"], (string?)videos[0].Fields["channel_title"], (string?)videos[0].Fields["published_at"], (string?)videos[0].Fields["live"], (string?)videos[0].Fields["thumbnail"]));
            await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new()
            {
                ["query"] = "café & menu", ["channel"] = Channel, ["category"] = "26", ["region"] = "se", ["published_after"] = "2026-10-01", ["published_before"] = "2026-10-12T09:00:00+02:00",
                ["event"] = "Live", ["order"] = "viewcount", ["safe_search"] = "STRICT", ["max"] = "5",
            }));
            Assert.Equal($"{Api}/search?part=snippet&type=video&q=caf%C3%A9%20%26%20menu&channelId={Channel}&videoCategoryId=26&regionCode=SE&publishedAfter=2026-10-01T00%3A00%3A00Z&publishedBefore=2026-10-12T07%3A00%3A00Z&eventType=live&order=viewCount&safeSearch=strict&maxResults=5", search.Calls[^1].Url); // a search: not the account's own
            await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new() { ["query"] = "fika", ["mine"] = "true", ["max"] = "5" }));
            Assert.Equal($"{Api}/search?part=snippet&type=video&forMine=true&q=fika&maxResults=5", search.Calls[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new() { ["order"] = "date", ["max"] = "5" }));
            Assert.Equal($"{Api}/search?part=snippet&type=video&forMine=true&order=date&maxResults=5", search.Calls[^1].Url); // an order alone: still the account's own
            await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new() { ["region"] = "SE", ["max"] = "5" }));
            Assert.Equal($"{Api}/search?part=snippet&type=video&regionCode=SE&maxResults=5", search.Calls[^1].Url); // a region alone: a search
            await Contexts.DrainAsync(Connector(), Context(search, YouTubeConnector.VideosOperation, new() { ["mine"] = "false", ["max"] = "5" }));
            Assert.Equal($"{Api}/search?part=snippet&type=video&maxResults=5", search.Calls[^1].Url);
        }

        [Fact]
        public async Task Playlists_and_their_items_are_read_made_changed_and_deleted()
        {
            const string PlaylistJson = "{\"id\":\"" + Playlist + "\",\"snippet\":{\"publishedAt\":\"2026-01-01T00:00:00Z\",\"channelId\":\"" + Channel + "\",\"title\":\"Fika\",\"description\":\"Kept\",\"tags\":[\"a\",\"b\"],\"defaultLanguage\":\"en\",\"channelTitle\":\"Café\","
                + "\"thumbnails\":{\"standard\":{\"url\":\"https://yt.example/s.jpg\"}}},\"status\":{\"privacyStatus\":\"public\"},\"contentDetails\":{\"itemCount\":12}}";
            const string ItemJson = "{\"id\":\"" + Item + "\",\"snippet\":{\"publishedAt\":\"2026-10-02T00:00:00Z\",\"channelId\":\"" + Channel + "\",\"title\":\"Menu\",\"description\":\"D\",\"playlistId\":\"" + Playlist + "\",\"position\":3,"
                + "\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\"" + Video + "\"},\"videoOwnerChannelTitle\":\"Other café\"},\"contentDetails\":{\"videoId\":\"" + Video + "\",\"note\":\"Watch first\",\"videoPublishedAt\":\"2025-05-05T05:05:05Z\"},\"status\":{\"privacyStatus\":\"unlisted\"}}";
            var google = new Google
            {
                YouTube = (method, url) =>
                    method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : url.Contains("/playlistItems", StringComparison.Ordinal) ? (HttpStatusCode.OK, method == HttpMethod.Post ? ItemJson : "{\"items\":[" + ItemJson + "]}")
                    : (HttpStatusCode.OK, method == HttpMethod.Get ? "{\"items\":[" + PlaylistJson + "]}" : PlaylistJson),
            };
            (string, string, string?) Last() => (google.Calls[^1].Method.Method, google.Calls[^1].Url, google.Calls[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(google, operation, parameters));

            var mine = Assert.Single(await Run(YouTubeConnector.PlaylistsOperation, new()));
            Assert.Equal($"{Api}/playlists?part=snippet,status,contentDetails&mine=true&maxResults=50", Last().Item2);
            Assert.Equal((Playlist, "Fika", "Kept", Channel, "Café", "2026-01-01T00:00:00Z", "a, b", "en", "public", 12L, "https://yt.example/s.jpg"),
                ((string?)mine.Fields["id"], (string?)mine.Fields["title"], (string?)mine.Fields["description"], (string?)mine.Fields["channel_id"], (string?)mine.Fields["channel_title"], (string?)mine.Fields["published_at"], (string?)mine.Fields["tags"], (string?)mine.Fields["default_language"], (string?)mine.Fields["privacy"], (long?)mine.Fields["videos"], (string?)mine.Fields["thumbnail"]));
            await Run(YouTubeConnector.PlaylistsOperation, new() { ["channel"] = Channel });
            Assert.Equal($"{Api}/playlists?part=snippet,status,contentDetails&channelId={Channel}&maxResults=50", Last().Item2);
            await Run(YouTubeConnector.PlaylistsOperation, new() { ["id"] = Playlist });
            Assert.Equal($"{Api}/playlists?part=snippet,status,contentDetails&id={Playlist}&maxResults=50", Last().Item2);
            Assert.Equal("Fika", (string?)Assert.Single(await Run(YouTubeConnector.GetPlaylistOperation, new() { ["playlist"] = Playlist })).Fields["title"]);
            Assert.Equal(("GET", $"{Api}/playlists?part=snippet,status,contentDetails&id={Playlist}", (string?)null), Last());

            await Run(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "Fika & café", ["description"] = "Line one\nLine two", ["privacy"] = "Unlisted", ["tags"] = "fika, café, fika", ["default_language"] = "sv" });
            Assert.Equal(("POST", $"{Api}/playlists?part=snippet,status", (string?)"{\"snippet\":{\"title\":\"Fika & café\",\"description\":\"Line one\\nLine two\",\"tags\":[\"fika\",\"café\"],\"defaultLanguage\":\"sv\"},\"status\":{\"privacyStatus\":\"unlisted\"}}"), Last());
            await Run(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "Plain" });
            Assert.Equal(("POST", $"{Api}/playlists?part=snippet", (string?)"{\"snippet\":{\"title\":\"Plain\"}}"), Last()); // no privacy: YouTube's own

            await Run(YouTubeConnector.UpdatePlaylistOperation, new() { ["playlist"] = Playlist, ["privacy"] = "private", ["tags"] = "c" });
            Assert.Equal(new[]
            {
                ("GET", $"{Api}/playlists?part=snippet,status&id={Playlist}", (string?)null),
                ("PUT", $"{Api}/playlists?part=snippet,status", "{\"id\":\"" + Playlist + "\",\"snippet\":{\"title\":\"Fika\",\"description\":\"Kept\",\"tags\":[\"c\"],\"defaultLanguage\":\"en\"},\"status\":{\"privacyStatus\":\"private\"}}"),
            }, google.Calls.TakeLast(2).Select(call => (call.Method.Method, call.Url, call.Body))); // what is not given kept, only what may be written sent back
            var gone = Assert.Single(await Run(YouTubeConnector.DeletePlaylistOperation, new() { ["playlist"] = Playlist }));
            Assert.Equal(("DELETE", $"{Api}/playlists?id={Playlist}", (string?)null), Last());
            Assert.Equal((Playlist, true), ((string?)gone.Fields["playlist"], (bool?)gone.Fields["done"]));

            var item = Assert.Single(await Run(YouTubeConnector.PlaylistItemsOperation, new() { ["playlist"] = Playlist, ["video"] = Video }));
            Assert.Equal($"{Api}/playlistItems?part=snippet,contentDetails,status&playlistId={Playlist}&videoId={Video}&maxResults=50", Last().Item2);
            Assert.Equal((Item, Playlist, Video, "Menu", "D", 3L, "2026-10-02T00:00:00Z", "2025-05-05T05:05:05Z", "Watch first", "Other café", "unlisted"),
                ((string?)item.Fields["id"], (string?)item.Fields["playlist_id"], (string?)item.Fields["video_id"], (string?)item.Fields["title"], (string?)item.Fields["description"], (long?)item.Fields["position"], (string?)item.Fields["added_at"], (string?)item.Fields["video_published_at"], (string?)item.Fields["note"], (string?)item.Fields["video_channel"], (string?)item.Fields["privacy"]));
            await Run(YouTubeConnector.GetPlaylistItemOperation, new() { ["item"] = Item });
            Assert.Equal(("GET", $"{Api}/playlistItems?part=snippet,contentDetails,status&id=UExCQ0YyREFDNkZGQjU3NERFLjU2QjQ0RjZEMTA1NTdDQzY%3D", (string?)null), Last());
            await Run(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist, ["video"] = Video, ["position"] = "0", ["note"] = "Watch first" });
            Assert.Equal(("POST", $"{Api}/playlistItems?part=snippet,contentDetails", (string?)("{\"snippet\":{\"playlistId\":\"" + Playlist + "\",\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\"" + Video + "\"},\"position\":0},\"contentDetails\":{\"note\":\"Watch first\"}}")), Last());
            await Run(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist, ["video"] = Video });
            Assert.Equal(("POST", $"{Api}/playlistItems?part=snippet", (string?)("{\"snippet\":{\"playlistId\":\"" + Playlist + "\",\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\"" + Video + "\"}}}")), Last()); // the end
            var removed = Assert.Single(await Run(YouTubeConnector.DeletePlaylistItemOperation, new() { ["item"] = Item }));
            Assert.Equal(("DELETE", $"{Api}/playlistItems?id=UExCQ0YyREFDNkZGQjU3NERFLjU2QjQ0RjZEMTA1NTdDQzY%3D", (string?)null), Last());
            Assert.Equal((Item, true), ((string?)removed.Fields["item"], (bool?)removed.Fields["done"]));
        }

        [Fact]
        public async Task A_video_is_got_changed_rated_and_deleted_a_channels_branding_changed_and_categories_read()
        {
            var google = new Google
            {
                YouTube = (method, url) =>
                    method == HttpMethod.Delete || url.Contains("/videos/rate", StringComparison.Ordinal) ? (HttpStatusCode.NoContent, "")
                    : url.Contains("/videoCategories", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"items\":[{\"id\":\"1\",\"snippet\":{\"title\":\"Film & Animation\",\"assignable\":true}},{\"id\":\"18\",\"snippet\":{\"title\":\"Short Movies\",\"assignable\":false}},{\"id\":\"26\",\"snippet\":{\"title\":\"Howto & Style\",\"assignable\":true}}]}")
                    : url.Contains("/channels", StringComparison.Ordinal) ? (HttpStatusCode.OK, method == HttpMethod.Put ? ChannelJson(1)
                        : "{\"items\":[{\"id\":\"" + Channel + "\",\"brandingSettings\":{\"channel\":{\"title\":\"Café\",\"description\":\"Old\",\"keywords\":\"a b\",\"country\":\"SE\"},\"image\":{\"bannerExternalUrl\":\"https://yt.example/banner\"},\"watch\":{\"textColor\":\"#fff\"}}}]}")
                    : (HttpStatusCode.OK, method == HttpMethod.Get ? "{\"items\":[" + VideoJson + "]}" : VideoJson),
            };
            (string, string, string?) Last() => (google.Calls[^1].Method.Method, google.Calls[^1].Url, google.Calls[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(google, operation, parameters));

            var video = Assert.Single(await Run(YouTubeConnector.GetVideoOperation, new() { ["video"] = Video })).Fields;
            Assert.Equal(("GET", $"{Api}/videos?part=snippet,contentDetails,statistics,status&id={Video}", (string?)null), Last());
            Assert.Equal((Video, "Café menu", "Fika & bullar", Channel, "Café", "26", "café, fika", "sv"), ((string?)video["id"], (string?)video["title"], (string?)video["description"], (string?)video["channel_id"], (string?)video["channel_title"], (string?)video["category_id"], (string?)video["tags"], (string?)video["default_language"]));
            Assert.Equal(("PT1H4M13S", 3853L, "hd", true, 1000L, 50L, 7L), ((string?)video["duration"], (long?)video["seconds"], (string?)video["definition"], (bool?)video["captions"], (long?)video["views"], (long?)video["likes"], (long?)video["comments"])); // a duration in seconds too
            Assert.Equal(("private", "2026-10-20T07:00:00Z", true, "youtube", false, "https://yt.example/x.jpg", "2026-10-01T09:00:00Z"), ((string?)video["privacy"], (string?)video["publish_at"], (bool?)video["embeddable"], (string?)video["license"], (bool?)video["made_for_kids"], (string?)video["thumbnail"], (string?)video["published_at"]));

            await Run(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["title"] = "Café & fika", ["privacy"] = "PUBLIC", ["public_stats"] = "true", ["publish_at"] = "2026-10-21T09:00:00+02:00" });
            Assert.Equal(new[]
            {
                ("GET", $"{Api}/videos?part=snippet,status&id={Video}", (string?)null),
                ("PUT", $"{Api}/videos?part=snippet,status", "{\"id\":\"" + Video + "\",\"snippet\":{\"title\":\"Café & fika\",\"description\":\"Fika & bullar\",\"tags\":[\"café\",\"fika\"],\"categoryId\":\"26\",\"defaultLanguage\":\"sv\"},"
                    + "\"status\":{\"privacyStatus\":\"public\",\"embeddable\":true,\"license\":\"youtube\",\"publicStatsViewable\":true,\"publishAt\":\"2026-10-21T07:00:00Z\",\"selfDeclaredMadeForKids\":false}}"),
            }, google.Calls.TakeLast(2).Select(call => (call.Method.Method, call.Url, call.Body))); // what is not given kept; YouTube's own status values not sent back
            await Run(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["description"] = "Ny", ["tags"] = "a, b", ["category"] = "22", ["default_language"] = "en-GB", ["embeddable"] = "false", ["made_for_kids"] = "TRUE", ["license"] = "creativecommon" });
            Assert.Equal("{\"id\":\"" + Video + "\",\"snippet\":{\"title\":\"Café menu\",\"description\":\"Ny\",\"tags\":[\"a\",\"b\"],\"categoryId\":\"22\",\"defaultLanguage\":\"en-GB\"},"
                + "\"status\":{\"privacyStatus\":\"private\",\"embeddable\":false,\"license\":\"creativeCommon\",\"publicStatsViewable\":false,\"publishAt\":\"2026-10-20T07:00:00Z\",\"selfDeclaredMadeForKids\":true}}", Last().Item3);

            var rated = Assert.Single(await Run(YouTubeConnector.RateVideoOperation, new() { ["video"] = Video, ["rating"] = "LIKE" }));
            Assert.Equal(("POST", $"{Api}/videos/rate?id={Video}&rating=like", (string?)null), Last());
            Assert.Equal((Video, "like", true), ((string?)rated.Fields["video"], (string?)rated.Fields["rating"], (bool?)rated.Fields["done"]));
            var deleted = Assert.Single(await Run(YouTubeConnector.DeleteVideoOperation, new() { ["video"] = Video }));
            Assert.Equal(("DELETE", $"{Api}/videos?id={Video}", (string?)null), Last());
            Assert.Equal((Video, true), ((string?)deleted.Fields["video"], (bool?)deleted.Fields["done"]));

            await Run(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["description"] = "Nytt <b>café</b>", ["country"] = "no", ["trailer"] = Video });
            Assert.Equal(new[]
            {
                ("GET", $"{Api}/channels?part=brandingSettings&id={Channel}", (string?)null),
                ("PUT", $"{Api}/channels?part=brandingSettings", "{\"id\":\"" + Channel + "\",\"brandingSettings\":{\"channel\":{\"title\":\"Café\",\"description\":\"Nytt <b>café</b>\",\"keywords\":\"a b\",\"country\":\"NO\",\"unsubscribedTrailer\":\"" + Video + "\"},"
                    + "\"image\":{\"bannerExternalUrl\":\"https://yt.example/banner\"}}}"),
            }, google.Calls.TakeLast(2).Select(call => (call.Method.Method, call.Url, call.Body))); // the rest of its branding kept
            Assert.Equal(Channel, (string?)Assert.Single(await Run(YouTubeConnector.GetChannelOperation, new() { ["channel"] = Channel })).Fields["id"]);
            Assert.Equal(("GET", $"{Api}/channels?part={ChannelParts}&id={Channel}", (string?)null), Last());
            await Run(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["keywords"] = "fika \"slow food\"", ["default_language"] = "sv" });
            Assert.Equal("{\"id\":\"" + Channel + "\",\"brandingSettings\":{\"channel\":{\"title\":\"Café\",\"description\":\"Old\",\"keywords\":\"fika \\\"slow food\\\"\",\"country\":\"SE\",\"defaultLanguage\":\"sv\"},\"image\":{\"bannerExternalUrl\":\"https://yt.example/banner\"}}}", Last().Item3);

            var categories = await Run(YouTubeConnector.VideoCategoriesOperation, new());
            Assert.Equal(("GET", $"{Api}/videoCategories?part=snippet&regionCode=US", (string?)null), Last()); // YouTube's region by default
            Assert.Equal(new[] { ("1", "Film & Animation", (bool?)true), ("18", "Short Movies", false), ("26", "Howto & Style", true) }, categories.Select(category => ((string)category.Fields["id"]!, (string)category.Fields["title"]!, (bool?)category.Fields["assignable"])));
            Assert.Equal(2, (await Run(YouTubeConnector.VideoCategoriesOperation, new() { ["region"] = "se", ["max"] = "2" })).Count);
            Assert.Equal($"{Api}/videoCategories?part=snippet&regionCode=SE", Last().Item2);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_youtubes_errors_are_its_words()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;

            Assert.Equal("Give the channel - its id.", await Refused(YouTubeConnector.GetChannelOperation, new()));
            Assert.Equal("'UCx' is not a channel's id - UC and 22 more.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = "UCx", ["description"] = "x" }));
            Assert.Equal("'cafe' is not a channel's id - UC and 22 more.", await Refused(YouTubeConnector.PlaylistsOperation, new() { ["channel"] = "cafe" }));
            Assert.Equal("'cafe' is not a channel's id - UC and 22 more.", await Refused(YouTubeConnector.VideosOperation, new() { ["channel"] = "cafe" }));
            Assert.Equal("'x' is not a channel's id - UC and 22 more.", await Refused(YouTubeConnector.ChannelsOperation, new() { ["id"] = Channel + ",x" }));
            Assert.Equal("Give 1 to 50 channels' ids - 0 given.", await Refused(YouTubeConnector.ChannelsOperation, new() { ["id"] = ", ," }));
            Assert.Equal("Give 1 to 50 playlists' ids - 51 given.", await Refused(YouTubeConnector.PlaylistsOperation, new() { ["id"] = string.Join(",", Enumerable.Range(10, 51).Select(n => "PLBCF2DAC6FFB574" + n)) }));
            Assert.Equal("Give one way to find channels - their ids, a handle or a user name.", await Refused(YouTubeConnector.ChannelsOperation, new() { ["id"] = Channel, ["handle"] = "@cafe" }));
            Assert.Equal("'@a b' is not a handle - @cafeshop.", await Refused(YouTubeConnector.ChannelsOperation, new() { ["handle"] = "@a b" }));
            Assert.Equal("The user name has a line break - one line only.", await Refused(YouTubeConnector.ChannelsOperation, new() { ["username"] = "a\nb" }));
            Assert.Equal("Give one way to find playlists - a channel or their ids.", await Refused(YouTubeConnector.PlaylistsOperation, new() { ["id"] = Playlist, ["channel"] = Channel }));
            Assert.Equal("Give the playlist - its id.", await Refused(YouTubeConnector.PlaylistItemsOperation, new()));
            Assert.Equal("'PL x' is not a playlist's id - PL....", await Refused(YouTubeConnector.DeletePlaylistOperation, new() { ["playlist"] = "PL x" }));
            Assert.Equal("'PLBCF2DAC 6FFB574DE' is not a playlist's id - PL....", await Refused(YouTubeConnector.GetPlaylistOperation, new() { ["playlist"] = "PLBCF2DAC 6FFB574DE" }));
            Assert.Equal("Give the playlist item - its id.", await Refused(YouTubeConnector.DeletePlaylistItemOperation, new()));
            Assert.Equal("'a/b' is not a playlist item's id - from Get playlist items.", await Refused(YouTubeConnector.GetPlaylistItemOperation, new() { ["item"] = "a/b" }));
            Assert.Equal("Give the video - its id.", await Refused(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist }));
            Assert.Equal("'dQw4w9WgXc' is not a video's id - the 11 characters after watch?v=.", await Refused(YouTubeConnector.GetVideoOperation, new() { ["video"] = "dQw4w9WgXc" }));
            Assert.Equal("'x' is not a video's id - the 11 characters after watch?v=.", await Refused(YouTubeConnector.PlaylistItemsOperation, new() { ["playlist"] = Playlist, ["video"] = "x" }));
            Assert.Equal("'x' is not a video's id - the 11 characters after watch?v=.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["trailer"] = "x" }));
            Assert.Equal("'-1' is not a position - 0 or more.", await Refused(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist, ["video"] = Video, ["position"] = "-1" }));
            Assert.Equal("The note has a line break - one line only.", await Refused(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist, ["video"] = Video, ["note"] = "a\nb" }));
            Assert.Equal("The note is longer than 280 characters.", await Refused(YouTubeConnector.AddPlaylistItemOperation, new() { ["playlist"] = Playlist, ["video"] = Video, ["note"] = new string('n', 281) }));
            Assert.Equal("Give something to change - a description, keywords, a country, a language or a trailer.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel }));
            Assert.Equal("The description is longer than 1,000 characters.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["description"] = new string('d', 1001) }));
            Assert.Equal("The keywords has a line break - one line only.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["keywords"] = "a\nb" }));
            Assert.Equal("'Sweden' is not a country - two letters, SE.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["country"] = "Sweden" }));
            Assert.Equal("'svenska' is not a language - sv, or en-GB.", await Refused(YouTubeConnector.UpdateChannelOperation, new() { ["channel"] = Channel, ["default_language"] = "svenska" }));
            Assert.Equal("Give the playlist's title.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["description"] = "x" }));
            Assert.Equal("The title has < or > - YouTube refuses both.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "<b>Fika</b>" }));
            Assert.Equal("The title is longer than 150 characters.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = new string('t', 151) }));
            Assert.Equal("The title is longer than 100 characters.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["title"] = new string('t', 101) }));
            Assert.Equal("The title has a line break - one line only.", await Refused(YouTubeConnector.UpdatePlaylistOperation, new() { ["playlist"] = Playlist, ["title"] = "a\nb" }));
            Assert.Equal("The description is longer than 5,000 characters.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "t", ["description"] = new string('d', 5001) }));
            Assert.Equal("The description is longer than 5,000 characters.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["description"] = new string('d', 5001) }));
            Assert.Equal("The description has < or > - YouTube refuses both.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "t", ["description"] = "<b>x</b>" }));
            Assert.Equal("The description has < or > - YouTube refuses both.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["description"] = "a > b" }));
            Assert.Equal("'secret' is not a privacy - private, public or unlisted.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "t", ["privacy"] = "secret" }));
            Assert.Equal("The tag has a line break - one line only.", await Refused(YouTubeConnector.CreatePlaylistOperation, new() { ["title"] = "t", ["tags"] = "a\nb, c" }));
            Assert.Equal("Give something to change - a title, a description, tags, a language or a privacy.", await Refused(YouTubeConnector.UpdatePlaylistOperation, new() { ["playlist"] = Playlist }));
            Assert.Equal("Give something to change - a title, a description, tags, a category, a language, a privacy, a time to publish, whether embeddable, its statistics public or made for kids, or a licence.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video }));
            Assert.Equal("'music' is not a video category's id - 26.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["category"] = "music" }));
            Assert.Equal("'soon' is not a time (publish at) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["publish_at"] = "soon" }));
            Assert.Equal("'yes' is not true or false (made for kids).", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["made_for_kids"] = "yes" }));
            Assert.Equal("'mit' is not a licence - youtube or creativeCommon.", await Refused(YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["license"] = "mit" }));
            Assert.Equal("Give the rating - like, dislike or none.", await Refused(YouTubeConnector.RateVideoOperation, new() { ["video"] = Video }));
            Assert.Equal("'love' is not a rating - like, dislike or none.", await Refused(YouTubeConnector.RateVideoOperation, new() { ["video"] = Video, ["rating"] = "love" }));
            Assert.Equal("The words has a line break - one line only.", await Refused(YouTubeConnector.VideosOperation, new() { ["query"] = "a\nb" }));
            Assert.Equal("'music' is not a video category's id - 26.", await Refused(YouTubeConnector.VideosOperation, new() { ["category"] = "music" }));
            Assert.Equal("'Sweden' is not a region - two letters, SE.", await Refused(YouTubeConnector.VideosOperation, new() { ["region"] = "Sweden" }));
            Assert.Equal("'Sweden' is not a region - two letters, SE.", await Refused(YouTubeConnector.VideoCategoriesOperation, new() { ["region"] = "Sweden" }));
            Assert.Equal("'later' is not a time (published before) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(YouTubeConnector.VideosOperation, new() { ["published_before"] = "later" }));
            Assert.Equal("'past' is not a live event - completed, live or upcoming.", await Refused(YouTubeConnector.VideosOperation, new() { ["event"] = "past" }));
            Assert.Equal("'likes' is not an order - date, rating, relevance, title or viewCount.", await Refused(YouTubeConnector.VideosOperation, new() { ["order"] = "likes" }));
            Assert.Equal("'off' is not a safe search - moderate, none or strict.", await Refused(YouTubeConnector.VideosOperation, new() { ["safe_search"] = "off" }));
            Assert.Equal("'yes' is not true or false (mine).", await Refused(YouTubeConnector.VideosOperation, new() { ["mine"] = "yes" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(YouTubeConnector.VideosOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("YouTube has no operation 'comments' - channels, get_channel, update_channel,", await Refused("comments", new()));
            Assert.Empty(google.Requests); // nothing sent - not even a token asked for

            var quota = new Google { YouTube = (_, _) => (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota.","status":"RESOURCE_EXHAUSTED","errors":[{"message":"x","domain":"youtube.quota","reason":"quotaExceeded"}]}}""") };
            Assert.Equal("HTTP 403 quotaExceeded: The request cannot be completed because you have exceeded your quota.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(quota, YouTubeConnector.VideosOperation, new())))).Message);
            var status = new Google { YouTube = (_, _) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}""") };
            Assert.Equal("HTTP 404 NOT_FOUND: Requested entity was not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(status, YouTubeConnector.DeleteVideoOperation, new() { ["video"] = Video })))).Message);
            var none = new Google { YouTube = (_, _) => (HttpStatusCode.OK, "{\"items\":[]}") };
            Assert.Equal($"No video has the id {Video}.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(none, YouTubeConnector.UpdateVideoOperation, new() { ["video"] = Video, ["title"] = "t" })))).Message);
            Assert.DoesNotContain(none.Calls, call => call.Method == HttpMethod.Put); // nothing changed
            Assert.Equal($"No playlist has the id {Playlist}.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(none, YouTubeConnector.GetPlaylistOperation, new() { ["playlist"] = Playlist })))).Message);
            var silent = new Google { YouTube = (_, _) => (HttpStatusCode.Unauthorized, "") };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, YouTubeConnector.VideoCategoriesOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_channel()
        {
            var google = new Google { YouTube = (_, _) => (HttpStatusCode.OK, "{\"items\":[{\"id\":\"" + Channel + "\",\"snippet\":{\"title\":\"Café Shop\"}}]}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in to the channel Café Shop."), (tested.Success, tested.Message));
            Assert.Equal(("https://oauth2.example.test/token", $"{Api}/channels?part=snippet&mine=true", "Bearer access-1"), (google.Requests[0].Url, google.Calls[0].Url, google.Calls[0].Authorization));
            var two = new Google { YouTube = (_, _) => (HttpStatusCode.OK, "{\"items\":[{\"id\":\"" + Channel + "\",\"snippet\":{\"title\":\"Café Shop\"}},{\"id\":\"UC2\"}]}") };
            Assert.Equal("Signed in to the channel Café Shop and 1 more.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(two), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials))).Message);
            var none = new Google { YouTube = (_, _) => (HttpStatusCode.OK, "{\"items\":[]}") };
            Assert.Equal("Signed in - the account has no channel.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(none), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "youtube"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing }, Connector().Categories);
            Assert.Equal(18, Connector().Operations.Count);
        }
    }
}
