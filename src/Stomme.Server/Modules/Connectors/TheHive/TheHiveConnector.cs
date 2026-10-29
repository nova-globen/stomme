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

namespace Stomme.Modules.Connectors.TheHive
{
    /// <summary>
    /// TheHive 5 (n8n's TheHive 5 node: alerts, cases, comments, tasks and their logs, observables) over TheHive's API v1
    /// (<c>{address}/api/v1</c>) by an API key as a bearer token in the Authorization header - never in the address. A test asks
    /// TheHive who the key is.
    /// </summary>
    /// <remarks>
    /// Alerts searched (by title, status, severity, tag, source and type), read, raised, changed, promoted to a case, merged into
    /// one and deleted; cases the same, a case found by its id (<c>~4415736</c>) or its number (<c>2</c>, <c>#2</c>); comments on
    /// a case or an alert listed, added, changed and deleted; a case's tasks and a task's logs listed and added, a task read,
    /// changed and deleted; observables on a case or an alert listed, added, read, changed and deleted. Searches through TheHive's
    /// query API (<c>/api/v1/query</c>), page by page, newest first, up to <c>max</c>. Severity, TLP and PAP by number or by
    /// name; every id, date, choice and length held to TheHive's shape before anything is sent; TheHive's message and the field
    /// it refused on a failure. n8n's responders, analyzers, attachments, pages and custom queries are not here: responders and
    /// analyzers need Cortex, and the import says which steps it left. Measured against TheHive 5.5.16
    /// (<c>scripts/thehive-test-server.ps1</c>).
    /// </remarks>
    public sealed class TheHiveConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100;

        private const int MostText = 1048576; // a description or a message: TheHive takes Markdown; a megabyte is far beyond any

        private static readonly string[] Known =
        {
            "alerts", "alert", "alert_create", "alert_update", "alert_promote", "alert_merge", "alert_delete",
            "cases", "case", "case_create", "case_update", "case_delete",
            "comments", "comment_add", "comment_update", "comment_delete",
            "tasks", "task", "task_create", "task_update", "task_delete", "task_logs", "task_log_add",
            "observables", "observable", "observable_create", "observable_update", "observable_delete",
        };

        private static readonly string[] Severities = { "LOW", "MEDIUM", "HIGH", "CRITICAL" }; // 1..4

        private static readonly string[] Tlps = { "CLEAR", "GREEN", "AMBER", "AMBER+STRICT", "RED" }; // 0..4; WHITE is CLEAR

        private static readonly string[] Paps = { "CLEAR", "GREEN", "AMBER", "RED" }; // 0..3; WHITE is CLEAR

        private static readonly string[] TaskStatuses = { "Waiting", "InProgress", "Completed", "Cancel" };

        private static readonly string[] ImpactStatuses = { "NoImpact", "WithImpact", "NotApplicable" };

        private static readonly Regex IdPattern = new("^~[0-9]{1,19}$", RegexOptions.CultureInvariant);

