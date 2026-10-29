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

namespace Stomme.Modules.Connectors.Bitly
{
    /// <summary>
    /// Bitly (BL-229, n8n's Bitly node: a link shortened, got and updated): Bitly's API v4 with an access token as a bearer
    /// token, over the host's egress. A test names the token's user and its default group.
    /// </summary>
    /// <remarks>
    /// <c>create</c> (a long address shortened - on a domain, in a group, with a title, tags and deep links), <c>link</c>,
    /// <c>update</c> (its title, tags, archived, its long address), <c>links</c> (a group's, searched, tagged, archived or
    /// not, created in a time - page by page up to <c>max</c>), <c>clicks</c> (a link's clicks by minute, hour, day, week or
    /// month), <c>groups</c> and <c>user</c>. A link is its id, its short and long address, title, tags and dates, a list or an
    /// object as its JSON. Every link, group, address, unit and time held to its shape before anything is sent. A throttled
    /// call waits as Bitly says; a failure is thrown with its status, Bitly's word and its description.
    /// </remarks>
    public sealed class BitlyConnector : IConnector, IConnectorExtractor
    {
        public const string CreateOperation = "create";

        public const string LinkOperation = "link";

        public const string UpdateOperation = "update";

        public const string LinksOperation = "links";

        public const string ClicksOperation = "clicks";

        public const string GroupsOperation = "groups";

        public const string UserOperation = "user";

        public const string DefaultEndpoint = "https://api-ssl.bitly.com/v4";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A link: its domain and back-half - bit.ly/3xYz9 - an address it was given with or without its scheme.
        private static readonly Regex Bitlink = new("^(https?://)?(?<link>[A-Za-z0-9.-]{1,253}\\.[A-Za-z]{2,}/[A-Za-z0-9_-]{1,100})$", RegexOptions.CultureInvariant);

        private static readonly Regex GroupGuid = new("^[A-Za-z0-9]{2,40}$", RegexOptions.CultureInvariant);

