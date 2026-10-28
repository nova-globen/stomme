#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

namespace Stomme.Modules.Connectors.MailerLite
{
    /// <summary>
    /// MailerLite (BL-229, n8n's MailerLite node: a subscriber created, got, read and updated): MailerLite's API with an API
    /// token as a bearer token, over the host's egress. A test names how many groups the token's account has.
    /// </summary>
    /// <remarks>
    /// <c>subscribers</c> (by status, page by page by MailerLite's cursor up to <c>max</c>), <c>subscriber</c> (by id or
    /// e-mail), <c>create_subscriber</c> (MailerLite's upsert: its fields, groups, status, times and addresses),
    /// <c>update_subscriber</c>, <c>delete_subscriber</c> and <c>forget_subscriber</c> (deleted with their data); <c>groups</c>
    /// (by name, sorted, page by page), <c>create_group</c>, <c>update_group</c>, <c>delete_group</c>,
    /// <c>group_subscribers</c>, <c>add_to_group</c> and <c>remove_from_group</c>; <c>fields</c>. A subscriber given by e-mail
    /// where MailerLite asks for an id is looked up first. A record is its fields as MailerLite gives them, a list or an
    /// object as its JSON. Every id, e-mail, field, status, time, address and sort held to its shape before anything is sent;
    /// a time sent as MailerLite reads it. A throttled call waits as MailerLite says; a failure is thrown with its status,
    /// MailerLite's word and what it found wrong with each field.
    /// </remarks>
    public sealed class MailerLiteConnector : IConnector, IConnectorExtractor
    {
        public const string SubscribersOperation = "subscribers";

        public const string SubscriberOperation = "subscriber";

        public const string CreateSubscriberOperation = "create_subscriber";

        public const string UpdateSubscriberOperation = "update_subscriber";

        public const string DeleteSubscriberOperation = "delete_subscriber";

        public const string ForgetSubscriberOperation = "forget_subscriber";

        public const string GroupsOperation = "groups";

        public const string CreateGroupOperation = "create_group";

        public const string UpdateGroupOperation = "update_group";

        public const string DeleteGroupOperation = "delete_group";

        public const string GroupSubscribersOperation = "group_subscribers";

        public const string AddToGroupOperation = "add_to_group";

        public const string RemoveFromGroupOperation = "remove_from_group";

        public const string FieldsOperation = "fields";

