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

namespace Stomme.Modules.Connectors.PagerDuty
{
    /// <summary>
    /// PagerDuty (BL-229, n8n's PagerDuty node: incidents created, got, read and updated, their notes, the log entries, a user):
    /// PagerDuty's REST API v2 with an API token, over the host's egress - a write sent from a PagerDuty user's e-mail, the
    /// connection's or the step's. A test names how many abilities the account has.
    /// </summary>
    /// <remarks>
    /// <c>incidents</c> (by status, urgency, service, team, user, time, incident key, sorted - page by page up to <c>max</c>),
    /// <c>incident</c>, <c>create_incident</c>, <c>update_incident</c> (acknowledged, resolved, reassigned, escalated),
    /// <c>notes</c>, <c>create_note</c>, <c>log_entries</c>, <c>log_entry</c>, <c>user</c>, <c>users</c>, <c>services</c>
    /// and <c>oncalls</c> - who is on call. A record is its fields as PagerDuty gives them, a reference - a service, a priority -
    /// as its summary and its id, a list or an object as its JSON. Every id, status, urgency, time and address held to its shape
    /// before anything is sent. A throttled call waits as PagerDuty says; a failure is thrown with its status, PagerDuty's code,
    /// word and errors.
    /// </remarks>
    public sealed class PagerDutyConnector : IConnector, IConnectorExtractor
    {
        public const string IncidentsOperation = "incidents";

        public const string IncidentOperation = "incident";

        public const string CreateIncidentOperation = "create_incident";

        public const string UpdateIncidentOperation = "update_incident";

        public const string NotesOperation = "notes";

        public const string CreateNoteOperation = "create_note";

        public const string LogEntriesOperation = "log_entries";

        public const string LogEntryOperation = "log_entry";

        public const string UserOperation = "user";

        public const string UsersOperation = "users";

        public const string ServicesOperation = "services";

        public const string OnCallsOperation = "oncalls";