        private static readonly Regex Domain = new("^[A-Za-z0-9.-]{1,253}\\.[A-Za-z]{2,}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { CreateOperation, LinkOperation, UpdateOperation, LinksOperation, ClicksOperation, GroupsOperation, UserOperation };

        private static readonly string[] Units = { "minute", "hour", "day", "week", "month" };

        public string ConnectorType => "bitly";

        public string DisplayName => "Bitly";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://bitly.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://dev.bitly.com/api-reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField LinkField = Field("link", "Link", "The short link - bit.ly/3xYz9.", required: true);

        private static readonly ConnectorConfigField TagsField = Field("tags", "Tags", "Comma-separated tags.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CreateOperation, "Shorten an address", "A long address shortened - n8n's Bitly Link Create.")
            {
                Parameters = Schema(
                    Field("long_url", "Address", "The long address - https://example.com/a/long/path.", required: true),
                    Field("domain", "Domain", "The short domain - bit.ly when left empty, or the account's own."),
                    Field("group", "Group", "The group's id - the user's default when left empty."),
                    Field("title", "Title", "Its title."),
                    TagsField,
                    Field("deeplinks", "Deep links", "A JSON list of deep links - [{\"app_id\": \"com.example\", \"app_uri_path\": \"/item/1\", \"install_url\": \"https://...\", \"install_type\": \"promote_install\"}].", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(LinkOperation, "Get a link", "n8n's Bitly Link Get.") { Parameters = Schema(LinkField) },
            new ConnectorOperation(UpdateOperation, "Update a link", "Changes what is given - n8n's Bitly Link Update.")
            {
                Parameters = Schema(
                    LinkField,
                    Field("title", "Title", "Its new title."),
                    TagsField,
                    Field("archived", "Archived", "true or false.", ConnectorConfigFieldType.Boolean),
                    Field("long_url", "Address", "Its new long address - a paid plan's."),
                    Field("deeplinks", "Deep links", "A JSON list of deep links.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(LinksOperation, "Get links", "A group's links.")
            {
                Parameters = Schema(
                    Field("group", "Group", "The group's id - the user's default when left empty."),
                    Field("query", "Search", "Words in a link's title, address or tags."),
                    TagsField,
                    Field("archived", "Archived", "on, off (the default) or both."),
                    Field("created_after", "Created after", "A time - 2026-10-01T00:00:00Z."),
                    Field("created_before", "Created before", "A time."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(ClicksOperation, "Get a link's clicks", "Its clicks by minute, hour, day, week or month.")
            {
                Parameters = Schema(
                    LinkField,
                    Field("unit", "Unit", "minute, hour, day (the default), week or month."),
                    new ConnectorConfigField("units", "How many", ConnectorConfigFieldType.Integer, Required: false, Description: "How many units back - every one when left empty."),
                    Field("total", "Total", "true: one record, the total.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(GroupsOperation, "Get groups", "The groups the token's user is in.") { Parameters = Schema() },
            new ConnectorOperation(UserOperation, "Get the user", "The token's user and its default group.") { Parameters = Schema() },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var user = await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Bitly: the token is {Text(user?["name"]) ?? Text(user?["login"]) ?? "a user"}'s{(Text(user?["default_group_guid"]) is { } group ? $", its group {group}" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? CreateOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Bitly has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? link = null;
            if (operation is LinkOperation or UpdateOperation or ClicksOperation)
            {
                link = Values.Get(p, "link") is { } given ? Bitlink.Match(given) is { Success: true } match ? match.Groups["link"].Value : throw new InvalidOperationException($"'{given}' is not a short link - bit.ly/3xYz9.") : throw new InvalidOperationException("Give the link - bit.ly/3xYz9.");
            }

            var group = Values.Get(p, "group") is { } named ? GroupGuid.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a group's id.") : null;
            JsonArray? Tags() => Values.Get(p, "tags") is { } tags ? new JsonArray(tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()) : null;
            JsonArray? Deeplinks() => Values.Get(p, "deeplinks") is { } json ? Parse(json) is JsonArray { Count: > 0 } list && list.All(item => item is JsonObject) ? list : throw new InvalidOperationException("The deep links are not a JSON list of objects - [{\"app_id\": \"com.example\", \"app_uri_path\": \"/item/1\"}].") : null;
            string LongUrl(string given) => Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) ? given : throw new InvalidOperationException($"'{given}' is not an address - https://example.com/a/long/path.");

            using var client = session.Client;
            switch (operation)
            {
                case CreateOperation:
                    {
                        var body = new JsonObject { ["long_url"] = LongUrl(Values.Get(p, "long_url") ?? throw new InvalidOperationException("Give the address to shorten.")) };
                        if (Values.Get(p, "domain") is { } domain)
                        {
                            body["domain"] = Domain.IsMatch(domain) ? domain.ToLowerInvariant() : throw new InvalidOperationException($"'{domain}' is not a domain - bit.ly.");
                        }

                        if (group is not null)
                        {
                            body["group_guid"] = group;
                        }

                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = title;
                        }

                        if (Tags() is { } tags)
                        {
                            body["tags"] = tags;
                        }

                        if (Deeplinks() is { } deeplinks)
                        {
                            body["deeplinks"] = deeplinks;
                        }

                        yield return Link(await session.SendAsync(HttpMethod.Post, "/bitlinks", body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case UpdateOperation:
                    {
                        var body = new JsonObject();
                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = title;
                        }

                        if (Tags() is { } tags)
                        {
                            body["tags"] = tags;
                        }

                        if (Values.Get(p, "archived") is { } archived)
                        {
                            body["archived"] = archived.ToLowerInvariant() is "true" or "false" ? archived.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{archived}' is not true or false (archived).");
                        }

                        if (Values.Get(p, "long_url") is { } address)
                        {
                            body["long_url"] = LongUrl(address);
                        }

                        if (Deeplinks() is { } deeplinks)
                        {
                            body["deeplinks"] = deeplinks;
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - a title, tags, archived, the address or deep links.");
                        }

                        yield return Link(await session.SendAsync(HttpMethod.Patch, $"/bitlinks/{link}", body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case LinkOperation:
                    yield return Link(await session.SendAsync(HttpMethod.Get, $"/bitlinks/{link}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case LinksOperation:
                    {
                        var query = new List<string>();
                        if (Values.Get(p, "query") is { } search)
                        {
                            query.Add("query=" + Uri.EscapeDataString(search));
                        }

                        foreach (var tag in Tags() ?? new JsonArray())
                        {
                            query.Add("tags=" + Uri.EscapeDataString(tag!.GetValue<string>()));
                        }

                        if (Values.Get(p, "archived") is { } archived)
                        {
                            query.Add("archived=" + (archived.ToLowerInvariant() is "on" or "off" or "both" ? archived.ToLowerInvariant() : throw new InvalidOperationException($"'{archived}' is not on, off or both (archived).")));
                        }

                        foreach (var name in new[] { "created_after", "created_before" })
                        {
                            if (Values.Get(p, name) is { } when)
                            {
                                query.Add(name + "=" + (DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{when}' is not a time - 2026-10-01T00:00:00Z ({name}).")));
                            }
                        }

                        // A group's links, the user's default group when none is given.
                        var guid = group ?? Text((await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken) as JsonObject)?["default_group_guid"]) ?? throw new InvalidOperationException("The token's user has no default group - give the group.");

                        // Bitly pages by a cursor, every page the same size: the size is fixed at the start, a short page or no
                        // next the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? after = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/groups/{guid}/bitlinks?" + string.Join("&", query.Append($"size={size}").Concat(after is null ? Array.Empty<string>() : new[] { "search_after=" + Uri.EscapeDataString(after) })), null, cancellationToken) as JsonObject;
                            var links = (answer?["links"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in links)
                            {
                                yield return Link(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            after = Text(answer?["pagination"]?["search_after"]);
                            if (links.Count < size || after is not { Length: > 0 } || Text(answer?["pagination"]?["next"]) is not { Length: > 0 })
                            {
                                yield break;
                            }
                        }
                    }

                case ClicksOperation:
                    {
                        var unit = Values.Get(p, "unit")?.ToLowerInvariant() is { } chosen ? Units.Contains(chosen) ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "unit")}' is not minute, hour, day, week or month (unit).") : "day";
                        var units = Values.Get(p, "units") is { } count ? int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : throw new InvalidOperationException($"'{count}' is not a number above 0 (units).") : -1;
                        var total = Values.GetBool(p, "total", false);
                        var answer = await session.SendAsync(HttpMethod.Get, $"/bitlinks/{link}/clicks{(total ? "/summary" : "")}?unit={unit}&units={units.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken) as JsonObject;
                        if (total)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["link"] = link, ["clicks"] = Number(answer?["total_clicks"]), ["unit"] = unit });
                            yield break;
                        }

                        foreach (var day in (answer?["link_clicks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["link"] = link, ["date"] = Text(day["date"]), ["clicks"] = Number(day["clicks"]) });
                        }

                        yield break;
                    }

                case GroupsOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, "/groups", null, cancellationToken))?["groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Link(item);
                    }

                    yield break;
                default:
                    yield return Link(await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
            }
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A record: its fields as Bitly gives them, a list or an object as its JSON.
        private static ConnectorRecord Link(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
            var other => other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Bitly's API v4 with the token as a bearer token; a throttled call waiting as Bitly says; a
        // failure thrown with its status, Bitly's word and its description.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Bitly connection has no access token."), egress.CreateHttpClient(timeout));

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Bitly's - a sign-in page, a proxy's - read as no links would hide that it never was.
                    throw new InvalidOperationException($"Bitly answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var said = Text(error?["message"]) is { } message ? Text(error?["description"]) is { } description ? message + " - " + description : message : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
