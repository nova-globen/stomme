#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.YouTube
{
    /// <summary>
    /// YouTube (BL-229, n8n's YouTube node: channels, playlists, playlist items, videos and video categories): a channel over the
    /// YouTube Data API v3 (<c>https://www.googleapis.com/youtube/v3</c>) through an OAuth client and a refresh token granted
    /// YouTube's scope, exchanged at Google's token endpoint through the shared <see cref="OAuth2TokenProvider"/>, the access token
    /// sent as a bearer. A test names the channel.
    /// </summary>
    /// <remarks>
    /// A channel, a playlist, a playlist item and a video are YouTube's ids, each held to its shape before anything is sent.
    /// <c>channels</c> (the account's own, or by id, handle or legacy user name), <c>get_channel</c> and <c>update_channel</c> -
    /// its description, keywords, country, language and trailer, the rest of its branding kept; <c>playlists</c> (the account's,
    /// a channel's or by id), <c>get_playlist</c>, <c>create_playlist</c>, <c>update_playlist</c> - what is not given kept - and
    /// <c>delete_playlist</c>; <c>playlist_items</c>, <c>get_playlist_item</c>, <c>add_playlist_item</c> and
    /// <c>delete_playlist_item</c>; <c>videos</c> - the account's own or a search, by words, a channel, a category, a region, two
    /// times, a live event, an order and safe search - <c>get_video</c>, <c>update_video</c> - what is not given kept -
    /// <c>delete_video</c> and <c>rate_video</c>; <c>video_categories</c> of a region. Lists page by page by YouTube's token up to
    /// <c>max</c> (1,000). Each a record of its values - counts as numbers, a duration in seconds too. A throttled call waits as
    /// Google says; a failure is thrown with its status, YouTube's reason and its message.
    /// </remarks>
    public sealed class YouTubeConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://www.googleapis.com/youtube/v3";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string ChannelsOperation = "channels";

        public const string GetChannelOperation = "get_channel";

        public const string UpdateChannelOperation = "update_channel";

        public const string PlaylistsOperation = "playlists";

        public const string GetPlaylistOperation = "get_playlist";

        public const string CreatePlaylistOperation = "create_playlist";

        public const string UpdatePlaylistOperation = "update_playlist";

        public const string DeletePlaylistOperation = "delete_playlist";

        public const string PlaylistItemsOperation = "playlist_items";

        public const string GetPlaylistItemOperation = "get_playlist_item";

        public const string AddPlaylistItemOperation = "add_playlist_item";

        public const string DeletePlaylistItemOperation = "delete_playlist_item";

        public const string VideosOperation = "videos";

        public const string GetVideoOperation = "get_video";

        public const string UpdateVideoOperation = "update_video";

        public const string DeleteVideoOperation = "delete_video";

        public const string RateVideoOperation = "rate_video";

        public const string VideoCategoriesOperation = "video_categories";

        public const int DefaultMax = 1000;

        private const int PageSize = 50;

        private const string ChannelParts = "snippet,statistics,contentDetails,brandingSettings,status";

        private const string PlaylistParts = "snippet,status,contentDetails";

        private const string ItemParts = "snippet,contentDetails,status";

        private const string VideoParts = "snippet,contentDetails,statistics,status";

        private static readonly Regex ChannelId = new("^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant);

        private static readonly Regex PlaylistId = new("^[A-Za-z0-9_-]{10,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex ItemId = new("^[A-Za-z0-9_=-]{10,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);

        private static readonly Regex Handle = new("^@?[A-Za-z0-9._-]{3,30}$", RegexOptions.CultureInvariant);

        private static readonly Regex Region = new("^[A-Za-z]{2}$", RegexOptions.CultureInvariant);

        private static readonly Regex Language = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

        private static readonly Regex CategoryId = new("^[0-9]{1,3}$", RegexOptions.CultureInvariant);

        private static readonly string[] Privacies = { "private", "public", "unlisted" };

        // The status values a video's update may carry back - the rest of its status is YouTube's own.
        private static readonly string[] WritableStatus = { "privacyStatus", "embeddable", "license", "publicStatsViewable", "publishAt", "selfDeclaredMadeForKids", "containsSyntheticMedia" };

        // JSON as written: titles, descriptions and their accents, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            ChannelsOperation, GetChannelOperation, UpdateChannelOperation, PlaylistsOperation, GetPlaylistOperation, CreatePlaylistOperation, UpdatePlaylistOperation, DeletePlaylistOperation,
            PlaylistItemsOperation, GetPlaylistItemOperation, AddPlaylistItemOperation, DeletePlaylistItemOperation, VideosOperation, GetVideoOperation, UpdateVideoOperation, DeleteVideoOperation,
            RateVideoOperation, VideoCategoriesOperation,
        };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "youtube";

        public string DisplayName => "YouTube";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.youtube.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/youtube/v3/docs");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField ChannelField = new("channel", "Channel", ConnectorConfigFieldType.Text, Required: true, Description: "The channel's id - UC and 22 more.");

        private static readonly ConnectorConfigField PlaylistField = new("playlist", "Playlist", ConnectorConfigFieldType.Text, Required: true, Description: "The playlist's id - PL...");

        private static readonly ConnectorConfigField ItemField = new("item", "Playlist item", ConnectorConfigFieldType.Text, Required: true, Description: "The playlist item's id.");

        private static readonly ConnectorConfigField VideoField = new("video", "Video", ConnectorConfigFieldType.Text, Required: true, Description: "The video's id - the 11 characters after watch?v=.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField Privacy() => new("privacy", "Privacy", ConnectorConfigFieldType.Text, Required: false, Description: "private, public or unlisted.");

        private static ConnectorConfigField Tags() => new("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - cooking, café.");

        private static ConnectorConfigField DefaultLanguage() => new("default_language", "Language", ConnectorConfigFieldType.Text, Required: false, Description: "Its language - sv, or en-GB.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ChannelsOperation, "Get channels", "The account's own channels, or channels by id, handle or user name - n8n's channel Get Many.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("id", "Ids", ConnectorConfigFieldType.Text, Required: false, Description: "Channels' ids, comma-separated."),
                    new ConnectorConfigField("handle", "Handle", ConnectorConfigFieldType.Text, Required: false, Description: "A channel's handle - @cafeshop."),
                    new ConnectorConfigField("username", "User name", ConnectorConfigFieldType.Text, Required: false, Description: "A channel's legacy YouTube user name."),
                    Max()),
            },
            new ConnectorOperation(GetChannelOperation, "Get a channel", "One channel - n8n's channel Get.") { Parameters = Schema(ChannelField) },
            new ConnectorOperation(UpdateChannelOperation, "Update a channel", "Changes a channel's description, keywords, country, language or trailer - the rest of its branding kept; n8n's channel Update.")
            {
                Parameters = Schema(
                    ChannelField,
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "What the channel is."),
                    new ConnectorConfigField("keywords", "Keywords", ConnectorConfigFieldType.Text, Required: false, Description: "Space-separated, a phrase in quotes - café \"slow food\"."),
                    new ConnectorConfigField("country", "Country", ConnectorConfigFieldType.Text, Required: false, Description: "Two letters - SE."),
                    DefaultLanguage(),
                    new ConnectorConfigField("trailer", "Trailer", ConnectorConfigFieldType.Text, Required: false, Description: "The video shown to those not subscribed - its id.")),
            },
            new ConnectorOperation(PlaylistsOperation, "Get playlists", "The account's own playlists, a channel's or by id - n8n's playlist Get Many.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("channel", "Channel", ConnectorConfigFieldType.Text, Required: false, Description: "A channel's - its id."),
                    new ConnectorConfigField("id", "Ids", ConnectorConfigFieldType.Text, Required: false, Description: "Playlists' ids, comma-separated."),
                    Max()),
            },
            new ConnectorOperation(GetPlaylistOperation, "Get a playlist", "One playlist - n8n's playlist Get.") { Parameters = Schema(PlaylistField) },
            new ConnectorOperation(CreatePlaylistOperation, "Create a playlist", "Makes a playlist - n8n's playlist Create.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: true, Description: "Weekend recipes."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "What it holds."),
                    Privacy(), Tags(), DefaultLanguage()),
            },
            new ConnectorOperation(UpdatePlaylistOperation, "Update a playlist", "Changes a playlist - what is not given kept; n8n's playlist Update.")
            {
                Parameters = Schema(
                    PlaylistField,
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: false, Description: "Weekend recipes."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "What it holds."),
                    Privacy(), Tags(), DefaultLanguage()),
            },
            new ConnectorOperation(DeletePlaylistOperation, "Delete a playlist", "Deletes a playlist - n8n's playlist Delete.") { Parameters = Schema(PlaylistField) },
            new ConnectorOperation(PlaylistItemsOperation, "Get playlist items", "A playlist's videos - n8n's playlist item Get Many.")
            {
                Parameters = Schema(PlaylistField, new ConnectorConfigField("video", "Video", ConnectorConfigFieldType.Text, Required: false, Description: "Only this video's place in it - its id."), Max()),
            },
            new ConnectorOperation(GetPlaylistItemOperation, "Get a playlist item", "One playlist item - n8n's playlist item Get.") { Parameters = Schema(ItemField) },
            new ConnectorOperation(AddPlaylistItemOperation, "Add to a playlist", "Puts a video in a playlist - n8n's playlist item Add.")
            {
                Parameters = Schema(
                    PlaylistField, VideoField,
                    new ConnectorConfigField("position", "Position", ConnectorConfigFieldType.Integer, Required: false, Description: "Its place, the first 0 - the end when left out."),
                    new ConnectorConfigField("note", "Note", ConnectorConfigFieldType.Text, Required: false, Description: "A note beside it.")),
            },
            new ConnectorOperation(DeletePlaylistItemOperation, "Remove from a playlist", "Takes a video out of a playlist - n8n's playlist item Delete.") { Parameters = Schema(ItemField) },
            new ConnectorOperation(VideosOperation, "Get videos", "The account's own videos, or a search - n8n's video Get Many.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("query", "Words", ConnectorConfigFieldType.Text, Required: false, Description: "What to search for."),
                    new ConnectorConfigField("mine", "Mine", ConnectorConfigFieldType.Text, Required: false, Description: "true for the account's own videos - so when no words, channel, category or region are given."),
                    new ConnectorConfigField("channel", "Channel", ConnectorConfigFieldType.Text, Required: false, Description: "A channel's - its id."),
                    new ConnectorConfigField("category", "Category", ConnectorConfigFieldType.Text, Required: false, Description: "A video category's id - 26."),
                    new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: false, Description: "Videos that can be watched in it - SE."),
                    new ConnectorConfigField("published_after", "Published after", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00+02:00."),
                    new ConnectorConfigField("published_before", "Published before", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("event", "Live", ConnectorConfigFieldType.Text, Required: false, Description: "completed, live or upcoming broadcasts only."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "date, rating, relevance, title or viewCount."),
                    new ConnectorConfigField("safe_search", "Safe search", ConnectorConfigFieldType.Text, Required: false, Description: "moderate, none or strict."),
                    Max()),
            },
            new ConnectorOperation(GetVideoOperation, "Get a video", "One video with its statistics - n8n's video Get.") { Parameters = Schema(VideoField) },
            new ConnectorOperation(UpdateVideoOperation, "Update a video", "Changes a video - what is not given kept; n8n's video Update.")
            {
                Parameters = Schema(
                    VideoField,
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: false, Description: "Up to 100 characters."),
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Up to 5,000 characters."),
                    Tags(),
                    new ConnectorConfigField("category", "Category", ConnectorConfigFieldType.Text, Required: false, Description: "A video category's id - 26."),
                    Privacy(),
                    new ConnectorConfigField("publish_at", "Publish at", ConnectorConfigFieldType.Text, Required: false, Description: "When a private video goes public - 2026-10-20T09:00:00+02:00."),
                    new ConnectorConfigField("embeddable", "Embeddable", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
                    new ConnectorConfigField("public_stats", "Public statistics", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
                    new ConnectorConfigField("made_for_kids", "Made for kids", ConnectorConfigFieldType.Text, Required: false, Description: "true or false."),
                    new ConnectorConfigField("license", "Licence", ConnectorConfigFieldType.Text, Required: false, Description: "youtube or creativeCommon."),
                    DefaultLanguage()),
            },
            new ConnectorOperation(DeleteVideoOperation, "Delete a video", "Deletes a video - n8n's video Delete.") { Parameters = Schema(VideoField) },
            new ConnectorOperation(RateVideoOperation, "Rate a video", "Likes or dislikes a video, or takes the rating back - n8n's video Rate.")
            {
                Parameters = Schema(VideoField, new ConnectorConfigField("rating", "Rating", ConnectorConfigFieldType.Text, Required: true, Description: "like, dislike or none.")),
            },
            new ConnectorOperation(VideoCategoriesOperation, "Get video categories", "The video categories of a region - n8n's video category Get Many.")
            {
                Parameters = Schema(new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: false, Description: "Two letters - SE.") { Default = "US" }, Max()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var found = await SendAsync(client, token, HttpMethod.Get, api + "/channels?part=snippet&mine=true", null, cancellationToken);
                var channels = (found?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                return ConnectionTestResult.Ok(channels.Count == 0 ? "Signed in - the account has no channel." : $"Signed in to the channel {Text(channels[0]["snippet"]?["title"]) ?? Text(channels[0]["id"])}{(channels.Count > 1 ? $" and {channels.Count - 1} more" : "")}.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? VideosOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"YouTube has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var channel = operation is GetChannelOperation or UpdateChannelOperation ? Id(Values.Get(p, "channel"), "channel", ChannelId, "UC and 22 more")
                : operation is PlaylistsOperation or VideosOperation && Values.Get(p, "channel") is { } some ? Id(some, "channel", ChannelId, "UC and 22 more") : null;
            var playlist = operation is GetPlaylistOperation or UpdatePlaylistOperation or DeletePlaylistOperation or PlaylistItemsOperation or AddPlaylistItemOperation ? Id(Values.Get(p, "playlist"), "playlist", PlaylistId, "PL...") : null;
            var item = operation is GetPlaylistItemOperation or DeletePlaylistItemOperation ? Id(Values.Get(p, "item"), "playlist item", ItemId, "from Get playlist items") : null;
            var video = operation is GetVideoOperation or UpdateVideoOperation or DeleteVideoOperation or RateVideoOperation or AddPlaylistItemOperation ? Id(Values.Get(p, "video"), "video", VideoId, "the 11 characters after watch?v=")
                : operation == PlaylistItemsOperation && Values.Get(p, "video") is { } only ? Id(only, "video", VideoId, "the 11 characters after watch?v=") : null;
            var query = new List<string>();
            JsonObject? body = null;
            Action<JsonObject>? change = null;
            switch (operation)
            {
                case ChannelsOperation:
                    {
                        var ways = new[] { ("id", Values.Get(p, "id")), ("forHandle", Values.Get(p, "handle")), ("forUsername", Values.Get(p, "username")) }.Where(way => way.Item2 is not null).ToList();
                        if (ways.Count > 1)
                        {
                            throw new InvalidOperationException("Give one way to find channels - their ids, a handle or a user name.");
                        }

                        query.Add(ways.Count == 0 ? "mine=true" : ways[0].Item1 switch
                        {
                            "id" => "id=" + Ids(ways[0].Item2!, "channel", ChannelId, "UC and 22 more"),
                            "forHandle" => "forHandle=" + Uri.EscapeDataString(Handle.IsMatch(ways[0].Item2!) ? (ways[0].Item2!.StartsWith('@') ? ways[0].Item2! : "@" + ways[0].Item2) : throw new InvalidOperationException($"'{ways[0].Item2}' is not a handle - @cafeshop.")),
                            _ => "forUsername=" + Uri.EscapeDataString(OneLine(ways[0].Item2!, "user name")),
                        });
                        break;
                    }

                case UpdateChannelOperation:
                    {
                        var given = new JsonObject();
                        if (Values.Get(p, "description") is { } description)
                        {
                            given["description"] = description.Length <= 1000 ? description : throw new InvalidOperationException("The description is longer than 1,000 characters.");
                        }

                        if (Values.Get(p, "keywords") is { } keywords)
                        {
                            given["keywords"] = OneLine(keywords, "keywords");
                        }

                        if (Values.Get(p, "country") is { } country)
                        {
                            given["country"] = Region.IsMatch(country) ? country.ToUpperInvariant() : throw new InvalidOperationException($"'{country}' is not a country - two letters, SE.");
                        }

                        if (Values.Get(p, "default_language") is { } language)
                        {
                            given["defaultLanguage"] = LanguageOf(language);
                        }

                        if (Values.Get(p, "trailer") is { } trailer)
                        {
                            given["unsubscribedTrailer"] = Id(trailer, "video", VideoId, "the 11 characters after watch?v=");
                        }

                        if (given.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a description, keywords, a country, a language or a trailer.");
                        }

                        change = found =>
                        {
                            var branding = found["brandingSettings"] as JsonObject ?? new JsonObject();
                            var kept = branding["channel"] as JsonObject ?? new JsonObject();
                            foreach (var (name, value) in given)
                            {
                                kept[name] = value?.DeepClone();
                            }

                            body = new JsonObject { ["id"] = channel, ["brandingSettings"] = new JsonObject { ["channel"] = kept.DeepClone() } };
                            if (branding["image"] is JsonObject image)
                            {
                                body["brandingSettings"]!["image"] = image.DeepClone();
                            }
                        };
                        break;
                    }

                case PlaylistsOperation:
                    {
                        var ids = Values.Get(p, "id");
                        if (ids is not null && channel is not null)
                        {
                            throw new InvalidOperationException("Give one way to find playlists - a channel or their ids.");
                        }

                        query.Add(ids is not null ? "id=" + Ids(ids, "playlist", PlaylistId, "PL...") : channel is not null ? "channelId=" + channel : "mine=true");
                        break;
                    }

                case CreatePlaylistOperation:
                case UpdatePlaylistOperation:
                    {
                        var snippet = new JsonObject();
                        var status = new JsonObject();
                        if (Values.Get(p, "title") is { } title)
                        {
                            snippet["title"] = Title(title, 150);
                        }
                        else if (operation == CreatePlaylistOperation)
                        {
                            throw new InvalidOperationException("Give the playlist's title.");
                        }

                        if (Values.Get(p, "description") is { } description)
                        {
                            snippet["description"] = Description(description);
                        }

                        if (Values.Get(p, "tags") is { } tags)
                        {
                            snippet["tags"] = TagsOf(tags);
                        }

                        if (Values.Get(p, "default_language") is { } language)
                        {
                            snippet["defaultLanguage"] = LanguageOf(language);
                        }

                        if (Values.Get(p, "privacy") is { } privacy)
                        {
                            status["privacyStatus"] = Choice(privacy, "a privacy", Privacies);
                        }

                        if (operation == CreatePlaylistOperation)
                        {
                            body = new JsonObject { ["snippet"] = snippet };
                            if (status.Count > 0)
                            {
                                body["status"] = status;
                            }

                            break;
                        }

                        if (snippet.Count + status.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a title, a description, tags, a language or a privacy.");
                        }

                        change = found => body = Merged(found, playlist!, ("snippet", snippet, new[] { "title", "description", "tags", "defaultLanguage" }), ("status", status, new[] { "privacyStatus" }));
                        break;
                    }

                case PlaylistItemsOperation:
                    query.Add("playlistId=" + playlist);
                    if (video is not null)
                    {
                        query.Add("videoId=" + video);
                    }

                    break;
                case AddPlaylistItemOperation:
                    {
                        var snippet = new JsonObject { ["playlistId"] = playlist, ["resourceId"] = new JsonObject { ["kind"] = "youtube#video", ["videoId"] = video } };
                        if (Values.Get(p, "position") is { } position)
                        {
                            snippet["position"] = int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var place) ? place : throw new InvalidOperationException($"'{position}' is not a position - 0 or more.");
                        }

                        body = new JsonObject { ["snippet"] = snippet };
                        if (Values.Get(p, "note") is { } note)
                        {
                            body["contentDetails"] = new JsonObject { ["note"] = note.Length <= 280 ? OneLine(note, "note") : throw new InvalidOperationException("The note is longer than 280 characters.") };
                        }

                        break;
                    }

                case VideosOperation:
                    {
                        query.Add("type=video");
                        if (Values.Get(p, "query") is { } words)
                        {
                            query.Add("q=" + Uri.EscapeDataString(OneLine(words, "words")));
                        }

                        if (channel is not null)
                        {
                            query.Add("channelId=" + channel);
                        }

                        if (Values.Get(p, "category") is { } category)
                        {
                            query.Add("videoCategoryId=" + (CategoryId.IsMatch(category) ? category : throw new InvalidOperationException($"'{category}' is not a video category's id - 26.")));
                        }

                        if (Values.Get(p, "region") is { } region)
                        {
                            query.Add("regionCode=" + (Region.IsMatch(region) ? region.ToUpperInvariant() : throw new InvalidOperationException($"'{region}' is not a region - two letters, SE.")));
                        }

                        foreach (var (name, key) in new[] { ("published_after", "publishedAfter"), ("published_before", "publishedBefore") })
                        {
                            if (Values.Get(p, name) is { } moment)
                            {
                                query.Add(key + "=" + Uri.EscapeDataString(Time(moment, name.Replace('_', ' '))));
                            }
                        }

                        if (Values.Get(p, "event") is { } live)
                        {
                            query.Add("eventType=" + Choice(live, "a live event", "completed", "live", "upcoming"));
                        }

                        if (Values.Get(p, "order") is { } order)
                        {
                            query.Add("order=" + Choice(order, "an order", "date", "rating", "relevance", "title", "viewCount"));
                        }

                        if (Values.Get(p, "safe_search") is { } safe)
                        {
                            query.Add("safeSearch=" + Choice(safe, "a safe search", "moderate", "none", "strict"));
                        }

                        var mine = Values.Get(p, "mine") is { } own ? Flag(own, "mine")
                            : Values.Get(p, "query") is null && channel is null && Values.Get(p, "category") is null && Values.Get(p, "region") is null;
                        if (mine)
                        {
                            query.Insert(1, "forMine=true");
                        }

                        break;
                    }

                case UpdateVideoOperation:
                    {
                        var snippet = new JsonObject();
                        var status = new JsonObject();
                        if (Values.Get(p, "title") is { } title)
                        {
                            snippet["title"] = Title(title, 100);
                        }

                        if (Values.Get(p, "description") is { } description)
                        {
                            snippet["description"] = Description(description);
                        }

                        if (Values.Get(p, "tags") is { } tags)
                        {
                            snippet["tags"] = TagsOf(tags);
                        }

                        if (Values.Get(p, "category") is { } category)
                        {
                            snippet["categoryId"] = CategoryId.IsMatch(category) ? category : throw new InvalidOperationException($"'{category}' is not a video category's id - 26.");
                        }

                        if (Values.Get(p, "default_language") is { } language)
                        {
                            snippet["defaultLanguage"] = LanguageOf(language);
                        }

                        if (Values.Get(p, "privacy") is { } privacy)
                        {
                            status["privacyStatus"] = Choice(privacy, "a privacy", Privacies);
                        }

                        if (Values.Get(p, "publish_at") is { } publishAt)
                        {
                            status["publishAt"] = Time(publishAt, "publish at");
                        }

                        foreach (var (name, key) in new[] { ("embeddable", "embeddable"), ("public_stats", "publicStatsViewable"), ("made_for_kids", "selfDeclaredMadeForKids") })
                        {
                            if (Values.Get(p, name) is { } flag)
                            {
                                status[key] = Flag(flag, name.Replace('_', ' '));
                            }
                        }

                        if (Values.Get(p, "license") is { } license)
                        {
                            status["license"] = Choice(license, "a licence", "youtube", "creativeCommon");
                        }

                        if (snippet.Count + status.Count == 0)
                        {
                            throw new InvalidOperationException("Give something to change - a title, a description, tags, a category, a language, a privacy, a time to publish, whether embeddable, its statistics public or made for kids, or a licence.");
                        }

                        change = found => body = Merged(found, video!, ("snippet", snippet, new[] { "title", "description", "tags", "categoryId", "defaultLanguage" }), ("status", status, WritableStatus));
                        break;
                    }

                case RateVideoOperation:
                    query.Add("rating=" + Choice(Values.Get(p, "rating") ?? throw new InvalidOperationException("Give the rating - like, dislike or none."), "a rating", "like", "dislike", "none"));
                    break;
                case VideoCategoriesOperation:
                    {
                        var region = Values.Get(p, "region") ?? "US";
                        query.Add("regionCode=" + (Region.IsMatch(region) ? region.ToUpperInvariant() : throw new InvalidOperationException($"'{region}' is not a region - two letters, SE.")));
                        break;
                    }
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation is ChannelsOperation or PlaylistsOperation or PlaylistItemsOperation or VideosOperation)
            {
                var (path, parts, record) = operation switch
                {
                    ChannelsOperation => ("/channels", ChannelParts, (Func<JsonObject, ConnectorRecord>)ChannelRecord),
                    PlaylistsOperation => ("/playlists", PlaylistParts, PlaylistRecord),
                    PlaylistItemsOperation => ("/playlistItems", ItemParts, ItemRecord),
                    _ => ("/search", "snippet", SearchRecord),
                };
                var read = 0;
                string? next = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var page = await SendAsync(client, token, HttpMethod.Get, $"{api}{path}?part={parts}&{string.Join("&", query)}&maxResults={size.ToString(CultureInfo.InvariantCulture)}{(next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next))}", null, cancellationToken);
                    foreach (var one in (page?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return record(one);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = Text(page?["nextPageToken"]) is { Length: > 0 } more ? more : null;
                }
                while (next is not null);

                yield break;
            }

            switch (operation)
            {
                case GetChannelOperation:
                    yield return ChannelRecord(await OneAsync(client, token, $"{api}/channels?part={ChannelParts}&id={channel}", "channel", channel!, cancellationToken));
                    yield break;
                case UpdateChannelOperation:
                    change!(await OneAsync(client, token, $"{api}/channels?part=brandingSettings&id={channel}", "channel", channel!, cancellationToken));
                    yield return ChannelRecord(await SendAsync(client, token, HttpMethod.Put, $"{api}/channels?part=brandingSettings", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case GetPlaylistOperation:
                    yield return PlaylistRecord(await OneAsync(client, token, $"{api}/playlists?part={PlaylistParts}&id={playlist}", "playlist", playlist!, cancellationToken));
                    yield break;
                case CreatePlaylistOperation:
                    yield return PlaylistRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/playlists?part={(body!["status"] is null ? "snippet" : "snippet,status")}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case UpdatePlaylistOperation:
                    change!(await OneAsync(client, token, $"{api}/playlists?part=snippet,status&id={playlist}", "playlist", playlist!, cancellationToken));
                    yield return PlaylistRecord(await SendAsync(client, token, HttpMethod.Put, $"{api}/playlists?part=snippet,status", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case GetPlaylistItemOperation:
                    yield return ItemRecord(await OneAsync(client, token, $"{api}/playlistItems?part={ItemParts}&id={Uri.EscapeDataString(item!)}", "playlist item", item!, cancellationToken));
                    yield break;
                case AddPlaylistItemOperation:
                    yield return ItemRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/playlistItems?part={(body!["contentDetails"] is null ? "snippet" : "snippet,contentDetails")}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case GetVideoOperation:
                    yield return VideoRecord(await OneAsync(client, token, $"{api}/videos?part={VideoParts}&id={video}", "video", video!, cancellationToken));
                    yield break;
                case UpdateVideoOperation:
                    change!(await OneAsync(client, token, $"{api}/videos?part=snippet,status&id={video}", "video", video!, cancellationToken));
                    yield return VideoRecord(await SendAsync(client, token, HttpMethod.Put, $"{api}/videos?part=snippet,status", body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case RateVideoOperation:
                    await SendAsync(client, token, HttpMethod.Post, $"{api}/videos/rate?id={video}&{query[0]}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["video"] = video, ["rating"] = query[0]["rating=".Length..], ["done"] = true });
                    yield break;
                case VideoCategoriesOperation:
                    {
                        var read = 0;
                        foreach (var one in ((await SendAsync(client, token, HttpMethod.Get, $"{api}/videoCategories?part=snippet&{query[0]}", null, cancellationToken))?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["id"] = Text(one["id"]),
                                ["title"] = Text(one["snippet"]?["title"]),
                                ["assignable"] = Bool(one["snippet"]?["assignable"]),
                            });
                            if (++read >= max)
                            {
                                yield break;
                            }
                        }

                        yield break;
                    }

                default:
                    {
                        var (path, id, what) = operation switch
                        {
                            DeletePlaylistOperation => ("/playlists", playlist!, "playlist"),
                            DeletePlaylistItemOperation => ("/playlistItems", item!, "item"),
                            _ => ("/videos", video!, "video"),
                        };
                        await SendAsync(client, token, HttpMethod.Delete, $"{api}{path}?id={Uri.EscapeDataString(id)}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [what] = id, ["done"] = true });
                        yield break;
                    }
            }
        }

        // An update's body: what YouTube holds of each part, what was given laid over it - only a part's writable values sent back.
        private static JsonObject Merged(JsonObject found, string id, params (string Part, JsonObject Given, string[] Writable)[] parts)
        {
            var body = new JsonObject { ["id"] = id };
            foreach (var (part, given, writable) in parts)
            {
                var held = found[part] as JsonObject ?? new JsonObject();
                var sent = new JsonObject();
                foreach (var name in writable)
                {
                    if (given[name] is { } value)
                    {
                        sent[name] = value.DeepClone();
                    }
                    else if (held[name] is { } kept)
                    {
                        sent[name] = kept.DeepClone();
                    }
                }

                body[part] = sent;
            }

            return body;
        }

        private static ConnectorRecord ChannelRecord(JsonObject channel)
        {
            var snippet = channel["snippet"] as JsonObject ?? new JsonObject();
            var statistics = channel["statistics"] as JsonObject ?? new JsonObject();
            var branding = channel["brandingSettings"]?["channel"] as JsonObject ?? new JsonObject();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(channel["id"]),
                ["title"] = Text(snippet["title"]),
                ["description"] = Text(snippet["description"]) ?? Text(branding["description"]),
                ["handle"] = Text(snippet["customUrl"]),
                ["published_at"] = Text(snippet["publishedAt"]),
                ["country"] = Text(snippet["country"]) ?? Text(branding["country"]),
                ["default_language"] = Text(snippet["defaultLanguage"]) ?? Text(branding["defaultLanguage"]),
                ["keywords"] = Text(branding["keywords"]),
                ["trailer"] = Text(branding["unsubscribedTrailer"]),
                ["subscribers"] = Count(statistics["subscriberCount"]),
                ["views"] = Count(statistics["viewCount"]),
                ["videos"] = Count(statistics["videoCount"]),
                ["uploads"] = Text(channel["contentDetails"]?["relatedPlaylists"]?["uploads"]),
                ["privacy"] = Text(channel["status"]?["privacyStatus"]),
                ["made_for_kids"] = Bool(channel["status"]?["madeForKids"]),
                ["thumbnail"] = Thumbnail(snippet),
            });
        }

        private static ConnectorRecord PlaylistRecord(JsonObject playlist)
        {
            var snippet = playlist["snippet"] as JsonObject ?? new JsonObject();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(playlist["id"]),
                ["title"] = Text(snippet["title"]),
                ["description"] = Text(snippet["description"]),
                ["channel_id"] = Text(snippet["channelId"]),
                ["channel_title"] = Text(snippet["channelTitle"]),
                ["published_at"] = Text(snippet["publishedAt"]),
                ["tags"] = TagsText(snippet),
                ["default_language"] = Text(snippet["defaultLanguage"]),
                ["privacy"] = Text(playlist["status"]?["privacyStatus"]),
                ["videos"] = Count(playlist["contentDetails"]?["itemCount"]),
                ["thumbnail"] = Thumbnail(snippet),
            });
        }

        private static ConnectorRecord ItemRecord(JsonObject item)
        {
            var snippet = item["snippet"] as JsonObject ?? new JsonObject();
            var details = item["contentDetails"] as JsonObject ?? new JsonObject();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(item["id"]),
                ["playlist_id"] = Text(snippet["playlistId"]),
                ["video_id"] = Text(details["videoId"]) ?? Text(snippet["resourceId"]?["videoId"]),
                ["title"] = Text(snippet["title"]),
                ["description"] = Text(snippet["description"]),
                ["position"] = Count(snippet["position"]),
                ["added_at"] = Text(snippet["publishedAt"]),
                ["video_published_at"] = Text(details["videoPublishedAt"]),
                ["note"] = Text(details["note"]),
                ["video_channel"] = Text(snippet["videoOwnerChannelTitle"]),
                ["privacy"] = Text(item["status"]?["privacyStatus"]),
                ["thumbnail"] = Thumbnail(snippet),
            });
        }

        private static ConnectorRecord SearchRecord(JsonObject found)
        {
            var snippet = found["snippet"] as JsonObject ?? new JsonObject();
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(found["id"]?["videoId"]),
                ["title"] = Text(snippet["title"]),
                ["description"] = Text(snippet["description"]),
                ["channel_id"] = Text(snippet["channelId"]),
                ["channel_title"] = Text(snippet["channelTitle"]),
                ["published_at"] = Text(snippet["publishedAt"]),
                ["live"] = Text(snippet["liveBroadcastContent"]),
                ["thumbnail"] = Thumbnail(snippet),
            });
        }

        private static ConnectorRecord VideoRecord(JsonObject video)
        {
            var snippet = video["snippet"] as JsonObject ?? new JsonObject();
            var details = video["contentDetails"] as JsonObject ?? new JsonObject();
            var statistics = video["statistics"] as JsonObject ?? new JsonObject();
            var status = video["status"] as JsonObject ?? new JsonObject();
            var duration = Text(details["duration"]);
            long? seconds = null;
            if (duration is not null)
            {
                try
                {
                    seconds = (long)XmlConvert.ToTimeSpan(duration).TotalSeconds;
                }
                catch (FormatException)
                {
                }
            }

            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(video["id"]),
                ["title"] = Text(snippet["title"]),
                ["description"] = Text(snippet["description"]),
                ["channel_id"] = Text(snippet["channelId"]),
                ["channel_title"] = Text(snippet["channelTitle"]),
                ["published_at"] = Text(snippet["publishedAt"]),
                ["category_id"] = Text(snippet["categoryId"]),
                ["tags"] = TagsText(snippet),
                ["default_language"] = Text(snippet["defaultLanguage"]),
                ["duration"] = duration,
                ["seconds"] = seconds,
                ["definition"] = Text(details["definition"]),
                ["captions"] = Text(details["caption"]) is { } caption ? caption == "true" : null,
                ["views"] = Count(statistics["viewCount"]),
                ["likes"] = Count(statistics["likeCount"]),
                ["comments"] = Count(statistics["commentCount"]),
                ["privacy"] = Text(status["privacyStatus"]),
                ["publish_at"] = Text(status["publishAt"]),
                ["embeddable"] = Bool(status["embeddable"]),
                ["license"] = Text(status["license"]),
                ["made_for_kids"] = Bool(status["madeForKids"]),
                ["thumbnail"] = Thumbnail(snippet),
            });
        }

        // The largest thumbnail YouTube gives.
        private static string? Thumbnail(JsonObject snippet) =>
            new[] { "maxres", "standard", "high", "medium", "default" }.Select(size => Text(snippet["thumbnails"]?[size]?["url"])).FirstOrDefault(url => url is not null);

        private static string? TagsText(JsonObject snippet) => snippet["tags"] is JsonArray tags ? string.Join(", ", tags.Select(Text).Where(tag => tag is not null)) : null;

        // One of a list YouTube answers for an id - none at all a failure naming it, the way a missing one reads.
        private static async Task<JsonObject> OneAsync(HttpClient client, string token, string url, string what, string id, CancellationToken cancellationToken) =>
            ((await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken))?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault()
                ?? throw new InvalidOperationException($"No {what} has the id {id}.");

        private static string Id(string? given, string what, Regex shape, string example) =>
            given is null ? throw new InvalidOperationException($"Give the {what} - its id.")
                : shape.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s id - {example}.");

        private static string Ids(string given, string what, Regex shape, string example)
        {
            var ids = given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count is 0 or > PageSize)
            {
                throw new InvalidOperationException($"Give 1 to {PageSize} {what}s' ids - {ids.Count} given.");
            }

            return string.Join(",", ids.Select(id => Id(id, what, shape, example)));
        }

        private static string Title(string given, int most) =>
            given.Contains('<', StringComparison.Ordinal) || given.Contains('>', StringComparison.Ordinal) ? throw new InvalidOperationException("The title has < or > - YouTube refuses both.")
                : OneLine(given, "title") is { } title && title.Length <= most ? title : throw new InvalidOperationException($"The title is longer than {most} characters.");

        private static string Description(string given) =>
            given.Contains('<', StringComparison.Ordinal) || given.Contains('>', StringComparison.Ordinal) ? throw new InvalidOperationException("The description has < or > - YouTube refuses both.")
                : given.Length <= 5000 ? given : throw new InvalidOperationException("The description is longer than 5,000 characters.");

        private static JsonArray TagsOf(string given) =>
            new(given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Select(tag => (JsonNode)OneLine(tag, "tag")).ToArray());

        private static string LanguageOf(string given) =>
            Language.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a language - sv, or en-GB.");

        private static string Time(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-01, or 2026-10-01T09:00:00+02:00.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        // YouTube's counts are strings of digits; a number here.
        private static long? Count(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : null;

        private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // YouTube's answer as JSON (an empty answer - its 204 - nothing), the token as a bearer, a throttled call waiting as Google
        // says; a failure thrown with its status, YouTube's reason (Google's status word when none) and its message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"YouTube answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var reason = Text(json?["error"]?["errors"]?[0]?["reason"]) ?? Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(reason is null ? "" : " " + reason)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
