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
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.GoogleBusinessProfile
{
    /// <summary>
    /// Google Business Profile (n8n's Google Business Profile node: a location's posts and its reviews) over Google's
    /// Business Profile APIs - the My Business API v4 (<c>https://mybusiness.googleapis.com/v4</c>) for posts and reviews, and
    /// its Account Management and Business Information APIs for the accounts and locations to name them by - as a person signed
    /// in: an OAuth client's refresh token granted business.manage, exchanged at Google's token endpoint through the shared
    /// <see cref="OAuth2TokenProvider"/>. A test lists the accounts the person manages.
    /// </summary>
    /// <remarks>
    /// <c>accounts</c> and <c>locations</c> (an account's, by their names and titles - n8n's lists); <c>posts</c>, <c>post</c>,
    /// <c>post_create</c> (a standard post, an event with its title and times, an offer with its title, days, coupon, link and
    /// terms, or an alert - a call to action with its link beside), <c>post_update</c> (only what is given, Google's update mask
    /// made from it as n8n's node does) and <c>post_delete</c>; <c>reviews</c> (each with its stars as a number beside Google's
    /// word for them), <c>review</c>, <c>review_reply</c> and <c>review_reply_delete</c>. Every account, location, post, review,
    /// choice, time and length held to Google's shape before a token is asked for; lists page by page by Google's page token up to
    /// <c>max</c>; Google's status and message on a failure, a 401 or 403 pointing at the token's scope.
    /// </remarks>
    public sealed class GoogleBusinessProfileConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://mybusiness.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const int DefaultMax = 100;

        private const string AccountsApi = "https://mybusinessaccountmanagement.googleapis.com";

        private const string LocationsApi = "https://mybusinessbusinessinformation.googleapis.com";

        private const int MostSummary = 1500; // Google's most for a post's text

        private const int MostReply = 4096; // and for a reply's

        private static readonly string[] Known =
        {
            "accounts", "locations", "posts", "post", "post_create", "post_update", "post_delete", "reviews", "review", "review_reply", "review_reply_delete",
        };

        private static readonly string[] PostTypes = { "STANDARD", "EVENT", "OFFER", "ALERT" };

        private static readonly string[] Actions = { "BOOK", "CALL", "LEARN_MORE", "ORDER", "SHOP", "SIGN_UP" };

        private static readonly string[] Stars = { "ONE", "TWO", "THREE", "FOUR", "FIVE" };

        private static readonly Regex Account = new("^(?:accounts/)?([0-9]{1,30})$", RegexOptions.CultureInvariant);

        private static readonly Regex Location = new("^(?:(?:accounts/[0-9]{1,30}/)?locations/)?([0-9]{1,30})$", RegexOptions.CultureInvariant);

        private static readonly Regex Post = new("^(?:accounts/[0-9]{1,30}/locations/[0-9]{1,30}/localPosts/)?([0-9]{1,30})$", RegexOptions.CultureInvariant);

        private static readonly Regex Review = new("^(?:accounts/[0-9]{1,30}/locations/[0-9]{1,30}/reviews/)?([A-Za-z0-9_-]{1,200})$", RegexOptions.CultureInvariant);

        private static readonly Regex Owner = new("^accounts/([0-9]{1,30})/locations/([0-9]{1,30})/(?:localPosts|reviews)/", RegexOptions.CultureInvariant);

        private static readonly Regex Language =new("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})?$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlebusinessprofile";

        public string DisplayName => "Google Business Profile";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted business.manage)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.google.com/business/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/my-business/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("account", "Account", ConnectorConfigFieldType.Text, Required: false, Description: "The account a step names none of - accounts/0123456789."),
            new ConnectorConfigField("location", "Location", ConnectorConfigFieldType.Text, Required: false, Description: "The location a step names none of - locations/0123456789."),
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField AccountField = Field("account", "Account", "accounts/0123456789. Empty: the connection's.");

        private static readonly ConnectorConfigField LocationField = Field("location", "Location", "locations/0123456789. Empty: the connection's.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField PostField = Field("post", "Post", "Its name - accounts/1/locations/2/localPosts/3 - or its id.", required: true);

        private static readonly ConnectorConfigField ReviewField = Field("review", "Review", "Its name - accounts/1/locations/2/reviews/AbC_1 - or its id.", required: true);

        private static readonly ConnectorConfigField[] PostFields =
        {
            Field("summary", "Text", "What it says - at most 1,500 characters."),
            Field("title", "Title", "An event's or an offer's title."),
            Field("start", "Starts", "An event's start - 2026-11-01T18:00:00+01:00 - or an offer's first day - 2026-11-01."),
            Field("end", "Ends", "An event's end, or an offer's last day."),
            Field("call_to_action", "Call to action", "BOOK, CALL, LEARN_MORE, ORDER, SHOP or SIGN_UP."),
            Field("url", "Link", "Where the call to action leads - https://kafe.example/boka. Not for CALL."),
            Field("coupon_code", "Coupon code", "An offer's code - KASSA10."),
            Field("redeem_url", "Redeem online", "Where an offer is redeemed - https://kafe.example/erbjudande."),
            Field("terms", "Terms", "An offer's terms and conditions."),
            Field("language", "Language", "Its language - sv."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("accounts", "Get accounts", "The accounts the person manages.") { Parameters = new(new[] { MaxField }) },
            new ConnectorOperation("locations", "Get locations", "An account's locations.") { Parameters = new(new[] { AccountField, MaxField }) },
            new ConnectorOperation("posts", "Get posts", "n8n's Post Get Many.") { Parameters = new(new[] { AccountField, LocationField, MaxField }) },
            new ConnectorOperation("post", "Get a post", "n8n's Post Get.") { Parameters = new(new[] { AccountField, LocationField, PostField }) },
            new ConnectorOperation("post_create", "Create a post", "n8n's Post Create.")
            {
                Parameters = new(new[] { AccountField, LocationField, Field("type", "Type", "STANDARD (the default), EVENT, OFFER or ALERT.") }.Concat(PostFields).ToArray()),
            },
            new ConnectorOperation("post_update", "Update a post", "n8n's Post Update - only what is given.") { Parameters = new(new[] { AccountField, LocationField, PostField }.Concat(PostFields).ToArray()) },
            new ConnectorOperation("post_delete", "Delete a post", "n8n's Post Delete.") { Parameters = new(new[] { AccountField, LocationField, PostField }) },
            new ConnectorOperation("reviews", "Get reviews", "n8n's Review Get Many.") { Parameters = new(new[] { AccountField, LocationField, MaxField }) },
            new ConnectorOperation("review", "Get a review", "n8n's Review Get.") { Parameters = new(new[] { AccountField, LocationField, ReviewField }) },
            new ConnectorOperation("review_reply", "Reply to a review", "n8n's Review Reply.") { Parameters = new(new[] { AccountField, LocationField, ReviewField, Field("reply", "Reply", "What to answer - at most 4,096 characters.", required: true) }) },
            new ConnectorOperation("review_reply_delete", "Delete a review's reply", "n8n's Review Delete Reply.") { Parameters = new(new[] { AccountField, LocationField, ReviewField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var answer = await session.JsonAsync(HttpMethod.Get, session.Accounts + "/v1/accounts?pageSize=20", null, cancellationToken) ?? throw new InvalidOperationException("Google Business Profile answered with nothing - check the address.");
                var accounts = (answer?["accounts"] as JsonArray)?.Count ?? 0;
                return ConnectionTestResult.Ok($"Google Business Profile answers - {accounts} account{(accounts == 1 ? "" : "s")}{(answer?["nextPageToken"] is null ? "" : " and more")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "posts").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Business Profile has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            // A post or a review named in full carries its account and location, as n8n's node calls it by that name alone.
            var named = operation switch
            {
                "post" or "post_update" or "post_delete" => Values.Get(p, "post"),
                "review" or "review_reply" or "review_reply_delete" => Values.Get(p, "review"),
                _ => null,
            };
            var owner = named is not null && Owner.Match(named.Trim()) is { Success: true } whose ? whose : null;
            var account = operation == "accounts" ? string.Empty : "accounts/" + (owner?.Groups[1].Value ?? Id(p, context.Settings, "account", Account, "an account - accounts/0123456789"));
            var location = operation is "accounts" or "locations" ? string.Empty : account + "/locations/" + (owner?.Groups[2].Value ?? Id(p, context.Settings, "location", Location, "a location - locations/0123456789"));
            string? item = operation switch
            {
                "post" or "post_update" or "post_delete" => location + "/localPosts/" + Id(p, null, "post", Post, "a post - its name or id"),
                "review" or "review_reply" or "review_reply_delete" => location + "/reviews/" + Id(p, null, "review", Review, "a review - its name or id"),
                _ => null,
            };
            var body = new JsonObject();
            var mask = new List<string>();
            switch (operation)
            {
                case "post_create":
                    {
                        var type = (Values.Get(p, "type") ?? "STANDARD").ToUpperInvariant();
                        body["topicType"] = PostTypes.Contains(type) ? type : throw new InvalidOperationException($"'{Values.Get(p, "type")}' is not STANDARD, EVENT, OFFER or ALERT (type).");
                        PostBody(p, body, mask, type, create: true);
                        break;
                    }

                case "post_update":
                    PostBody(p, body, mask, null, create: false);
                    if (mask.Count == 0)
                    {
                        throw new InvalidOperationException("Give what to change - summary, title, start, end, call to action, link, coupon code, redeem link, terms or language.");
                    }

                    break;
                case "review_reply":
                    {
                        var reply = Values.Get(p, "reply") ?? throw new InvalidOperationException("Give the reply (reply).");
                        body["comment"] = reply.Length <= MostReply ? reply : throw new InvalidOperationException($"A reply is at most {MostReply} characters - this is {reply.Length} (reply).");
                        break;
                    }
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case "accounts" or "locations" or "posts" or "reviews":
                    {
                        var (address, list, size) = operation switch
                        {
                            "accounts" => (session.Accounts + "/v1/accounts?", "accounts", 20),
                            "locations" => (session.Locations + $"/v1/{account}/locations?readMask={Uri.EscapeDataString("name,title,storefrontAddress,websiteUri,phoneNumbers,categories")}&", "locations", 100),
                            "posts" => (session.Api + $"/v4/{location}/localPosts?", "localPosts", 100),
                            _ => (session.Api + $"/v4/{location}/reviews?", "reviews", 50),
                        };
                        var read = 0;
                        string? token = null;
                        do
                        {
                            var page = address + "pageSize=" + Math.Min(size, max - read).ToString(CultureInfo.InvariantCulture) + (token is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(token));
                            var answer = await session.JsonAsync(HttpMethod.Get, page, null, cancellationToken);
                            foreach (var found in (answer?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(found);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            token = Text(answer?["nextPageToken"]) is { Length: > 0 } next ? next : null;
                        }
                        while (token is not null);

                        break;
                    }

                case "post" or "review":
                    yield return Record(await session.JsonAsync(HttpMethod.Get, session.Api + "/v4/" + item, null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "post_create":
                    yield return Record(await session.JsonAsync(HttpMethod.Post, session.Api + $"/v4/{location}/localPosts", body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "post_update":
                    yield return Record(await session.JsonAsync(new HttpMethod("PATCH"), session.Api + "/v4/" + item + "?updateMask=" + Uri.EscapeDataString(string.Join(",", mask)), body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;
                case "review_reply":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Put, session.Api + "/v4/" + item + "/reply", body, cancellationToken) as JsonObject ?? new JsonObject();
                        var fields = Record(answer).Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        fields["review"] = item;
                        yield return new ConnectorRecord(fields);
                        break;
                    }

                default: // post_delete, review_reply_delete
                    await session.JsonAsync(HttpMethod.Delete, session.Api + "/v4/" + item + (operation == "review_reply_delete" ? "/reply" : string.Empty), null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [operation == "post_delete" ? "post" : "review"] = item, ["deleted"] = true });
                    break;
            }
        }

        // A post's fields as Google takes them, each given one named in the update mask as n8n's node names it.
        private static void PostBody(IReadOnlyDictionary<string, string> p, JsonObject body, List<string> mask, string? type, bool create)
        {
            if (Values.Get(p, "summary") is { } summary)
            {
                body["summary"] = summary.Length <= MostSummary ? summary : throw new InvalidOperationException($"A post's text is at most {MostSummary} characters - this is {summary.Length} (summary).");
                mask.Add("summary");
            }
            else if (create && type is not "EVENT" and not "OFFER")
            {
                throw new InvalidOperationException("Give the post's text (summary).");
            }

            if (Values.Get(p, "language") is { } language)
            {
                body["languageCode"] = Language.IsMatch(language) ? language : throw new InvalidOperationException($"'{language}' is not a language's code - sv, en-GB (language).");
                mask.Add("languageCode");
            }

            var offer = type == "OFFER";
            if (Values.Get(p, "title") is { } title)
            {
                body["event"] = new JsonObject { ["title"] = title };
                mask.Add("event.title");
            }
            else if (create && type is "EVENT" or "OFFER")
            {
                throw new InvalidOperationException($"Give the {(offer ? "offer" : "event")}'s title (title).");
            }

            foreach (var (name, prefix) in new[] { ("start", "start"), ("end", "end") })
            {
                if (Values.Get(p, name) is { } given)
                {
                    var (date, time) = When(given, name, dayOnly: offer);
                    var schedule = (body["event"] ??= new JsonObject())["schedule"] as JsonObject;
                    if (schedule is null)
                    {
                        schedule = new JsonObject();
                        body["event"]!["schedule"] = schedule;
                    }

                    schedule[prefix + "Date"] = date;
                    mask.Add($"event.schedule.{prefix}Date");
                    if (time is not null)
                    {
                        schedule[prefix + "Time"] = time;
                        mask.Add($"event.schedule.{prefix}Time");
                    }
                }
                else if (create && type is "EVENT" or "OFFER")
                {
                    throw new InvalidOperationException($"Give when the {(offer ? "offer" : "event")} {(name == "start" ? "starts" : "ends")} ({name}).");
                }
            }

            if (Values.Get(p, "call_to_action") is { } action)
            {
                if (offer)
                {
                    throw new InvalidOperationException("An offer has no call to action - its coupon code and redeem link stand for one (call_to_action).");
                }

                var kind = action.ToUpperInvariant();
                var cta = new JsonObject { ["actionType"] = Actions.Contains(kind) ? kind : throw new InvalidOperationException($"'{action}' is not BOOK, CALL, LEARN_MORE, ORDER, SHOP or SIGN_UP (call_to_action).") };
                mask.Add("callToAction.actionType");
                if (Values.Get(p, "url") is { } url)
                {
                    cta["url"] = kind == "CALL" ? throw new InvalidOperationException("A CALL calls the location's number - it takes no link (url).") : Link(url, "url");
                    mask.Add("callToAction.url");
                }
                else if (kind != "CALL")
                {
                    throw new InvalidOperationException($"Give where the {kind} leads - https://kafe.example (url).");
                }

                body["callToAction"] = cta;
            }
            else if (Values.Get(p, "url") is not null)
            {
                throw new InvalidOperationException("Give the call to action the link is for (call_to_action).");
            }

            foreach (var (name, key, link) in new[] { ("coupon_code", "couponCode", false), ("redeem_url", "redeemOnlineUrl", true), ("terms", "termsConditions", false) })
            {
                if (Values.Get(p, name) is { } given)
                {
                    if (create && !offer)
                    {
                        throw new InvalidOperationException($"Only an offer has a {name.Replace('_', ' ')} ({name}).");
                    }

                    var holder = body["offer"] as JsonObject ?? new JsonObject();
                    holder[key] = link ? Link(given, name) : given;
                    body["offer"] = holder;
                    mask.Add("offer." + key);
                }
            }

            if (create && type == "ALERT")
            {
                body["alertType"] = "COVID_19"; // the one alert Google has, as n8n's node sends it
            }
        }

        // A moment as Google's date and time of day, in UTC as n8n's node sends them - or, for an offer, a day alone.
        private static (JsonObject Date, JsonObject? Time) When(string given, string name, bool dayOnly)
        {
            if (DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return (new JsonObject { ["year"] = day.Year, ["month"] = day.Month, ["day"] = day.Day }, null);
            }

            if (dayOnly || !DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                throw new InvalidOperationException(dayOnly ? $"'{given}' is not a day - 2026-11-01 ({name})." : $"'{given}' is not a time - 2026-11-01T18:00:00+01:00 ({name}).");
            }

            var utc = at.UtcDateTime;
            return (new JsonObject { ["year"] = utc.Year, ["month"] = utc.Month, ["day"] = utc.Day }, new JsonObject { ["hours"] = utc.Hour, ["minutes"] = utc.Minute, ["seconds"] = utc.Second, ["nanos"] = 0 });
        }

        private static string Link(string given, string name) =>
            Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var link) && link.Scheme is "https" or "http" ? given : throw new InvalidOperationException($"'{given}' is not a web address - https://kafe.example ({name}).");

        // An id from the step, or from the connection's setting - by its name (accounts/1) or bare.
        private static string Id(IReadOnlyDictionary<string, string> p, IReadOnlyDictionary<string, string>? settings, string name, Regex shape, string what)
        {
            var given = Values.Get(p, name) ?? (settings is null ? null : Values.Get(settings, name)) ?? throw new InvalidOperationException($"Give {what} ({name}){(settings is null ? "" : " - or set the connection's")}.");
            return shape.Match(given.Trim()) is { Success: true } match ? match.Groups[1].Value : throw new InvalidOperationException($"'{given}' is not {what} ({name}).");
        }

        // An object as a record: its plain values as they are, a list or an object as JSON - a review's stars as a number beside.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = item.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                var other => (object?)other.ToJsonString(),
            }, StringComparer.Ordinal);
            if (Text(item["starRating"]) is { } rating && Array.IndexOf(Stars, rating) is >= 0 and var index)
            {
                fields["stars"] = index + 1;
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;

            private Session(HttpClient client, string token, string api, string accounts, string locations)
            {
                this.client = client;
                this.token = token;
                Api = api;
                Accounts = accounts;
                Locations = locations;
            }

            /// <summary>The My Business API's root, and the Account Management and Business Information APIs' - all the one address given, a stand-in's.</summary>
            public string Api { get; }

            public string Accounts { get; }

            public string Locations { get; }

            public static async Task<Session> OpenAsync(GoogleBusinessProfileConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/v4", StringComparison.Ordinal))
                {
                    root = root[..^"/v4".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Google Business Profile's address - its own, {DefaultEndpoint}.");
                }

                var own = root == DefaultEndpoint;
                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, root, own ? AccountsApi : root, own ? LocationsApi : root);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's JSON, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string address, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, address);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Google Business Profile answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is null or JsonObject ? json : throw new InvalidOperationException("Google Business Profile answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var error = json?["error"];
                var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                throw new InvalidOperationException($"Google Business Profile answered HTTP {status}{(Text(error?["status"]) is { } kind ? " " + kind : string.Empty)}{(message is null ? "." : ": " + message)}"
                    + (status is 401 or 403 ? " - check the refresh token, its business.manage scope and the person's access to the location." : string.Empty));
            }
        }
    }
}
