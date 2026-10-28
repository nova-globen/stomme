#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.ElasticSecurity
{
    /// <summary>
    /// Elastic Security (BL-229, n8n's Elastic Security node: cases, their comments and tags) over Kibana's Cases API by a Kibana
    /// user's name and password - Basic, as n8n's node signs in - with Kibana's <c>kbn-xsrf</c> header on every call, in the
    /// default space or the connection's own. A test reads the open cases' count.
    /// </summary>
    /// <remarks>
    /// <c>cases</c> (Kibana's find: by status, severity, tags and text, sorted, page by page up to <c>max</c>), <c>case</c>,
    /// <c>case_create</c> (its title, description, tags, severity, category, owner and whether alerts sync, with no external
    /// connector), <c>case_update</c> (only what is given - its title, description, status, severity, tags, category, alert sync -
    /// at the version Kibana last gave it, read first as n8n's node reads it), <c>case_delete</c>, <c>case_status</c> (the
    /// counts of open, in-progress and closed cases - Kibana 9 has no status endpoint, measured, so they come from its find),
    /// <c>case_comments</c>, <c>case_comment</c>, <c>case_comment_add</c> (a comment over several lines from the step's Query
    /// box), <c>case_comment_update</c>, <c>case_comment_delete</c>, <c>case_tag_add</c> and <c>case_tag_remove</c> (a tag added
    /// or removed at the case's version - a tag already there, or not there, leaves the case as it is, where n8n's node failed).
    /// n8n's Connector Create is not here: it takes a Jira token, a ServiceNow password or a Resilient key as a step's input, and a
    /// password is never a step's parameter. Every id, choice, tag, length and text held to Kibana's shape before anything is
    /// sent; Kibana's message on a failure, a 409 saying another change landed first.
    /// </remarks>
    public sealed class ElasticSecurityConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100; // Kibana's find, its largest page

        private static readonly string[] Known =
        {
            "cases", "case", "case_create", "case_update", "case_delete", "case_status",
            "case_comments", "case_comment", "case_comment_add", "case_comment_update", "case_comment_delete", "case_tag_add", "case_tag_remove",
        };

        private static readonly string[] Statuses = { "open", "in-progress", "closed" };

        private static readonly string[] Severities = { "low", "medium", "high", "critical" };

        private static readonly string[] Owners = { "securitySolution", "observability", "cases" };

        private static readonly Dictionary<string, string> SortFields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["createdAt"] = "createdAt", ["updatedAt"] = "updatedAt", ["closedAt"] = "closedAt", ["title"] = "title", ["status"] = "status", ["severity"] = "severity", ["category"] = "category",
        };

        private static readonly Regex Space = new("^[a-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // Kibana's text as it is, not as \u escapes

        public string ConnectorType => "elasticsecurity";

        public string DisplayName => "Elastic Security";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Kibana user name", ["password"] = "Password" };

        public string EndpointExample => "https://kibana.kafe.example:5601";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://www.elastic.co/security");

        public Uri ApiDocumentationUrl { get; } = new("https://www.elastic.co/docs/api/doc/kibana/group/endpoint-cases");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("space", "Space", ConnectorConfigFieldType.Text, Required: false, Description: "The Kibana space the cases are in - soc. Empty: the default space."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField CaseField = Field("case", "Case", "Its id - the id a create or a list answered.", required: true);

        private static readonly ConnectorConfigField CommentField = Field("comment_id", "Comment", "Its id.", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("cases", "List cases", "Kibana's cases, by their status, severity, tags and text - n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    Field("status", "Status", "open, in-progress or closed."),
                    Field("severity", "Severity", "low, medium, high or critical."),
                    Field("tags", "Tags", "By commas - phishing, kafe."),
                    Field("search", "Text", "Words in a case's title or description."),
                    Field("sort", "Sorted by", "createdAt (n8n's default), updatedAt, closedAt, title, status, severity or category."),
                    Field("order", "Order", "asc (n8n's default) or desc."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many cases, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("case", "Get a case", "n8n's Get.") { Parameters = new(new[] { CaseField }) },
            new ConnectorOperation("case_create", "Create a case", "n8n's Create.")
            {
                Parameters = new(new[]
                {
                    Field("title", "Title", "Phishing against the till staff.", required: true),
                    Field("description", "Description", "What happened - over several lines from the step's Query box. Kibana requires one.", required: true),
                    Field("tags", "Tags", "By commas - phishing, kafe."),
                    Field("severity", "Severity", "low (Kibana's default), medium, high or critical."),
                    Field("category", "Category", "Phishing."),
                    Field("owner", "Owner", "securitySolution (n8n's), observability or cases."),
                    Field("sync_alerts", "Sync alerts", "true: the case's alerts follow its status."),
                }),
            },
            new ConnectorOperation("case_update", "Update a case", "Only what is given - n8n's Update.")
            {
                Parameters = new(new[]
                {
                    CaseField, Field("title", "Title", "Phishing against the till staff, contained."), Field("description", "Description", "Over several lines from the step's Query box."),
                    Field("status", "Status", "open, in-progress or closed."), Field("severity", "Severity", "low, medium, high or critical."),
                    Field("tags", "Tags", "All of them, by commas - they replace the case's."), Field("category", "Category", "Phishing."), Field("sync_alerts", "Sync alerts", "true or false."),
                }),
            },
            new ConnectorOperation("case_delete", "Delete a case", "n8n's Delete.") { Parameters = new(new[] { CaseField }) },
            new ConnectorOperation("case_status", "Count cases by status", "Open, in progress and closed - n8n's Get Status."),
            new ConnectorOperation("case_comments", "List a case's comments", "n8n's Comment Get Many.")
            {
                Parameters = new(new[] { CaseField, new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many comments, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation("case_comment", "Get a comment", "n8n's Comment Get.") { Parameters = new(new[] { CaseField, CommentField }) },
            new ConnectorOperation("case_comment_add", "Add a comment", "n8n's Comment Add.")
            {
                Parameters = new(new[] { CaseField, Field("comment", "Comment", "Seen in the till's logs - over several lines from the step's Query box.", required: true), Field("owner", "Owner", "The case's - securitySolution (n8n's), observability or cases.") }),
            },
            new ConnectorOperation("case_comment_update", "Update a comment", "n8n's Comment Update.")
            {
                Parameters = new(new[] { CaseField, CommentField, Field("comment", "Comment", "Over several lines from the step's Query box.", required: true) }),
            },
            new ConnectorOperation("case_comment_delete", "Delete a comment", "n8n's Comment Remove.") { Parameters = new(new[] { CaseField, CommentField }) },
            new ConnectorOperation("case_tag_add", "Tag a case", "n8n's Case Tag Add.") { Parameters = new(new[] { CaseField, Field("tag", "Tag", "contained.", required: true) }) },
            new ConnectorOperation("case_tag_remove", "Untag a case", "n8n's Case Tag Remove.") { Parameters = new(new[] { CaseField, Field("tag", "Tag", "contained.", required: true) }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
                var found = await session.SendAsync(HttpMethod.Get, "/cases/_find?perPage=1", null, cancellationToken) as JsonObject;
                return found?["count_open_cases"] is JsonValue open && open.TryGetValue<long>(out var count)
                    ? ConnectionTestResult.Ok($"Kibana answers - {count} open cases.")
                    : ConnectionTestResult.Fail("Kibana answered without its cases - check the address: a sign-in page or a proxy answers so.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "cases").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Elastic Security has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to Kibana's shape before anything is sent.
            string? caseId = operation is "cases" or "case_create" or "case_status" ? null : Id(p, "case", "a case");
            string? commentId = operation is "case_comment" or "case_comment_update" or "case_comment_delete" ? Id(p, "comment_id", "a comment") : null;
            var query = new List<(string Name, string Value)>();
            JsonObject? body = null;
            string? tag = null;
            var max = DefaultMax;
            switch (operation)
            {
                case "cases":
                    Pick(p, "status", Statuses, query);
                    Pick(p, "severity", Severities, query);
                    if (Tags(p) is { } tags)
                    {
                        query.AddRange(tags.Select(one => ("tags", one)));
                    }

                    if (Values.Get(p, "search") is { } search)
                    {
                        query.Add(("search", search.Length <= 500 && !search.Any(char.IsControl) ? search : throw new InvalidOperationException("A search is at most 500 characters, on one line (search).")));
                    }

                    query.Add(("sortField", Values.Get(p, "sort") is { } sort ? SortFields.TryGetValue(sort, out var field) ? field : throw new InvalidOperationException($"'{sort}' is not createdAt, updatedAt, closedAt, title, status, severity or category (sort).") : "createdAt"));
                    query.Add(("sortOrder", (Values.Get(p, "order") ?? "asc").ToLowerInvariant() is ("asc" or "desc") and var order ? order : throw new InvalidOperationException($"'{Values.Get(p, "order")}' is not asc or desc (order).")));
                    max = Most(p);
                    break;
                case "case_comments":
                    max = Most(p);
                    break;
                case "case_create":
                    {
                        body = new JsonObject { ["title"] = Title(Values.Get(p, "title") ?? throw new InvalidOperationException("Give the case's title (title).")) };
                        body["description"] = Text(p, "description", required: true); // Kibana refuses a case without one (measured)
                        body["tags"] = new JsonArray((Tags(p) ?? new List<string>()).Select(one => (JsonNode?)one).ToArray());
                        body["connector"] = new JsonObject { ["id"] = "none", ["name"] = "none", ["type"] = ".none", ["fields"] = null };
                        body["settings"] = new JsonObject { ["syncAlerts"] = Flag(p, "sync_alerts") ?? false };
                        body["owner"] = Owner(p);
                        Pick(p, "severity", Severities, body);
                        Category(p, body);
                        break;
                    }

                case "case_update":
                    {
                        body = new JsonObject();
                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = Title(title);
                        }

                        if (Text(p, "description", required: false) is { } description)
                        {
                            body["description"] = description;
                        }

                        Pick(p, "status", Statuses, body);
                        Pick(p, "severity", Severities, body);
                        if (Tags(p) is { } replaced)
                        {
                            body["tags"] = new JsonArray(replaced.Select(one => (JsonNode?)one).ToArray());
                        }

                        Category(p, body);
                        if (Flag(p, "sync_alerts") is { } sync)
                        {
                            body["settings"] = new JsonObject { ["syncAlerts"] = sync };
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - title, description, status, severity, tags, category or alert sync.");
                        }

                        break;
                    }

                case "case_comment_add" or "case_comment_update":
                    body = new JsonObject { ["type"] = "user", ["comment"] = Text(p, "comment", required: true) };
                    if (operation == "case_comment_add")
                    {
                        body["owner"] = Owner(p);
                    }

                    break;
                case "case_tag_add" or "case_tag_remove":
                    tag = Values.Get(p, "tag") ?? throw new InvalidOperationException("Give the tag - contained (tag).");
                    tag = tag.Length <= 256 && !tag.Any(char.IsControl) && !tag.Contains(',') ? tag : throw new InvalidOperationException($"'{tag}' is not a tag - at most 256 characters, one line, no comma (tag).");
                    break;
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
            switch (operation)
            {
                case "cases" or "case_comments":
                    {
                        // Page by page, the page the same size each time - Kibana counts its pages by it - until a short page or the most.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var route = (operation == "cases" ? "/cases/_find" : $"/cases/{caseId}/comments/_find")
                                + "?" + string.Join("&", query.Append(("page", page.ToString(CultureInfo.InvariantCulture))).Append(("perPage", size.ToString(CultureInfo.InvariantCulture))).Select(pair => pair.Item1 + "=" + Uri.EscapeDataString(pair.Item2)));
                            var found = await session.SendAsync(HttpMethod.Get, route, null, cancellationToken) as JsonObject;
                            var items = found?[operation == "cases" ? "cases" : "comments"] as JsonArray ?? throw new InvalidOperationException("Kibana answered without its list - check the address: a sign-in page or a proxy answers so.");
                            foreach (var item in items.OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count < size)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "case":
                    yield return Record(await CaseAsync(session, caseId!, cancellationToken));
                    break;
                case "case_create":
                    yield return Record(Object(await session.SendAsync(HttpMethod.Post, "/cases", body, cancellationToken)));
                    break;
                case "case_update":
                    {
                        var current = await CaseAsync(session, caseId!, cancellationToken);
                        yield return Record(await PatchAsync(session, caseId!, current, body!, cancellationToken));
                        break;
                    }

                case "case_tag_add" or "case_tag_remove":
                    {
                        var current = await CaseAsync(session, caseId!, cancellationToken);
                        var tags = (current["tags"] as JsonArray ?? new JsonArray()).Select(one => one?.GetValue<string>() ?? string.Empty).ToList();
                        var changed = operation == "case_tag_add" ? !tags.Contains(tag!, StringComparer.Ordinal) : tags.Contains(tag!, StringComparer.Ordinal);
                        if (!changed)
                        {
                            yield return Record(current); // already as asked: the case as it is
                            break;
                        }

                        var next = operation == "case_tag_add" ? tags.Append(tag!) : tags.Where(one => one != tag);
                        yield return Record(await PatchAsync(session, caseId!, current, new JsonObject { ["tags"] = new JsonArray(next.Select(one => (JsonNode?)one).ToArray()) }, cancellationToken));
                        break;
                    }

                case "case_delete":
                    await session.SendAsync(HttpMethod.Delete, "/cases?ids=" + Uri.EscapeDataString(new JsonArray((JsonNode?)caseId).ToJsonString()), null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = caseId, ["deleted"] = true });
                    break;
                case "case_status":
                    {
                        var found = await session.SendAsync(HttpMethod.Get, "/cases/_find?perPage=1", null, cancellationToken) as JsonObject
                            ?? throw new InvalidOperationException("Kibana answered without its cases - check the address: a sign-in page or a proxy answers so.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["count_open_cases"] = Number(found["count_open_cases"]),
                            ["count_in_progress_cases"] = Number(found["count_in_progress_cases"]),
                            ["count_closed_cases"] = Number(found["count_closed_cases"]),
                        });
                        break;
                    }

                case "case_comment":
                    yield return Record(Object(await session.SendAsync(HttpMethod.Get, $"/cases/{caseId}/comments/{commentId}", null, cancellationToken)));
                    break;
                case "case_comment_add":
                    {
                        var after = Object(await session.SendAsync(HttpMethod.Post, $"/cases/{caseId}/comments", body, cancellationToken));
                        yield return Record((after["comments"] as JsonArray)?.OfType<JsonObject>().LastOrDefault() ?? after); // the comment made, as n8n's node's Simplify gives it
                        break;
                    }

                case "case_comment_update":
                    {
                        var current = Object(await session.SendAsync(HttpMethod.Get, $"/cases/{caseId}/comments/{commentId}", null, cancellationToken));
                        body!["id"] = commentId;
                        body["version"] = current["version"]?.DeepClone();
                        body["owner"] = current["owner"]?.DeepClone();
                        await session.SendAsync(new HttpMethod("PATCH"), $"/cases/{caseId}/comments", body, cancellationToken);
                        yield return Record(Object(await session.SendAsync(HttpMethod.Get, $"/cases/{caseId}/comments/{commentId}", null, cancellationToken)));
                        break;
                    }

                default:
                    await session.SendAsync(HttpMethod.Delete, $"/cases/{caseId}/comments/{commentId}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = commentId, ["case"] = caseId, ["deleted"] = true });
                    break;
            }
        }

        private static async Task<JsonObject> CaseAsync(Session session, string caseId, CancellationToken cancellationToken) =>
            Object(await session.SendAsync(HttpMethod.Get, $"/cases/{caseId}", null, cancellationToken));

        // A change at the version Kibana last gave the case - a 409 when another change landed between.
        private static async Task<JsonObject> PatchAsync(Session session, string caseId, JsonObject current, JsonObject changes, CancellationToken cancellationToken)
        {
            var change = new JsonObject { ["id"] = caseId, ["version"] = current["version"]?.DeepClone() };
            foreach (var (name, value) in changes)
            {
                change[name] = value?.DeepClone();
            }

            var answer = await session.SendAsync(new HttpMethod("PATCH"), "/cases", new JsonObject { ["cases"] = new JsonArray(change) }, cancellationToken);
            return (answer as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("Kibana answered with what is not the case changed - check the address: a sign-in page or a proxy answers so.");
        }

        private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new InvalidOperationException("Kibana answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");

        private static string Id(IReadOnlyDictionary<string, string> p, string name, string what)
        {
            var given = Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what} - its id ({name}).");
            return Guid.TryParseExact(given, "D", out var id) ? id.ToString("D") : throw new InvalidOperationException($"'{given}' is not {what}'s id - 5d801d85-1245-48ab-9999-84e858897359 ({name}).");
        }

        private static string Title(string title) => title.Length <= 160 && !title.Any(char.IsControl) ? title : throw new InvalidOperationException("A case's title is at most 160 characters, on one line (title).");

        // A text over several lines: the step's Query box first, then the named parameter.
        private static string? Text(IReadOnlyDictionary<string, string> p, string name, bool required)
        {
            var given = p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : Values.Get(p, name);
            if (given is null)
            {
                return required ? throw new InvalidOperationException($"Give the {name} ({name}) - over several lines in the step's Query box.") : null;
            }

            return given.Length <= 30000 ? given : throw new InvalidOperationException($"A {name} is at most 30000 characters - this is {given.Length} ({name}).");
        }

        private static List<string>? Tags(IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "tags") is not { } given)
            {
                return null;
            }

            var tags = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
            return tags.Count <= 200 && tags.All(one => one.Length <= 256 && !one.Any(char.IsControl)) ? tags : throw new InvalidOperationException("A case has at most 200 tags, each at most 256 characters on one line (tags).");
        }

        private static void Pick(IReadOnlyDictionary<string, string> p, string name, string[] allowed, List<(string Name, string Value)> query)
        {
            if (Values.Get(p, name) is { } given)
            {
                query.Add((name, Choose(given, name, allowed)));
            }
        }

        private static void Pick(IReadOnlyDictionary<string, string> p, string name, string[] allowed, JsonObject body)
        {
            if (Values.Get(p, name) is { } given)
            {
                body[name] = Choose(given, name, allowed);
            }
        }

        private static string Choose(string given, string name, string[] allowed) =>
            allowed.FirstOrDefault(one => string.Equals(one, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not {string.Join(", ", allowed[..^1])} or {allowed[^1]} ({name}).");

        private static string Owner(IReadOnlyDictionary<string, string> p) => Values.Get(p, "owner") is { } owner ? Choose(owner, "owner", Owners) : "securitySolution";

        private static void Category(IReadOnlyDictionary<string, string> p, JsonObject body)
        {
            if (Values.Get(p, "category") is { } category)
            {
                body["category"] = category.Length <= 50 && !category.Any(char.IsControl) ? category : throw new InvalidOperationException("A category is at most 50 characters, on one line (category).");
            }
        }

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static int Most(IReadOnlyDictionary<string, string> p) =>
            Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;

        private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

        // An object as a record: its plain values as they are, a list or an object as JSON.
        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(),
        }, StringComparer.Ordinal));

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;

            private Session(HttpClient client, string api)
            {
                this.client = client;
                this.api = api;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Kibana's address - https://kibana.kafe.example:5601.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Kibana's address - https://kibana.kafe.example:5601, with no query.");
                }

                var space = Values.Get(settings, "space");
                if (space is not null && !Space.IsMatch(space))
                {
                    throw new InvalidOperationException($"'{space}' is not a Kibana space's id - soc, lower-case letters, digits, _ and - (space).");
                }

                return new Session(BearerClient.Create(egress, timeout, BearerClient.Basic(credentials, "Elastic Security"), acceptJson: true), root + (space is null or "default" ? string.Empty : "/s/" + space) + "/api");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with Kibana's kbn-xsrf header; Kibana's JSON, nothing for a 204, or its status and message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Add("kbn-xsrf", "true");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    return null;
                }

                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Kibana answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("Kibana answered with nothing - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var said = (json as JsonObject)?["message"] is JsonValue message && message.TryGetValue<string>(out var text) ? text.Replace("\n", " ", StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal) : null;
                said = said is { Length: > 300 } ? said[..300] : said;
                throw new InvalidOperationException($"Elastic Security answered HTTP {status}{(said is null ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + status switch
                    {
                        401 => " - check the user name and password.",
                        403 => " - check the user's Kibana privileges for cases.",
                        409 => " - another change landed first; run the step again.",
                        _ => string.Empty,
                    });
            }
        }
    }
}