        public const string DefaultEndpoint = "https://connect.mailerlite.com/api";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[0-9]{1,30}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Sort = new("^-?(name|total|open_rate|click_rate|created_at)$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SubscribersOperation, SubscriberOperation, CreateSubscriberOperation, UpdateSubscriberOperation, DeleteSubscriberOperation, ForgetSubscriberOperation, GroupsOperation, CreateGroupOperation, UpdateGroupOperation, DeleteGroupOperation, GroupSubscribersOperation, AddToGroupOperation, RemoveFromGroupOperation, FieldsOperation };

        private static readonly string[] Statuses = { "active", "unsubscribed", "unconfirmed", "bounced", "junk" };

        public string ConnectorType => "mailerlite";

        public string DisplayName => "MailerLite";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "API token (Integrations > MailerLite API)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.mailerlite.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.mailerlite.com/docs/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField SubscriberField = Field("subscriber", "Subscriber", "The subscriber's id or e-mail.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "The group's id.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField StatusFilterField = Field("status", "Status", "active, unsubscribed, unconfirmed, bounced or junk - every one when left empty.");

        private static ConnectorConfigField[] SubscriberFields(params ConnectorConfigField[] first) => first.Concat(new[]
        {
            Field("fields", "Fields", "A JSON object of its fields by their keys - {\"name\": \"Ada\", \"company\": \"Kafé\"}.", ConnectorConfigFieldType.Multiline),
            Field("groups", "Groups", "Comma-separated group ids."),
            Field("status", "Status", "active, unsubscribed, unconfirmed, bounced or junk."),
            Field("subscribed_at", "Subscribed at", "A time - 2026-10-13 10:00:00, UTC when it names no offset."),
            Field("ip_address", "IP address", "The address they subscribed from."),
            Field("opted_in_at", "Opted in at", "A time."),
            Field("optin_ip", "Opt-in IP address", "The address they confirmed from."),
            Field("unsubscribed_at", "Unsubscribed at", "A time."),
        }).ToArray();

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SubscribersOperation, "Get subscribers", "Subscribers, by status - n8n's MailerLite Subscriber Get Many.") { Parameters = Schema(StatusFilterField, MaxField) },
            new ConnectorOperation(SubscriberOperation, "Get a subscriber", "By id or e-mail - n8n's MailerLite Subscriber Get.") { Parameters = Schema(SubscriberField) },
            new ConnectorOperation(CreateSubscriberOperation, "Create a subscriber", "Created, or updated when the e-mail is already a subscriber's - n8n's MailerLite Subscriber Create.")
            {
                Parameters = Schema(SubscriberFields(Field("email", "E-mail", "Their e-mail.", required: true))),
            },
            new ConnectorOperation(UpdateSubscriberOperation, "Update a subscriber", "Changes what is given - n8n's MailerLite Subscriber Update.") { Parameters = Schema(SubscriberFields(SubscriberField)) },
            new ConnectorOperation(DeleteSubscriberOperation, "Delete a subscriber", "Deleted - their data kept by MailerLite.") { Parameters = Schema(SubscriberField) },
            new ConnectorOperation(ForgetSubscriberOperation, "Forget a subscriber", "Deleted with their data, as the GDPR asks - within 30 days.") { Parameters = Schema(SubscriberField) },
            new ConnectorOperation(GroupsOperation, "Get groups", "Groups, by name.")
            {
                Parameters = Schema(
                    Field("name", "Name", "Words in a group's name."),
                    Field("sort", "Sort", "name, total, open_rate, click_rate or created_at - a leading - for descending."),
                    MaxField),
            },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "A group by its name.") { Parameters = Schema(Field("name", "Name", "Its name.", required: true)) },
            new ConnectorOperation(UpdateGroupOperation, "Rename a group", "A group's new name.") { Parameters = Schema(GroupField, Field("name", "Name", "Its new name.", required: true)) },
            new ConnectorOperation(DeleteGroupOperation, "Delete a group", "A group deleted - its subscribers kept.") { Parameters = Schema(GroupField) },
            new ConnectorOperation(GroupSubscribersOperation, "Get a group's subscribers", "A group's subscribers, by status.") { Parameters = Schema(GroupField, StatusFilterField, MaxField) },
            new ConnectorOperation(AddToGroupOperation, "Add to a group", "A subscriber added to a group.") { Parameters = Schema(SubscriberField, GroupField) },
            new ConnectorOperation(RemoveFromGroupOperation, "Remove from a group", "A subscriber taken out of a group.") { Parameters = Schema(SubscriberField, GroupField) },
            new ConnectorOperation(FieldsOperation, "Get fields", "The subscribers' fields - their keys, names and types.") { Parameters = Schema(MaxField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var groups = await session.SendAsync(HttpMethod.Get, "/groups?limit=1", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"MailerLite: the token is accepted{(IdOf(groups?["meta"]?["total"]) is { } total ? $" - its account has {total} group{(total == "1" ? "" : "s")}" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SubscribersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"MailerLite has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? subscriber = null;
            if (operation is SubscriberOperation or UpdateSubscriberOperation or DeleteSubscriberOperation or ForgetSubscriberOperation or AddToGroupOperation or RemoveFromGroupOperation)
            {
                subscriber = Values.Get(p, "subscriber") is { } given ? Id.IsMatch(given) || Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a subscriber's id or e-mail.") : throw new InvalidOperationException("Give the subscriber - their id or e-mail.");
            }

            string? group = null;
            if (operation is UpdateGroupOperation or DeleteGroupOperation or GroupSubscribersOperation or AddToGroupOperation or RemoveFromGroupOperation)
            {
                group = Values.Get(p, "group") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a group's id.") : throw new InvalidOperationException("Give the group's id.");
            }

            var status = Values.Get(p, "status") is { } chosen ? Statuses.Contains(chosen.ToLowerInvariant()) ? chosen.ToLowerInvariant() : throw new InvalidOperationException($"'{chosen}' is not active, unsubscribed, unconfirmed, bounced or junk (status).") : null;
            JsonObject? body = null;
            if (operation is CreateSubscriberOperation or UpdateSubscriberOperation)
            {
                body = new JsonObject();
                if (operation == CreateSubscriberOperation)
                {
                    body["email"] = Values.Get(p, "email") is { } email ? Email.IsMatch(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail (email).") : throw new InvalidOperationException("Give their e-mail.");
                }

                if (Values.Get(p, "fields") is { } fields)
                {
                    var parsed = Parse(fields) as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object - {\"name\": \"Ada\", \"company\": \"Kafé\"}.");
                    foreach (var (key, value) in parsed)
                    {
                        if (value is not (null or JsonValue))
                        {
                            throw new InvalidOperationException($"'{key}' is not a text, a number or null (fields).");
                        }
                    }

                    body["fields"] = parsed;
                }

                if (Values.Get(p, "groups") is { } groups)
                {
                    body["groups"] = new JsonArray(groups.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(id => (JsonNode?)JsonValue.Create(Id.IsMatch(id) ? id : throw new InvalidOperationException($"'{id}' is not a group's id (groups)."))).ToArray());
                }

                if (status is not null)
                {
                    body["status"] = status;
                }

                foreach (var name in new[] { "subscribed_at", "opted_in_at", "unsubscribed_at" })
                {
                    if (Values.Get(p, name) is { } when)
                    {
                        body[name] = DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{when}' is not a time - 2026-10-13 10:00:00 ({name}).");
                    }
                }

                foreach (var name in new[] { "ip_address", "optin_ip" })
                {
                    if (Values.Get(p, name) is { } address)
                    {
                        // An IPv4 address in its four parts - .NET would read 192.0.2 as 192.0.0.2.
                        body[name] = IPAddress.TryParse(address, out var ip) && (ip.AddressFamily == AddressFamily.InterNetworkV6 || address.Count(c => c == '.') == 3) ? address : throw new InvalidOperationException($"'{address}' is not an IP address ({name}).");
                    }
                }

                if (operation == UpdateSubscriberOperation && body.Count == 0)
                {
                    throw new InvalidOperationException("Give what to change - their fields, groups, status, times or addresses.");
                }
            }

            string Name() => Values.Get(p, "name") is { } name ? name.Length <= 255 ? name : throw new InvalidOperationException("The group's name is longer than 255 characters.") : throw new InvalidOperationException("Give the group's name.");
            if (operation is CreateGroupOperation or UpdateGroupOperation)
            {
                body = new JsonObject { ["name"] = Name() };
            }

            var query = new List<string>();
            if (status is not null && operation is SubscribersOperation or GroupSubscribersOperation)
            {
                query.Add("filter%5Bstatus%5D=" + status);
            }

            if (operation == GroupsOperation)
            {
                if (Values.Get(p, "name") is { } words)
                {
                    query.Add("filter%5Bname%5D=" + Uri.EscapeDataString(words));
                }

                if (Values.Get(p, "sort") is { } sort)
                {
                    query.Add("sort=" + (Sort.IsMatch(sort) ? sort : throw new InvalidOperationException($"'{sort}' is not name, total, open_rate, click_rate or created_at, a leading - for descending (sort).")));
                }
            }

            using var client = session.Client;

            // A subscriber given by e-mail where MailerLite asks for an id: looked up first.
            async Task<string> SubscriberId() => Id.IsMatch(subscriber!) ? subscriber! : IdOf((await session.SendAsync(HttpMethod.Get, "/subscribers/" + Uri.EscapeDataString(subscriber!), null, cancellationToken))?["data"]?["id"]) ?? throw new InvalidOperationException($"MailerLite gave no id for the subscriber {subscriber}.");
            switch (operation)
            {
                case SubscriberOperation:
                    yield return Record(Data(await session.SendAsync(HttpMethod.Get, "/subscribers/" + Uri.EscapeDataString(subscriber!), null, cancellationToken)));
                    yield break;
                case CreateSubscriberOperation:
                    yield return Record(Data(await session.SendAsync(HttpMethod.Post, "/subscribers", body, cancellationToken)));
                    yield break;
                case UpdateSubscriberOperation:
                    {
                        var id = await SubscriberId();
                        yield return Record(Data(await session.SendAsync(HttpMethod.Put, $"/subscribers/{id}", body, cancellationToken)));
                        yield break;
                    }

                case DeleteSubscriberOperation:
                    {
                        var id = await SubscriberId();
                        await session.SendAsync(HttpMethod.Delete, $"/subscribers/{id}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["deleted"] = true });
                        yield break;
                    }

                case ForgetSubscriberOperation:
                    {
                        var id = await SubscriberId();
                        var answer = await session.SendAsync(HttpMethod.Post, $"/subscribers/{id}/forget", null, cancellationToken) as JsonObject;
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["forgotten"] = true, ["message"] = Text(answer?["message"]) });
                        yield break;
                    }

                case CreateGroupOperation:
                    yield return Record(Data(await session.SendAsync(HttpMethod.Post, "/groups", body, cancellationToken)));
                    yield break;
                case UpdateGroupOperation:
                    yield return Record(Data(await session.SendAsync(HttpMethod.Put, $"/groups/{group}", body, cancellationToken)));
                    yield break;
                case DeleteGroupOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/groups/{group}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = group, ["deleted"] = true });
                    yield break;
                case AddToGroupOperation:
                    {
                        var id = await SubscriberId();
                        var added = Data(await session.SendAsync(HttpMethod.Post, $"/subscribers/{id}/groups/{group}", null, cancellationToken));
                        added["subscriber"] = id;
                        yield return Record(added);
                        yield break;
                    }

                case RemoveFromGroupOperation:
                    {
                        var id = await SubscriberId();
                        await session.SendAsync(HttpMethod.Delete, $"/subscribers/{id}/groups/{group}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["subscriber"] = id, ["group"] = group, ["removed"] = true });
                        yield break;
                    }

                case SubscribersOperation or GroupSubscribersOperation:
                    {
                        // MailerLite pages subscribers by a cursor, every page the same size: the size is fixed at the start, a
                        // short page or no next cursor the end.
                        var path = operation == SubscribersOperation ? "/subscribers" : $"/groups/{group}/subscribers";
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? cursor = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"limit={size}").Concat(cursor is null ? Array.Empty<string>() : new[] { "cursor=" + Uri.EscapeDataString(cursor) })), null, cancellationToken) as JsonObject;
                            var items = (answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            cursor = Text(answer?["meta"]?["next_cursor"]);
                            if (items.Count < size || cursor is not { Length: > 0 })
                            {
                                yield break;
                            }
                        }
                    }