        private static readonly Regex StatusPattern = new("^[A-Za-z][A-Za-z0-9 _-]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex DataTypePattern = new("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "thehive";

        public string DisplayName => "TheHive";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (your user's Settings - API key)" };

        public string EndpointExample => "https://thehive.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://strangebee.com/thehive/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.strangebee.com/thehive/api-docs/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        // Markdown over several lines: the step's Query box (the query parameter), when the field is not given.
        private static ConnectorConfigField Long(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Multiline, Required: required, Description: description + " Over several lines from the step's Query box.");

        private static ConnectorConfigField Max(string what) => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: $"How many {what}, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField AlertField = Field("alert", "Alert", "Its id - ~4309136.", required: true);

        private static readonly ConnectorConfigField CaseField = Field("case", "Case", "Its id (~4415736) or its number (2).", required: true);

        private static readonly ConnectorConfigField TaskField = Field("task", "Task", "Its id - ~8323120.", required: true);

        private static readonly ConnectorConfigField ObservableField = Field("observable", "Observable", "Its id - ~4153448.", required: true);

        private static readonly ConnectorConfigField CommentField = Field("comment", "Comment", "Its id - ~4034728.", required: true);

        private static readonly ConnectorConfigField OnField = Field("on", "On", "case (the default) or alert.");

        private static readonly ConnectorConfigField OnIdField = Field("id", "Case or alert", "The case's id or number, or the alert's id.", required: true);

        private static ConnectorConfigField[] Marks(params ConnectorConfigField[] first) => first.Concat(new[]
        {
            Field("severity", "Severity", "1-4 or LOW, MEDIUM, HIGH, CRITICAL."),
            Field("tlp", "TLP", "0-4 or CLEAR, GREEN, AMBER, AMBER+STRICT, RED."),
            Field("pap", "PAP", "0-3 or CLEAR, GREEN, AMBER, RED."),
            Field("tags", "Tags", "By commas - phishing, kafe."),
        }).ToArray();

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("alerts", "Search alerts", "n8n's Alert - Search.")
            {
                Parameters = new(new[]
                {
                    Field("title", "Title", "Alerts whose title contains it."), Field("status", "Status", "New, InProgress, Ignored, Imported - as your TheHive names them."),
                    Field("severity", "Severity", "1-4 or LOW, MEDIUM, HIGH, CRITICAL."), Field("tag", "Tag", "Alerts with this tag."),
                    Field("source", "Source", "Alerts from this source."), Field("type", "Type", "Alerts of this type."), Max("alerts"),
                }),
            },
            new ConnectorOperation("alert", "Get an alert", "n8n's Alert - Get.") { Parameters = new(new[] { AlertField }) },
            new ConnectorOperation("alert_create", "Create an alert", "n8n's Alert - Create.")
            {
                Parameters = new(Marks(
                    Field("title", "Title", "Phishing against the till.", required: true), Long("description", "Description", "Markdown.", required: true),
                    Field("type", "Type", "phishing.", required: true), Field("source", "Source", "kassa.", required: true),
                    Field("source_ref", "Source reference", "Unique per type and source - kafe-2026-10-22-1.", required: true),
                    Field("date", "Date", "When it happened - 2026-10-22T08:15:00Z. Empty: now."))),
            },
            new ConnectorOperation("alert_update", "Update an alert", "n8n's Alert - Update and Update Status.")
            {
                Parameters = new(Marks(AlertField, Field("title", "Title", "A new title."), Long("description", "Description", "A new description."), Field("status", "Status", "New, InProgress, Ignored - as your TheHive names them."))),
            },
            new ConnectorOperation("alert_promote", "Promote an alert to a case", "n8n's Alert - Promote.") { Parameters = new(new[] { AlertField }) },
            new ConnectorOperation("alert_merge", "Merge an alert into a case", "n8n's Alert - Merge Into Case.") { Parameters = new(new[] { AlertField, CaseField }) },
            new ConnectorOperation("alert_delete", "Delete an alert", "n8n's Alert - Delete.") { Parameters = new(new[] { AlertField }) },
            new ConnectorOperation("cases", "Search cases", "n8n's Case - Search.")
            {
                Parameters = new(new[]
                {
                    Field("title", "Title", "Cases whose title contains it."), Field("status", "Status", "New, InProgress, TruePositive - as your TheHive names them."),
                    Field("severity", "Severity", "1-4 or LOW, MEDIUM, HIGH, CRITICAL."), Field("tag", "Tag", "Cases with this tag."),
                    Field("assignee", "Assignee", "Cases assigned to this login."), Max("cases"),
                }),
            },
            new ConnectorOperation("case", "Get a case", "n8n's Case - Get.") { Parameters = new(new[] { CaseField }) },
            new ConnectorOperation("case_create", "Create a case", "n8n's Case - Create.")
            {
                Parameters = new(Marks(Field("title", "Title", "Phishing against the till.", required: true), Long("description", "Description", "Markdown.", required: true),
                    Field("assignee", "Assignee", "A login - ada@kafe.example."), Field("flag", "Flagged", "true or false."))),
            },
            new ConnectorOperation("case_update", "Update a case", "n8n's Case - Update.")
            {
                Parameters = new(Marks(CaseField, Field("title", "Title", "A new title."), Long("description", "Description", "A new description."),
                    Field("status", "Status", "InProgress, TruePositive, FalsePositive, Duplicated - as your TheHive names them."),
                    Field("summary", "Summary", "Closing a case: what was found."), Field("impact_status", "Impact", "NoImpact, WithImpact or NotApplicable."),
                    Field("assignee", "Assignee", "A login."), Field("flag", "Flagged", "true or false."))),
            },
            new ConnectorOperation("case_delete", "Delete a case", "n8n's Case - Delete.") { Parameters = new(new[] { CaseField }) },
            new ConnectorOperation("comments", "Get comments", "n8n's Comment - Search.") { Parameters = new(new[] { OnField, OnIdField, Max("comments") }) },
            new ConnectorOperation("comment_add", "Add a comment", "n8n's Comment - Add.") { Parameters = new(new[] { OnField, OnIdField, Long("message", "Message", "Markdown.", required: true) }) },
            new ConnectorOperation("comment_update", "Update a comment", "n8n's Comment - Update.") { Parameters = new(new[] { CommentField, Long("message", "Message", "Markdown.", required: true) }) },
            new ConnectorOperation("comment_delete", "Delete a comment", "n8n's Comment - Delete.") { Parameters = new(new[] { CommentField }) },
            new ConnectorOperation("tasks", "Get a case's tasks", "n8n's Task - Search.") { Parameters = new(new[] { CaseField, Max("tasks") }) },
            new ConnectorOperation("task", "Get a task", "n8n's Task - Get.") { Parameters = new(new[] { TaskField }) },
            new ConnectorOperation("task_create", "Create a task", "n8n's Task - Create.")
            {
                Parameters = new(new[]
                {
                    CaseField, Field("title", "Title", "Block the sender.", required: true), Long("description", "Description", "Markdown."),
                    Field("group", "Group", "default."), Field("assignee", "Assignee", "A login."), Field("flag", "Flagged", "true or false."),
                    Field("due", "Due", "2026-10-23T12:00:00Z."), Field("mandatory", "Mandatory", "true or false."),
                }),
            },
            new ConnectorOperation("task_update", "Update a task", "n8n's Task - Update.")
            {
                Parameters = new(new[]
                {
                    TaskField, Field("title", "Title", "A new title."), Long("description", "Description", "A new description."),
                    Field("status", "Status", "Waiting, InProgress, Completed or Cancel."), Field("assignee", "Assignee", "A login."),
                    Field("flag", "Flagged", "true or false."), Field("due", "Due", "2026-10-23T12:00:00Z."),
                }),
            },
            new ConnectorOperation("task_delete", "Delete a task", "n8n's Task - Delete.") { Parameters = new(new[] { TaskField }) },
            new ConnectorOperation("task_logs", "Get a task's logs", "n8n's Log - Search.") { Parameters = new(new[] { TaskField, Max("logs") }) },
            new ConnectorOperation("task_log_add", "Add a task log", "n8n's Log - Create.") { Parameters = new(new[] { TaskField, Long("message", "Message", "Markdown.", required: true) }) },
            new ConnectorOperation("observables", "Get observables", "n8n's Observable - Search.") { Parameters = new(new[] { OnField, OnIdField, Field("data_type", "Data type", "Only these - ip, domain, url."), Max("observables") }) },
            new ConnectorOperation("observable", "Get an observable", "n8n's Observable - Get.") { Parameters = new(new[] { ObservableField }) },
            new ConnectorOperation("observable_create", "Create an observable", "n8n's Observable - Create.")
            {
                Parameters = new(Marks(OnField, OnIdField, Field("data_type", "Data type", "ip, domain, url, hash, mail - as your TheHive names them.", required: true),
                    Field("data", "Data", "203.0.113.7.", required: true), Field("message", "Message", "Why it is here."),
                    Field("ioc", "IOC", "true or false."), Field("sighted", "Sighted", "true or false."))),
            },
            new ConnectorOperation("observable_update", "Update an observable", "n8n's Observable - Update.")
            {
                Parameters = new(Marks(ObservableField, Field("message", "Message", "A new message."), Field("ioc", "IOC", "true or false."), Field("sighted", "Sighted", "true or false."))),
            },
            new ConnectorOperation("observable_delete", "Delete an observable", "n8n's Observable - Delete.") { Parameters = new(new[] { ObservableField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var me = await session.SendAsync(HttpMethod.Get, "/user/current", null, cancellationToken) as JsonObject;
                return Text(me?["login"]) is { } login
                    ? ConnectionTestResult.Ok($"TheHive answers - the key is {login}, in {Text(me!["organisation"]) ?? "its organisation"}.")
                    : ConnectionTestResult.Fail("TheHive answered without the key's user - check the address: a sign-in page or a proxy answers so.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "alerts").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"TheHive has no operation '{context.OperationName}' - alerts, alert, alert_create, alert_update, alert_promote, alert_merge, alert_delete, cases, case, case_create, case_update, case_delete, comments, comment_add, comment_update, comment_delete, tasks, task, task_create, task_update, task_delete, task_logs, task_log_add, observables, observable, observable_create, observable_update or observable_delete.");
            var p = context.Parameters;

            // Everything held to TheHive's shape before anything is sent.
            var call = Plan(operation, p);
            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            if (call.Query is not null)
            {
                var read = 0;
                while (read < call.Max)
                {
                    var take = Math.Min(PageSize, call.Max - read);
                    var query = new JsonArray(call.Query.Select(stage => stage.DeepClone()).ToArray());
                    query.Add(new JsonObject { ["_name"] = "page", ["from"] = read, ["to"] = read + take });
                    var answer = await session.SendAsync(HttpMethod.Post, "/query" + (call.Name is null ? string.Empty : "?name=" + call.Name), new JsonObject { ["query"] = query }, cancellationToken);
                    var found = answer as JsonArray ?? throw new InvalidOperationException("TheHive answered with what is not a list - check the address: a sign-in page or a proxy answers so.");
                    foreach (var item in found.OfType<JsonObject>())
                    {
                        yield return Record(item);
                        read++;
                    }

                    if (found.Count < take)
                    {
                        break;
                    }
                }

                yield break;
            }

            var result = await session.SendAsync(call.Method, call.Route, call.Body, cancellationToken);
            switch (result)
            {
                case JsonObject item:
                    yield return Record(item);
                    break;
                case JsonArray items: // an observable made answers as a list
                    foreach (var made in items.OfType<JsonObject>())
                    {
                        yield return Record(made);
                    }

                    break;
                default: // a change or a delete: TheHive answers 204 with nothing
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = call.Subject, ["done"] = operation });
                    break;
            }
        }

        private sealed record Call(HttpMethod Method, string Route, JsonObject? Body, string? Subject, JsonArray? Query = null, string? Name = null, int Max = DefaultMax);

        private static Call Plan(string operation, IReadOnlyDictionary<string, string> p)
        {
            switch (operation)
            {
                case "alerts":
                    {
                        var filters = new List<JsonObject>();
                        Like(filters, p, "title");
                        Equal(filters, "status", Status(p, "status"));
                        if (Values.Get(p, "severity") is not null)
                        {
                            Equal(filters, "severity", Level(p, "severity", Severities, 1));
                        }

                        Equal(filters, "tags", Line(p, "tag", 128));
                        Equal(filters, "source", Line(p, "source", 512));
                        Equal(filters, "type", Line(p, "type", 512));
                        return Search("listAlert", "alerts", filters, p);
                    }

                case "alert":
                    {
                        var id = Id(p, "alert", "an alert");
                        return new Call(HttpMethod.Get, "/alert/" + id, null, id);
                    }

                case "alert_create":
                    {
                        var body = new JsonObject
                        {
                            ["type"] = Required(p, "type", "Give the alert's type - phishing (type).", 512),
                            ["source"] = Required(p, "source", "Give the alert's source - kassa (source).", 512),
                            ["sourceRef"] = Required(p, "source_ref", "Give the alert's source reference, unique for its type and source - kafe-2026-10-22-1 (source_ref).", 512),
                            ["title"] = Required(p, "title", "Give the alert's title (title).", 512),
                            ["description"] = Body(p, "description", true, "Give the alert's description (description)."),
                        };
                        Marked(body, p);
                        if (Values.Get(p, "date") is not null)
                        {
                            body["date"] = Date(p, "date");
                        }

                        return new Call(HttpMethod.Post, "/alert", body, null);
                    }

                case "alert_update":
                    {
                        var id = Id(p, "alert", "an alert");
                        var body = Changes(p, "title", "description");
                        if (Status(p, "status") is { } status)
                        {
                            body["status"] = status;
                        }

                        return new Call(HttpMethod.Patch, "/alert/" + id, Something(body, "an alert"), id);
                    }

                case "alert_promote":
                    {
                        var id = Id(p, "alert", "an alert");
                        return new Call(HttpMethod.Post, $"/alert/{id}/case", new JsonObject(), id);
                    }

                case "alert_merge":
                    {
                        var id = Id(p, "alert", "an alert");
                        return new Call(HttpMethod.Post, $"/alert/{id}/merge/{Case(p, "case")}", new JsonObject(), id);
                    }

                case "alert_delete":
                    {
                        var id = Id(p, "alert", "an alert");
                        return new Call(HttpMethod.Delete, "/alert/" + id, null, id);
                    }

                case "cases":
                    {
                        var filters = new List<JsonObject>();
                        Like(filters, p, "title");
                        Equal(filters, "status", Status(p, "status"));
                        if (Values.Get(p, "severity") is not null)
                        {
                            Equal(filters, "severity", Level(p, "severity", Severities, 1));
                        }

                        Equal(filters, "tags", Line(p, "tag", 128));
                        Equal(filters, "assignee", Login(p, "assignee"));
                        return Search("listCase", "cases", filters, p);
                    }

                case "case":
                    {
                        var id = Case(p, "case");
                        return new Call(HttpMethod.Get, "/case/" + id, null, id);
                    }

                case "case_create":
                    {
                        var body = new JsonObject
                        {
                            ["title"] = Required(p, "title", "Give the case's title (title).", 512),
                            ["description"] = Body(p, "description", true, "Give the case's description (description)."),
                        };
                        Marked(body, p);
                        Optional(body, "assignee", Login(p, "assignee"));
                        Optional(body, "flag", Flag(p, "flag"));
                        return new Call(HttpMethod.Post, "/case", body, null);
                    }

                case "case_update":
                    {
                        var id = Case(p, "case");
                        var body = Changes(p, "title", "description");
                        Optional(body, "status", Status(p, "status"));
                        if (Values.Get(p, "summary") is not null)
                        {
                            body["summary"] = Body(p, "summary", false, string.Empty);
                        }

                        if (Values.Get(p, "impact_status") is { } impact)
                        {
                            body["impactStatus"] = ImpactStatuses.FirstOrDefault(known => string.Equals(known, impact, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException($"'{impact}' is not NoImpact, WithImpact or NotApplicable (impact_status).");
                        }

                        Optional(body, "assignee", Login(p, "assignee"));
                        Optional(body, "flag", Flag(p, "flag"));
                        return new Call(HttpMethod.Patch, "/case/" + id, Something(body, "a case"), id);
                    }

                case "case_delete":
                    {
                        var id = Case(p, "case");
                        return new Call(HttpMethod.Delete, "/case/" + id, null, id);
                    }

                case "comments":
                    {
                        var (getter, id) = On(p);
                        return new Call(HttpMethod.Post, string.Empty, null, id, new JsonArray(new JsonObject { ["_name"] = getter, ["idOrName"] = id }, new JsonObject { ["_name"] = "comments" }, Newest()), null, Most(p));
                    }

                case "comment_add":
                    {
                        var (getter, id) = On(p);
                        return new Call(HttpMethod.Post, $"/{(getter == "getCase" ? "case" : "alert")}/{id}/comment", new JsonObject { ["message"] = Body(p, "message", true, "Give the comment (message).") }, id);
                    }

                case "comment_update":
                    {
                        var id = Id(p, "comment", "a comment");
                        return new Call(HttpMethod.Patch, "/comment/" + id, new JsonObject { ["message"] = Body(p, "message", true, "Give the comment (message).") }, id);
                    }

                case "comment_delete":
                    {
                        var id = Id(p, "comment", "a comment");
                        return new Call(HttpMethod.Delete, "/comment/" + id, null, id);
                    }

                case "tasks":
                    {
                        var id = Case(p, "case");
                        return new Call(HttpMethod.Post, string.Empty, null, id, new JsonArray(new JsonObject { ["_name"] = "getCase", ["idOrName"] = id }, new JsonObject { ["_name"] = "tasks" }), null, Most(p));
                    }

                case "task":
                    {
                        var id = Id(p, "task", "a task");
                        return new Call(HttpMethod.Get, "/task/" + id, null, id);
                    }

                case "task_create":
                    {
                        var id = Case(p, "case");
                        var body = new JsonObject { ["title"] = Required(p, "title", "Give the task's title (title).", 512) };
                        TaskFields(body, p);
                        Optional(body, "mandatory", Flag(p, "mandatory"));
                        return new Call(HttpMethod.Post, $"/case/{id}/task", body, id);
                    }

                case "task_update":
                    {
                        var id = Id(p, "task", "a task");
                        var body = new JsonObject();
                        if (Values.Get(p, "title") is not null)
                        {
                            body["title"] = Line(p, "title", 512);
                        }

                        TaskFields(body, p);
                        if (Values.Get(p, "status") is { } status)
                        {
                            body["status"] = TaskStatuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException($"'{status}' is not Waiting, InProgress, Completed or Cancel (status).");
                        }

                        return new Call(HttpMethod.Patch, "/task/" + id, Something(body, "a task"), id);
                    }

                case "task_delete":
                    {
                        var id = Id(p, "task", "a task");
                        return new Call(HttpMethod.Delete, "/task/" + id, null, id);
                    }

                case "task_logs":
                    {
                        var id = Id(p, "task", "a task");
                        return new Call(HttpMethod.Post, string.Empty, null, id, new JsonArray(new JsonObject { ["_name"] = "getTask", ["idOrName"] = id }, new JsonObject { ["_name"] = "logs" }, Newest()), null, Most(p));
                    }

                case "task_log_add":
                    {
                        var id = Id(p, "task", "a task");
                        return new Call(HttpMethod.Post, $"/task/{id}/log", new JsonObject { ["message"] = Body(p, "message", true, "Give the log's message (message).") }, id);
                    }

                case "observables":
                    {
                        var (getter, id) = On(p);
                        var query = new JsonArray(new JsonObject { ["_name"] = getter, ["idOrName"] = id }, new JsonObject { ["_name"] = "observables" });
                        if (Values.Get(p, "data_type") is not null)
                        {
                            query.Add(new JsonObject { ["_name"] = "filter", ["_eq"] = new JsonObject { ["_field"] = "dataType", ["_value"] = DataType(p) } });
                        }

                        return new Call(HttpMethod.Post, string.Empty, null, id, query, null, Most(p));
                    }

                case "observable":
                    {
                        var id = Id(p, "observable", "an observable");
                        return new Call(HttpMethod.Get, "/observable/" + id, null, id);
                    }

                case "observable_create":
                    {
                        var (getter, id) = On(p);
                        if (Values.Get(p, "data_type") is null)
                        {
                            throw new InvalidOperationException("Give the observable's data type - ip, domain, url (data_type).");
                        }

                        var body = new JsonObject { ["dataType"] = DataType(p), ["data"] = Required(p, "data", "Give the observable's data - 203.0.113.7 (data).", 4096) };
                        ObservableFields(body, p);
                        return new Call(HttpMethod.Post, $"/{(getter == "getCase" ? "case" : "alert")}/{id}/observable", body, id);
                    }

                case "observable_update":
                    {
                        var id = Id(p, "observable", "an observable");
                        var body = new JsonObject();
                        ObservableFields(body, p);
                        return new Call(HttpMethod.Patch, "/observable/" + id, Something(body, "an observable"), id);
                    }

                default: // observable_delete
                    {
                        var id = Id(p, "observable", "an observable");
                        return new Call(HttpMethod.Delete, "/observable/" + id, null, id);
                    }
            }
        }

        private static Call Search(string list, string name, List<JsonObject> filters, IReadOnlyDictionary<string, string> p)
        {
            var query = new JsonArray(new JsonObject { ["_name"] = list });
            if (filters.Count > 0)
            {
                var filter = new JsonObject { ["_name"] = "filter" };
                if (filters.Count == 1)
                {
                    foreach (var (key, value) in filters[0].ToList())
                    {
                        filters[0].Remove(key);
                        filter[key] = value;
                    }
                }
                else
                {
                    filter["_and"] = new JsonArray(filters.Select(one => (JsonNode)one).ToArray());
                }

                query.Add(filter);
            }

            query.Add(Newest());
            return new Call(HttpMethod.Post, string.Empty, null, null, query, name, Most(p));
        }

        private static JsonObject Newest() => new() { ["_name"] = "sort", ["_fields"] = new JsonArray(new JsonObject { ["_createdAt"] = "desc" }) };

        private static void Like(List<JsonObject> filters, IReadOnlyDictionary<string, string> p, string name)
        {
            if (Values.Get(p, name) is not null)
            {
                filters.Add(new JsonObject { ["_like"] = new JsonObject { ["_field"] = name, ["_value"] = "*" + Line(p, name, 512) + "*" } });
            }
        }

        private static void Equal(List<JsonObject> filters, string field, object? value)
        {
            if (value is not null)
            {
                filters.Add(new JsonObject { ["_eq"] = new JsonObject { ["_field"] = field, ["_value"] = Node(value) } });
            }
        }

        private static JsonObject Changes(IReadOnlyDictionary<string, string> p, string title, string description)
        {
            var body = new JsonObject();
            if (Values.Get(p, title) is not null)
            {
                body["title"] = Line(p, title, 512);
            }

            if (Values.Get(p, description) is not null || Values.Get(p, "query") is not null)
            {
                body["description"] = Body(p, description, false, string.Empty);
            }

            Marked(body, p);
            return body;
        }

        private static JsonObject Something(JsonObject body, string what) =>
            body.Count > 0 ? body : throw new InvalidOperationException($"Give something to change on {what}.");

        // Severity, TLP, PAP and tags, wherever they are given.
        private static void Marked(JsonObject body, IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "severity") is not null)
            {
                body["severity"] = Level(p, "severity", Severities, 1);
            }

            if (Values.Get(p, "tlp") is not null)
            {
                body["tlp"] = Level(p, "tlp", Tlps, 0);
            }

            if (Values.Get(p, "pap") is not null)
            {
                body["pap"] = Level(p, "pap", Paps, 0);
            }

            if (Values.Get(p, "tags") is { } given)
            {
                var tags = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
                body["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)(tag.Length <= 128 && !tag.Any(char.IsControl) ? tag : throw new InvalidOperationException("A tag is at most 128 characters, on one line (tags)."))).ToArray());
            }
        }

        private static void TaskFields(JsonObject body, IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "description") is not null || Values.Get(p, "query") is not null)
            {
                body["description"] = Body(p, "description", false, string.Empty);
            }

            Optional(body, "group", Line(p, "group", 128));
            Optional(body, "assignee", Login(p, "assignee"));
            Optional(body, "flag", Flag(p, "flag"));
            if (Values.Get(p, "due") is not null)
            {
                body["dueDate"] = Date(p, "due");
            }
        }

        private static void ObservableFields(JsonObject body, IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "message") is not null)
            {
                body["message"] = Body(p, "message", false, string.Empty);
            }

            Optional(body, "ioc", Flag(p, "ioc"));
            Optional(body, "sighted", Flag(p, "sighted"));
            Marked(body, p);
            body.Remove("severity"); // an observable has none
            if (Values.Get(p, "severity") is not null)
            {
                throw new InvalidOperationException("An observable has no severity - its TLP, PAP, IOC and sighted say how it is handled (severity).");
            }
        }

