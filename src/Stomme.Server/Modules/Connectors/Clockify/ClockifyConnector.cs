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

namespace Stomme.Modules.Connectors.Clockify
{
    /// <summary>
    /// Clockify (BL-229, n8n's Clockify node: a workspace's clients, projects, tags, tasks and time entries created, got,
    /// updated and deleted, its users and the workspaces read): Clockify's API v1, an API key in <c>X-Api-Key</c>, over the
    /// host's egress. A test names whom the key signs in as.
    /// </summary>
    /// <remarks>
    /// Every resource's list, get, create, update and delete - each list page by page up to <c>max</c> - and a user's time
    /// entries over a period, the key's own user's when none is named, which n8n's node cannot read. A time entry's start and end
    /// are any date and time with its zone, sent in UTC; an update that gives no start keeps the entry's own, which Clockify
    /// requires. Every id held to its shape before anything is sent. A throttled call waits as Clockify says; a failure is thrown
    /// with its status and Clockify's word.
    /// </remarks>
    public sealed class ClockifyConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.clockify.me/api/v1";

        public const int DefaultMax = 1000;

        private const int PageSize = 200;

        private static readonly Regex ObjectId = new("^[0-9a-f]{24}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private enum Act
        {
            List,
            Get,
            Create,
            Update,
            Delete,
        }

        // Each operation: what it does, its path (its ids in braces - each a parameter of that name), and the list filters it
        // passes on as Clockify names them.
        private sealed record Spec(string Name, Act Act, string Path, string Title, string Description, params (string Parameter, string Query)[] Filters);

        private static readonly (string Parameter, string Query) ByName = ("name", "name");

        private static readonly (string Parameter, string Query) Archived = ("archived", "archived");

        private static readonly Spec[] Specs =
        {
            new("workspaces", Act.List, "/workspaces", "Get workspaces", "The workspaces the key sees - n8n's Clockify Workspace Get Many."),
            new("current_user", Act.Get, "/user", "Get the key's user", "Whom the key signs in as - its id, name and workspaces."),
            new("users", Act.List, "/workspaces/{workspace}/users", "Get users", "A workspace's users - n8n's Clockify User Get Many.", ByName, ("email", "email")),
            new("clients", Act.List, "/workspaces/{workspace}/clients", "Get clients", "n8n's Clockify Client Get Many.", ByName, Archived),
            new("get_client", Act.Get, "/workspaces/{workspace}/clients/{client}", "Get a client", "n8n's Clockify Client Get."),
            new("create_client", Act.Create, "/workspaces/{workspace}/clients", "Create a client", "n8n's Clockify Client Create."),
            new("update_client", Act.Update, "/workspaces/{workspace}/clients/{client}", "Update a client", "n8n's Clockify Client Update."),
            new("delete_client", Act.Delete, "/workspaces/{workspace}/clients/{client}", "Delete a client", "n8n's Clockify Client Delete."),
            new("projects", Act.List, "/workspaces/{workspace}/projects", "Get projects", "n8n's Clockify Project Get Many.", ByName, Archived, ("billable", "billable")),
            new("get_project", Act.Get, "/workspaces/{workspace}/projects/{project}", "Get a project", "n8n's Clockify Project Get."),
            new("create_project", Act.Create, "/workspaces/{workspace}/projects", "Create a project", "n8n's Clockify Project Create."),
            new("update_project", Act.Update, "/workspaces/{workspace}/projects/{project}", "Update a project", "n8n's Clockify Project Update."),
            new("delete_project", Act.Delete, "/workspaces/{workspace}/projects/{project}", "Delete a project", "An archived project - n8n's Clockify Project Delete."),
            new("tags", Act.List, "/workspaces/{workspace}/tags", "Get tags", "n8n's Clockify Tag Get Many.", ByName, Archived),
            new("get_tag", Act.Get, "/workspaces/{workspace}/tags/{tag}", "Get a tag", "One tag."),
            new("create_tag", Act.Create, "/workspaces/{workspace}/tags", "Create a tag", "n8n's Clockify Tag Create."),
            new("update_tag", Act.Update, "/workspaces/{workspace}/tags/{tag}", "Update a tag", "n8n's Clockify Tag Update."),
            new("delete_tag", Act.Delete, "/workspaces/{workspace}/tags/{tag}", "Delete a tag", "n8n's Clockify Tag Delete."),
            new("tasks", Act.List, "/workspaces/{workspace}/projects/{project}/tasks", "Get tasks", "A project's tasks - n8n's Clockify Task Get Many.", ByName, ("active", "is-active")),
            new("get_task", Act.Get, "/workspaces/{workspace}/projects/{project}/tasks/{task}", "Get a task", "n8n's Clockify Task Get."),
            new("create_task", Act.Create, "/workspaces/{workspace}/projects/{project}/tasks", "Create a task", "n8n's Clockify Task Create."),
            new("update_task", Act.Update, "/workspaces/{workspace}/projects/{project}/tasks/{task}", "Update a task", "n8n's Clockify Task Update."),
            new("delete_task", Act.Delete, "/workspaces/{workspace}/projects/{project}/tasks/{task}", "Delete a task", "n8n's Clockify Task Delete."),
            new("time_entries", Act.List, "/workspaces/{workspace}/user/{user}/time-entries", "Get time entries", "A user's time entries over a period - the key's own user's when none is named.", ("start", "start"), ("end", "end"), ("project", "project"), ("description", "description")),
            new("get_time_entry", Act.Get, "/workspaces/{workspace}/time-entries/{time_entry}", "Get a time entry", "n8n's Clockify Time Entry Get."),
            new("create_time_entry", Act.Create, "/workspaces/{workspace}/time-entries", "Create a time entry", "n8n's Clockify Time Entry Create."),
            new("update_time_entry", Act.Update, "/workspaces/{workspace}/time-entries/{time_entry}", "Update a time entry", "Its start kept when none is given - n8n's Clockify Time Entry Update."),
            new("delete_time_entry", Act.Delete, "/workspaces/{workspace}/time-entries/{time_entry}", "Delete a time entry", "n8n's Clockify Time Entry Delete."),
        };

        private static readonly Regex Placeholder = new("\\{([a-z_]+)\\}", RegexOptions.CultureInvariant);

        public string ConnectorType => "clockify";

        public string DisplayName => "Clockify";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://clockify.me/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.clockify.me/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        public IReadOnlyList<ConnectorOperation> Operations { get; } = Specs.Select(spec => new ConnectorOperation(spec.Name, spec.Title, spec.Description) { Parameters = Schema(spec) }).ToArray();

        private static ConnectorConfigSchema Schema(Spec spec)
        {
            var fields = new List<ConnectorConfigField>();
            foreach (Match id in Placeholder.Matches(spec.Path))
            {
                var name = id.Groups[1].Value;
                var required = !(spec.Act == Act.List && name == "user");
                fields.Add(new ConnectorConfigField(name, char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' '), ConnectorConfigFieldType.Text, Required: required, Description: required ? $"Its id - 24 hexadecimal digits." : "Its id - the key's own user when left empty."));
            }

            if (spec.Act is Act.Create or Act.Update)
            {
                fields.Add(new ConnectorConfigField(spec.Name.EndsWith("time_entry", StringComparison.Ordinal) ? "start" : "name", spec.Name.EndsWith("time_entry", StringComparison.Ordinal) ? "Start" : "Name", ConnectorConfigFieldType.Text, Required: spec.Act == Act.Create, Description: spec.Name.EndsWith("time_entry", StringComparison.Ordinal) ? "When it started, with its zone - 2026-10-13T08:00:00Z." : "Its name."));
                if (spec.Name.EndsWith("time_entry", StringComparison.Ordinal))
                {
                    fields.Add(new ConnectorConfigField("end", "End", ConnectorConfigFieldType.Text, Required: false, Description: "When it ended, with its zone - none for a running timer."));
                }

                fields.Add(new ConnectorConfigField("fields", "Other fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of any other field, as Clockify names it - {\"billable\": true, \"note\": \"Retainer\"}."));
            }

            foreach (var (parameter, _) in spec.Filters)
            {
                fields.Add(new ConnectorConfigField(parameter, char.ToUpperInvariant(parameter[0]) + parameter[1..], ConnectorConfigFieldType.Text, Required: false, Description: parameter switch
                {
                    "start" or "end" => "A date and time with its zone - 2026-10-01T00:00:00Z.",
                    "archived" or "billable" or "active" => "true or false.",
                    "project" => "A project's id.",
                    _ => "Its text, or part of it.",
                }));
            }

            if (spec.Act == Act.List)
            {
                fields.Add(new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) });
            }

            return new ConnectorConfigSchema(fields);
        }

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken);
                return ConnectionTestResult.Ok($"Clockify: signed in as {Text(me?["name"]) ?? Text(me?["email"]) ?? "the key's user"}.");
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
            var spec = Specs.FirstOrDefault(known => string.Equals(known.Name, (context.OperationName ?? "workspaces").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Clockify has no operation '{context.OperationName}' - {string.Join(", ", Specs[..^1].Select(known => known.Name))} or {Specs[^1].Name}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent: the ids in the path, the filters, the body.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var ids = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (Match placeholder in Placeholder.Matches(spec.Path))
            {
                var name = placeholder.Groups[1].Value;
                ids[name] = Values.Get(p, name) is { } given
                    ? ObjectId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {name.Replace('_', ' ')}'s id - 24 hexadecimal digits.")
                    : spec.Act == Act.List && name == "user" ? null : throw new InvalidOperationException($"Give the {name.Replace('_', ' ')} - its id.");
            }

            var query = new List<string>();
            foreach (var (parameter, name) in spec.Filters)
            {
                if (Values.Get(p, parameter) is not { } value)
                {
                    continue;
                }

                query.Add(name + "=" + Uri.EscapeDataString(parameter switch
                {
                    "start" or "end" => Instant(value, parameter),
                    "archived" or "billable" or "active" => value.ToLowerInvariant() is "true" or "false" ? value.ToLowerInvariant() : throw new InvalidOperationException($"'{value}' is not true or false ({parameter})."),
                    "project" => ObjectId.IsMatch(value) ? value : throw new InvalidOperationException($"'{value}' is not a project's id - 24 hexadecimal digits."),
                    _ => value.Length <= 200 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"The {parameter} is one line of up to 200 characters."),
                }));
            }

            JsonObject? body = null;
            var entry = spec.Name.EndsWith("time_entry", StringComparison.Ordinal);
            if (spec.Act is Act.Create or Act.Update)
            {
                body = Values.Get(p, "fields") is { } fields
                    ? Parse(fields) as JsonObject ?? throw new InvalidOperationException("The other fields are not a JSON object - {\"billable\": true}.")
                    : new JsonObject();
                if (entry)
                {
                    if (Values.Get(p, "start") is { } start)
                    {
                        body["start"] = Instant(start, "start");
                    }
                    else if (spec.Act == Act.Create)
                    {
                        throw new InvalidOperationException("Give the start - when it started, with its zone.");
                    }

                    if (Values.Get(p, "end") is { } end)
                    {
                        body["end"] = Instant(end, "end");
                    }
                }
                else if (Values.Get(p, "name") is { } name)
                {
                    body["name"] = name;
                }
                else if (spec.Act == Act.Create)
                {
                    throw new InvalidOperationException("Give the name.");
                }

                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give what to change - a name, a start or other fields.");
                }
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            if (ids.ContainsKey("user") && ids["user"] is null)
            {
                // The key's own user, as Clockify names it.
                ids["user"] = Text((await session.SendAsync(HttpMethod.Get, "/user", null, cancellationToken))?["id"]) ?? throw new InvalidOperationException("Clockify did not say whom the key signs in as.");
            }

            var path = Placeholder.Replace(spec.Path, match => ids[match.Groups[1].Value]!);
            switch (spec.Act)
            {
                case Act.List:
                    {
                        // Clockify pages by page number, every page the same size: the size is fixed at the start.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"page={page}").Append($"page-size={size}")), null, cancellationToken);
                            var items = (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count < size)
                            {
                                yield break;
                            }
                        }
                    }

                case Act.Get:
                    yield return Record(await session.SendAsync(HttpMethod.Get, path, null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case Act.Create:
                    yield return Record(await session.SendAsync(HttpMethod.Post, path, body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case Act.Update:
                    if (entry && body!["start"] is null)
                    {
                        // Clockify asks a time entry's start on every update: an update that gives none keeps the entry's own.
                        var current = await session.SendAsync(HttpMethod.Get, path, null, cancellationToken);
                        body["start"] = current?["timeInterval"]?["start"]?.DeepClone() ?? throw new InvalidOperationException("Clockify did not say when the time entry started.");
                    }

                    yield return Record(await session.SendAsync(HttpMethod.Put, path, body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    await session.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = ids[Placeholder.Matches(spec.Path)[^1].Groups[1].Value], ["deleted"] = true });
                    yield break;
            }
        }

        // A moment with its zone, in UTC as Clockify reads it.
        private static string Instant(string value, string name) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment) && Regex.IsMatch(value, "(Z|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{value}' is not a date and time with its zone ({name}) - 2026-10-13T08:00:00Z.");

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

        // A client, a project, a tag, a task, a time entry, a user or a workspace: its fields as Clockify gives them - a list or
        // an object (a time entry's interval) as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Clockify's API (a region's too), the key in X-Api-Key; a throttled call waiting as Clockify
        // says; a failure thrown with its status and Clockify's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string key;

            private Session(string api, string key, HttpClient client)
            {
                this.api = api;
                this.key = key;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                // Clockify's address or a region's - https://euc1.clockify.me - its API's path added when not given.
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                var api = root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/api/v1";
                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Clockify connection has no API key.");
                return new Session(api, key, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.TryAddWithoutValidation("X-Api-Key", key);
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
                    // A page that is not Clockify's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"Clockify answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["message"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