                default:
                    {
                        // Groups and fields page by number: the size fixed at the start, a short page or the last page the end.
                        var path = operation == GroupsOperation ? "/groups" : "/fields";
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"limit={size}").Append($"page={page}")), null, cancellationToken) as JsonObject;
                            var items = (answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count < size || (IdOf(answer?["meta"]?["last_page"]) is { } last && page >= int.Parse(last, CultureInfo.InvariantCulture)))
                            {
                                yield break;
                            }
                        }
                    }
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

        // What MailerLite answers about one thing is under its data.
        private static JsonObject Data(JsonNode? answer) => (answer?["data"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();

        // A record: its fields as MailerLite gives them, a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
            var other => other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        // An id as MailerLite gives it - a string of digits, or a number.
        private static string? IdOf(JsonNode? node) => node is JsonValue value ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: MailerLite's API with the token as a bearer token, asking for JSON; a throttled call waiting
        // as MailerLite says; a failure thrown with its status, MailerLite's word and what it found wrong with each field.
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

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The MailerLite connection has no API token.");
                var baseUrl = Values.BaseUrl(endpoint, DefaultEndpoint);
                return new(baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/api", token, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                    // A page that is not MailerLite's - a sign-in page, a proxy's - read as no subscribers would hide that it
                    // never was.
                    throw new InvalidOperationException($"MailerLite answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var said = Text(error?["message"]);
                    if (error?["errors"] is JsonObject { Count: > 0 } errors)
                    {
                        var each = string.Join("; ", errors.Select(pair => pair.Key + ": " + (pair.Value is JsonArray list ? string.Join(" ", list.Select(Text).Where(text => text is not null)) : Text(pair.Value))));
                        said = said is null ? each : said + " - " + each;
                    }

                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