        private static void Optional(JsonObject body, string name, object? value)
        {
            if (value is not null)
            {
                body[name] = Node(value);
            }
        }

        // A level by number or by name: severity 1-4, TLP 0-4, PAP 0-3; WHITE for CLEAR, as TheHive took it before.
        // A plain value as JSON - JsonValue.Create(object) would need the serializer's type information for a boxed one.
        private static JsonNode Node(object value) => value switch
        {
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.GetType().Name, "Not a plain value."),
        };

        private static int Level(IReadOnlyDictionary<string, string> p, string name, string[] names, int first)
        {
            var given = Values.Get(p, name)!;
            if (int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= first && number < first + names.Length)
            {
                return number;
            }

            var named = given.ToUpperInvariant() == "WHITE" ? "CLEAR" : given.ToUpperInvariant();
            var at = Array.IndexOf(names, named);
            return at >= 0 ? at + first : throw new InvalidOperationException($"'{given}' is not {first}-{first + names.Length - 1} or {string.Join(", ", names)} ({name}).");
        }

        private static string? Status(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? StatusPattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a status - New, InProgress, Ignored ({name}).") : null;

        private static string DataType(IReadOnlyDictionary<string, string> p)
        {
            var given = Values.Get(p, "data_type")!;
            return DataTypePattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a data type - ip, domain, url (data_type).");
        }

        private static string? Login(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? given.Length <= 254 && given.Contains('@') && !given.Any(char.IsWhiteSpace) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a login - ada@kafe.example ({name}).") : null;

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

        private static long Date(IReadOnlyDictionary<string, string> p, string name)
        {
            var given = Values.Get(p, name)!;
            return DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                ? when.ToUnixTimeMilliseconds()
                : throw new InvalidOperationException($"'{given}' is not a date - 2026-10-22T08:15:00Z ({name}).");
        }

        private static string? Line(IReadOnlyDictionary<string, string> p, string name, int most) =>
            Values.Get(p, name) is { } given ? given.Length <= most && !given.Any(char.IsControl) ? given : throw new InvalidOperationException($"'{(given.Length > 40 ? given[..40] + "..." : given)}' is more than {most} characters or more than one line ({name}).") : null;

        private static string Required(IReadOnlyDictionary<string, string> p, string name, string missing, int most) =>
            Line(p, name, most) ?? throw new InvalidOperationException(missing);

        private static string Body(IReadOnlyDictionary<string, string> p, string name, bool required, string missing)
        {
            var given = Values.Get(p, name) ?? Values.Get(p, "query"); // over several lines: the Query box
            if (given is null)
            {
                return required ? throw new InvalidOperationException(missing) : string.Empty;
            }

            return given.Length <= MostText ? given : throw new InvalidOperationException($"A {name} is at most a megabyte ({name}).");
        }

        private static string Id(IReadOnlyDictionary<string, string> p, string name, string what)
        {
            var given = Values.Get(p, name) ?? throw new InvalidOperationException($"Give {what} - its id, ~4309136 ({name}).");
            return IdPattern.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what}'s id - ~4309136 ({name}).");
        }

        // A case by its id (~4415736) or its number (2 or #2) - TheHive finds a case by either at the same address.
        private static string Case(IReadOnlyDictionary<string, string> p, string name)
        {
            var given = Values.Get(p, name) ?? throw new InvalidOperationException($"Give the case - its id (~4415736) or its number (2) ({name}).");
            var number = given.StartsWith('#') ? given[1..] : given;
            return IdPattern.IsMatch(given) ? given
                : number.Length is > 0 and <= 18 && number.All(char.IsAsciiDigit) && number[0] != '0' ? number
                : throw new InvalidOperationException($"'{given}' is not a case's id or number - ~4415736 or 2 ({name}).");
        }

        private static (string Getter, string Id) On(IReadOnlyDictionary<string, string> p)
        {
            var on = (Values.Get(p, "on") ?? "case").ToLowerInvariant();
            return on switch
            {
                "case" => ("getCase", Case(p, "id")),
                "alert" => ("getAlert", Id(p, "id", "an alert")),
                _ => throw new InvalidOperationException($"'{Values.Get(p, "on")}' is not case or alert (on)."),
            };
        }

        private static int Most(IReadOnlyDictionary<string, string> p) =>
            Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;

        // An object as a record: its plain values as they are, a list or an object as JSON, its id as id.
        private static ConnectorRecord Record(JsonObject data)
        {
            // TheHive's own fields without their underscore - _type as kind, beside an alert's own type; the key's permissions are not the item's.
            var fields = data.Where(pair => pair.Key != "_id" && pair.Key != "userPermissions").ToDictionary(pair => pair.Key == "_type" ? "kind" : pair.Key.TrimStart('_'), pair => pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                var other => (object?)other.ToJsonString(),
            }, StringComparer.Ordinal);
            fields["id"] = Text(data["_id"]);
            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string key;

            private Session(HttpClient client, string api, string key)
            {
                this.client = client;
                this.api = api;
                this.key = key;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give TheHive's address - https://thehive.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                foreach (var suffix in new[] { "/api/v1", "/api" })
                {
                    if (root.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        root = root[..^suffix.Length]; // the API's own address given: TheHive's
                        break;
                    }
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not TheHive's address - https://thehive.kafe.example, with no query.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The TheHive connection has no API key.");
                return new Session(egress.CreateHttpClient(timeout), root + "/api/v1", key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the key as a bearer token; TheHive's JSON (null for its 204), or its status, message and refused fields.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    request.Headers.Accept.ParseAdd("application/json");
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
                    throw new InvalidOperationException("TheHive answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    if (json is null && response.StatusCode != System.Net.HttpStatusCode.NoContent)
                    {
                        throw new InvalidOperationException("TheHive answered with nothing - check the address: a sign-in page or a proxy answers so.");
                    }

                    return json;
                }

                var status = (int)response.StatusCode;
                var said = Text(json?["message"]);
                var fields = json?["errors"] is JsonArray errors
                    ? string.Join("; ", errors.OfType<JsonObject>().Select(error => $"{Text(error["path"])?.TrimStart('.')} - {Text(error["error"])}"))
                    : null;
                var words = string.Join(": ", new[] { said, fields }.Where(part => !string.IsNullOrEmpty(part)));
                words = words.Length > 300 ? words[..300] : words;
                throw new InvalidOperationException($"TheHive answered HTTP {status}{(words.Length == 0 ? "." : ": " + words + (words.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 401 ? " - check the API key." : status == 403 ? " - the key's user may not do this; check its profile." : string.Empty));
            }
        }
    }
}