        public const string DefaultEndpoint = "https://api.pagerduty.com";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // PagerDuty's ids: PXXXXXX, Q0RIJJZL24RC6W.
        private static readonly Regex Id = new("^[A-Z0-9]{2,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Sort = new("^[a-z_]{1,40}(:(asc|desc))?(,[a-z_]{1,40}(:(asc|desc))?){0,1}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { IncidentsOperation, IncidentOperation, CreateIncidentOperation, UpdateIncidentOperation, NotesOperation, CreateNoteOperation, LogEntriesOperation, LogEntryOperation, UserOperation, UsersOperation, ServicesOperation, OnCallsOperation };

        private static readonly string[] IncidentIncludes = { "acknowledgers", "agents", "assignees", "conference_bridge", "escalation_policies", "first_trigger_log_entries", "priorities", "services", "teams", "users" };

        public string ConnectorType => "pagerduty";

        public string DisplayName => "PagerDuty";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement, ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://www.pagerduty.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.pagerduty.com/api-reference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: false, Description: "A PagerDuty user's e-mail - who an incident, a change or a note comes from, unless a step says."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField FromField = Field("from", "From", "A PagerDuty user's e-mail - the connection's when left empty.");

        private static readonly ConnectorConfigField IncidentField = Field("id", "Incident", "The incident's id.", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(IncidentsOperation, "Get incidents", "The account's incidents - n8n's PagerDuty Incident Get Many.")
            {
                Parameters = Schema(
                    Field("statuses", "Statuses", "Comma-separated - triggered, acknowledged, resolved."),
                    Field("urgencies", "Urgencies", "Comma-separated - high, low."),
                    Field("services", "Services", "Comma-separated service ids."),
                    Field("teams", "Teams", "Comma-separated team ids."),
                    Field("users", "Users", "Comma-separated ids of users they are assigned to."),
                    Field("since", "Since", "From when - 2026-10-01T00:00:00Z."),
                    Field("until", "Until", "Until when."),
                    Field("all", "Every time", "true: every incident, whenever - since and until left out.", ConnectorConfigFieldType.Boolean),
                    Field("incident_key", "Incident key", "The de-duplication key an incident was made with."),
                    Field("sort", "Sort", "incident_number, created_at, resolved_at or urgency, :desc for descending - created_at:desc."),
                    Field("include", "Include", "Comma-separated - assignees, services, priorities, teams, users and the like - filled in."),
                    Field("time_zone", "Time zone", "The time zone the times are given in - Europe/Stockholm."),
                    MaxField),
            },
            new ConnectorOperation(IncidentOperation, "Get an incident", "n8n's PagerDuty Incident Get.") { Parameters = Schema(IncidentField) },
            new ConnectorOperation(CreateIncidentOperation, "Create an incident", "n8n's PagerDuty Incident Create.")
            {
                Parameters = Schema(
                    Field("title", "Title", "What happened.", required: true),
                    Field("service", "Service", "The service's id.", required: true),
                    FromField,
                    Field("urgency", "Urgency", "high or low."),
                    Field("incident_key", "Incident key", "A de-duplication key - an incident open with it is not made twice."),
                    Field("details", "Details", "What more there is to say.", ConnectorConfigFieldType.Multiline),
                    Field("priority", "Priority", "A priority's id."),
                    Field("escalation_policy", "Escalation policy", "An escalation policy's id - the service's when left empty."),
                    Field("assignees", "Assignees", "Comma-separated ids of users to assign it to."),
                    Field("conference_number", "Conference number", "A phone number to dial in on."),
                    Field("conference_url", "Conference address", "A conference call's address.")),
            },
            new ConnectorOperation(UpdateIncidentOperation, "Update an incident", "Acknowledges, resolves, reassigns or escalates it - n8n's PagerDuty Incident Update.")
            {
                Parameters = Schema(
                    IncidentField,
                    FromField,
                    Field("status", "Status", "acknowledged or resolved."),
                    Field("resolution", "Resolution", "How it was resolved - a note on it."),
                    Field("title", "Title", "Its new title."),
                    Field("urgency", "Urgency", "high or low."),
                    new ConnectorConfigField("escalation_level", "Escalation level", ConnectorConfigFieldType.Integer, Required: false, Description: "The level to escalate it to."),
                    Field("priority", "Priority", "A priority's id."),
                    Field("escalation_policy", "Escalation policy", "An escalation policy's id."),
                    Field("assignees", "Assignees", "Comma-separated ids of users to reassign it to."),
                    Field("conference_number", "Conference number", "A phone number to dial in on."),
                    Field("conference_url", "Conference address", "A conference call's address.")),
            },
            new ConnectorOperation(NotesOperation, "Get an incident's notes", "n8n's PagerDuty Incident Note Get Many.") { Parameters = Schema(IncidentField) },
            new ConnectorOperation(CreateNoteOperation, "Add a note", "n8n's PagerDuty Incident Note Create.") { Parameters = Schema(IncidentField, Field("content", "Note", "The note.", ConnectorConfigFieldType.Multiline, required: true), FromField) },
            new ConnectorOperation(LogEntriesOperation, "Get log entries", "The account's log entries - n8n's PagerDuty Log Entry Get Many.")
            {
                Parameters = Schema(
                    Field("since", "Since", "From when."),
                    Field("until", "Until", "Until when."),
                    Field("overview", "Overview", "true: only the important changes to an incident.", ConnectorConfigFieldType.Boolean),
                    Field("include", "Include", "Comma-separated - incidents, services, channels, teams - filled in."),
                    Field("time_zone", "Time zone", "The time zone the times are given in."),
                    MaxField),
            },
            new ConnectorOperation(LogEntryOperation, "Get a log entry", "n8n's PagerDuty Log Entry Get.") { Parameters = Schema(Field("id", "Log entry", "Its id.", required: true)) },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's PagerDuty User Get.") { Parameters = Schema(Field("id", "User", "Its id.", required: true)) },
            new ConnectorOperation(UsersOperation, "Get users", "The account's users.") { Parameters = Schema(Field("query", "Search", "Part of a name or an e-mail."), MaxField) },
            new ConnectorOperation(ServicesOperation, "Get services", "The account's services.") { Parameters = Schema(Field("query", "Search", "Part of a name."), MaxField) },
            new ConnectorOperation(OnCallsOperation, "Get who is on call", "The on-call entries - a schedule's, an escalation policy's or a user's.")
            {
                Parameters = Schema(
                    Field("schedules", "Schedules", "Comma-separated schedule ids."),
                    Field("escalation_policies", "Escalation policies", "Comma-separated escalation policy ids."),
                    Field("users", "Users", "Comma-separated user ids."),
                    Field("since", "Since", "From when - now when left empty."),
                    Field("until", "Until", "Until when."),
                    Field("earliest", "Earliest", "true: each one's earliest on-call only.", ConnectorConfigFieldType.Boolean),
                    MaxField),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var abilities = (await session.SendAsync(HttpMethod.Get, "/abilities", null, null, cancellationToken))?["abilities"] as JsonArray;
                var count = abilities?.Count ?? 0;
                return ConnectionTestResult.Ok($"PagerDuty: the token answers - {count} {(count == 1 ? "ability" : "abilities")} on the account.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? IncidentsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"PagerDuty has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? id = null;
            if (operation is IncidentOperation or UpdateIncidentOperation or NotesOperation or CreateNoteOperation or LogEntryOperation or UserOperation)
            {
                id = Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a PagerDuty id - capital letters and digits, PXXXXXX.") : throw new InvalidOperationException("Give the id.");
            }

            string? from = null;
            if (operation is CreateIncidentOperation or UpdateIncidentOperation or CreateNoteOperation)
            {
                from = Values.Get(p, "from") ?? Values.Get(context.Settings, "from") ?? throw new InvalidOperationException("Give who it comes from - a PagerDuty user's e-mail, here or on the connection.");
                from = Email.IsMatch(from) ? from : throw new InvalidOperationException($"'{from}' is not an e-mail address (from).");
            }

            var query = new List<string>();
            void Times()
            {
                foreach (var name in new[] { "since", "until" })
                {
                    if (Values.Get(p, name) is { } when)
                    {
                        query.Add(name + "=" + Uri.EscapeDataString(DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{when}' is not a time - 2026-10-01T00:00:00Z ({name}).")));
                    }
                }
            }

            void List(string name, string key, Func<string, bool> valid, string shape)
            {
                if (Values.Get(p, name) is { } given)
                {
                    foreach (var item in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        query.Add(key + "[]=" + (valid(item) ? item : throw new InvalidOperationException($"'{item}' is not {shape} ({name}).")));
                    }
                }
            }

            void TimeZone()
            {
                if (Values.Get(p, "time_zone") is { } zone)
                {
                    query.Add("time_zone=" + Uri.EscapeDataString(Regex.IsMatch(zone, "^[A-Za-z_]{1,30}(/[A-Za-z0-9_+-]{1,30}){0,2}$", RegexOptions.CultureInvariant) ? zone : throw new InvalidOperationException($"'{zone}' is not a time zone - Europe/Stockholm.")));
                }
            }

            switch (operation)
            {
                case IncidentsOperation:
                    List("statuses", "statuses", item => item is "triggered" or "acknowledged" or "resolved", "triggered, acknowledged or resolved");
                    List("urgencies", "urgencies", item => item is "high" or "low", "high or low");
                    List("services", "service_ids", Id.IsMatch, "a PagerDuty id");
                    List("teams", "team_ids", Id.IsMatch, "a PagerDuty id");
                    List("users", "user_ids", Id.IsMatch, "a PagerDuty id");
                    List("include", "include", IncidentIncludes.Contains, "one of " + string.Join(", ", IncidentIncludes));
                    if (Values.GetBool(p, "all", false))
                    {
                        query.Add("date_range=all");
                    }
                    else
                    {
                        Times();
                    }

                    if (Values.Get(p, "incident_key") is { } key)
                    {
                        query.Add("incident_key=" + Uri.EscapeDataString(key));
                    }

                    if (Values.Get(p, "sort") is { } sort)
                    {
                        query.Add("sort_by=" + Uri.EscapeDataString(Sort.IsMatch(sort.Replace(" ", "", StringComparison.Ordinal)) ? sort.Replace(" ", "", StringComparison.Ordinal) : throw new InvalidOperationException($"'{sort}' is not a sort - created_at:desc.")));
                    }

                    TimeZone();
                    break;
                case LogEntriesOperation:
                    List("include", "include", item => item is "incidents" or "services" or "channels" or "teams", "incidents, services, channels or teams");
                    Times();
                    if (Values.GetBool(p, "overview", false))
                    {
                        query.Add("is_overview=true");
                    }

                    TimeZone();
                    break;
                case UsersOperation:
                case ServicesOperation:
                    if (Values.Get(p, "query") is { } search)
                    {
                        query.Add("query=" + Uri.EscapeDataString(search));
                    }

                    break;
                case OnCallsOperation:
                    List("schedules", "schedule_ids", Id.IsMatch, "a PagerDuty id");
                    List("escalation_policies", "escalation_policy_ids", Id.IsMatch, "a PagerDuty id");
                    List("users", "user_ids", Id.IsMatch, "a PagerDuty id");
                    Times();
                    if (Values.GetBool(p, "earliest", false))
                    {
                        query.Add("earliest=true");
                    }

                    break;
            }

            JsonObject? body = null;
            if (operation is CreateIncidentOperation or UpdateIncidentOperation)
            {
                var incident = new JsonObject { ["type"] = operation == CreateIncidentOperation ? "incident" : "incident_reference" };
                if (operation == CreateIncidentOperation)
                {
                    incident["title"] = Values.Get(p, "title") ?? throw new InvalidOperationException("Give the incident's title.");
                    incident["service"] = Reference(Values.Get(p, "service") ?? throw new InvalidOperationException("Give the service - its id."), "service", "service_reference");
                }
                else
                {
                    if (Values.Get(p, "status") is { } status)
                    {
                        incident["status"] = status.ToLowerInvariant() is "acknowledged" or "resolved" ? status.ToLowerInvariant() : throw new InvalidOperationException($"'{status}' is not acknowledged or resolved (status).");
                    }

                    if (Values.Get(p, "resolution") is { } resolution)
                    {
                        incident["resolution"] = resolution;
                    }

                    if (Values.Get(p, "title") is { } title)
                    {
                        incident["title"] = title;
                    }

                    if (Values.Get(p, "escalation_level") is { } level)
                    {
                        incident["escalation_level"] = int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l > 0 ? l : throw new InvalidOperationException($"'{level}' is not a level above 0 (escalation_level).");
                    }
                }

                if (Values.Get(p, "urgency") is { } urgency)
                {
                    incident["urgency"] = urgency.ToLowerInvariant() is "high" or "low" ? urgency.ToLowerInvariant() : throw new InvalidOperationException($"'{urgency}' is not high or low (urgency).");
                }

                if (operation == CreateIncidentOperation && Values.Get(p, "incident_key") is { } key)
                {
                    incident["incident_key"] = key;
                }

                if (operation == CreateIncidentOperation && Values.Get(p, "details") is { } details)
                {
                    incident["body"] = new JsonObject { ["type"] = "incident_body", ["details"] = details };
                }

                if (Values.Get(p, "priority") is { } priority)
                {
                    incident["priority"] = Reference(priority, "priority", "priority_reference");
                }

                if (Values.Get(p, "escalation_policy") is { } policy)
                {
                    incident["escalation_policy"] = Reference(policy, "escalation_policy", "escalation_policy_reference");
                }

                if (Values.Get(p, "assignees") is { } assignees)
                {
                    incident["assignments"] = new JsonArray(assignees.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(user => (JsonNode?)new JsonObject { ["assignee"] = Reference(user, "assignees", "user_reference") }).ToArray());
                }

                if (Values.Get(p, "conference_number") is not null || Values.Get(p, "conference_url") is not null)
                {
                    var bridge = new JsonObject();
                    if (Values.Get(p, "conference_number") is { } dial)
                    {
                        bridge["conference_number"] = dial;
                    }

                    if (Values.Get(p, "conference_url") is { } url)
                    {
                        bridge["conference_url"] = Uri.TryCreate(url, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp) ? url : throw new InvalidOperationException($"'{url}' is not an address - https://meet.example.com/ops (conference_url).");
                    }

                    incident["conference_bridge"] = bridge;
                }

                body = new JsonObject { ["incident"] = incident };
            }

            if (operation == CreateNoteOperation)
            {
                body = new JsonObject { ["note"] = new JsonObject { ["content"] = Values.Get(p, "content") ?? throw new InvalidOperationException("Give the note.") } };
            }

            using var client = session.Client;
            switch (operation)
            {
                case IncidentsOperation:
                case LogEntriesOperation:
                case UsersOperation:
                case ServicesOperation:
                case OnCallsOperation:
                    {
                        // PagerDuty pages by offset and limit: the limit is fixed at the start, its more the end. A list comes
                        // under the operation's own name - incidents, log_entries, oncalls.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/{operation}?" + string.Join("&", query.Append($"offset={read}").Append($"limit={size}")), null, null, cancellationToken);
                            var items = (answer?[operation] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count == 0 || answer?["more"] is not JsonValue more || !more.TryGetValue<bool>(out var further) || !further)
                            {
                                yield break;
                            }
                        }
                    }

                case NotesOperation:
                    {
                        foreach (var note in ((await session.SendAsync(HttpMethod.Get, $"/incidents/{id}/notes", null, null, cancellationToken))?["notes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return Record(note);
                        }

                        yield break;
                    }

                case CreateNoteOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/incidents/{id}/notes", body, from, cancellationToken))?["note"] as JsonObject ?? new JsonObject());
                    yield break;
                case CreateIncidentOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, "/incidents", body, from, cancellationToken))?["incident"] as JsonObject ?? new JsonObject());
                    yield break;
                case UpdateIncidentOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Put, $"/incidents/{id}", body, from, cancellationToken))?["incident"] as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    {
                        var (path, key) = operation switch
                        {
                            IncidentOperation => ($"/incidents/{id}", "incident"),
                            LogEntryOperation => ($"/log_entries/{id}", "log_entry"),
                            _ => ($"/users/{id}", "user"),
                        };
                        yield return Record((await session.SendAsync(HttpMethod.Get, path, null, null, cancellationToken))?[key] as JsonObject ?? new JsonObject());
                        yield break;
                    }
            }
        }

        private static JsonObject Reference(string id, string name, string type) =>
            Id.IsMatch(id) ? new JsonObject { ["id"] = id, ["type"] = type } : throw new InvalidOperationException($"'{id}' is not a PagerDuty id - capital letters and digits ({name}).");

        // A record: its fields as PagerDuty gives them - a reference (a service, a priority, a user) as its summary and its id,
        // a list or another object as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                switch (value)
                {
                    case null:
                        record[name] = null;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                        record[name] = text;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<bool>(out var flag):
                        record[name] = flag;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<long>(out var whole):
                        record[name] = whole;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<double>(out var real):
                        record[name] = real;
                        break;
                    case JsonObject reference when reference["id"] is JsonValue && reference["summary"] is JsonValue:
                        record[name] = Text(reference["summary"]);
                        record[name + "_id"] = Text(reference["id"]);
                        break;
                    default:
                        record[name] = value.ToJsonString(Wire);
                        break;
                }
            }

            return new ConnectorRecord(record);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: PagerDuty's REST API v2 with the token, a write's sender in its From header; a throttled call
        // waiting as PagerDuty says; a failure thrown with its status, PagerDuty's code, word and errors.
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
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The PagerDuty connection has no API token."), egress.CreateHttpClient(timeout));

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string? from, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Token", "token=" + token);
                    request.Headers.Accept.ParseAdd("application/vnd.pagerduty+json;version=2");
                    if (from is not null)
                    {
                        request.Headers.TryAddWithoutValidation("From", from);
                    }

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
                    // A page that is not PagerDuty's - a sign-in page, a proxy's - read as no incidents would hide that it never was.
                    throw new InvalidOperationException($"PagerDuty answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = (json as JsonObject)?["error"] as JsonObject;
                    var code = error?["code"] is JsonValue number && number.TryGetValue<long>(out var c) ? c.ToString(CultureInfo.InvariantCulture) : null;
                    var errors = (error?["errors"] as JsonArray)?.Select(Text).Where(word => word is not null).ToList();
                    var said = Text(error?["message"]) is { } message ? errors is { Count: > 0 } ? message + " - " + string.Join("; ", errors) : message : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : $" ({code})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
