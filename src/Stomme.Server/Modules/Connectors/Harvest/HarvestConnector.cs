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

namespace Stomme.Modules.Connectors.Harvest
{
    /// <summary>
    /// Harvest (BL-229, n8n's Harvest node: an account's clients, contacts, projects, tasks, users, invoices, estimates,
    /// expenses and time entries created, got, read, updated and deleted, a timer restarted and stopped, the company read):
    /// Harvest's API v2, a personal access token as a bearer token and the account it acts in (<c>Harvest-Account-Id</c>), over
    /// the host's egress. A test names the account's company.
    /// </summary>
    /// <remarks>
    /// Every resource's list - with Harvest's own filters, page by page up to <c>max</c> - get, create, update and delete, from
    /// one table of operations; a time entry's timer restarted or stopped and its external reference removed; the company and
    /// the token's own user. A create or update writes a JSON object of fields as Harvest names them. Every id, flag, date and
    /// state held to its shape before anything is sent. A throttled call waits as Harvest says; a failure is thrown with its
    /// status and Harvest's word.
    /// </remarks>
    public sealed class HarvestConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.harvestapp.com/v2";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Number = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private enum Act
        {
            List,
            Get,
            Create,
            Update,
            Delete,
            Action,
        }

        private enum Kind
        {
            Id,
            Flag,
            Date,
            Moment,
            State,
            Text,
        }

        // A list's filters as Harvest names them, each of its kind.
        private static readonly (string Name, Kind Kind)[] Changed = { ("updated_since", Kind.Moment) };

        private sealed record Spec(string Name, Act Act, string Path, string Title, string Description, (string Name, Kind Kind)[] Filters);

        private static readonly Spec[] Specs = Build().ToArray();

        private static IEnumerable<Spec> Build()
        {
            yield return new("company", Act.Get, "/company", "Get the company", "The account's company - n8n's Harvest Company Get.", Array.Empty<(string, Kind)>());
            yield return new("me", Act.Get, "/users/me", "Get the token's user", "Whom the token signs in as - n8n's Harvest User Me.", Array.Empty<(string, Kind)>());
            foreach (var (plural, singular, title, filters) in new (string, string, string, (string, Kind)[])[]
            {
                ("clients", "client", "Client", new[] { ("is_active", Kind.Flag) }),
                ("contacts", "contact", "Contact", new[] { ("client_id", Kind.Id) }),
                ("projects", "project", "Project", new[] { ("is_active", Kind.Flag), ("client_id", Kind.Id) }),
                ("tasks", "task", "Task", new[] { ("is_active", Kind.Flag) }),
                ("users", "user", "User", new[] { ("is_active", Kind.Flag) }),
                ("invoices", "invoice", "Invoice", new[] { ("client_id", Kind.Id), ("project_id", Kind.Id), ("state", Kind.State), ("from", Kind.Date), ("to", Kind.Date) }),
                ("estimates", "estimate", "Estimate", new[] { ("client_id", Kind.Id), ("state", Kind.State), ("from", Kind.Date), ("to", Kind.Date) }),
                ("expenses", "expense", "Expense", new[] { ("user_id", Kind.Id), ("client_id", Kind.Id), ("project_id", Kind.Id), ("is_billed", Kind.Flag), ("from", Kind.Date), ("to", Kind.Date) }),
                ("time_entries", "time_entry", "Time entry", new[] { ("user_id", Kind.Id), ("client_id", Kind.Id), ("project_id", Kind.Id), ("task_id", Kind.Id), ("is_billed", Kind.Flag), ("is_running", Kind.Flag), ("from", Kind.Date), ("to", Kind.Date), ("external_reference_id", Kind.Text) }),
            })
            {
                yield return new(plural, Act.List, "/" + plural, $"Get {plural.Replace('_', ' ')}", $"n8n's Harvest {title} Get Many.", filters.Concat(Changed).ToArray());
                yield return new("get_" + singular, Act.Get, $"/{plural}/{{id}}", $"Get a {title.ToLowerInvariant()}", $"n8n's Harvest {title} Get.", Array.Empty<(string, Kind)>());
                yield return new("create_" + singular, Act.Create, "/" + plural, $"Create a {title.ToLowerInvariant()}", $"n8n's Harvest {title} Create.", Array.Empty<(string, Kind)>());
                yield return new("update_" + singular, Act.Update, $"/{plural}/{{id}}", $"Update a {title.ToLowerInvariant()}", $"n8n's Harvest {title} Update.", Array.Empty<(string, Kind)>());
                yield return new("delete_" + singular, Act.Delete, $"/{plural}/{{id}}", $"Delete a {title.ToLowerInvariant()}", $"n8n's Harvest {title} Delete.", Array.Empty<(string, Kind)>());
            }

            yield return new("restart_time_entry", Act.Action, "/time_entries/{id}/restart", "Restart a timer", "A stopped time entry's timer started again - n8n's Harvest Restart Time.", Array.Empty<(string, Kind)>());
            yield return new("stop_time_entry", Act.Action, "/time_entries/{id}/stop", "Stop a timer", "A running time entry's timer stopped - n8n's Harvest Stop Time.", Array.Empty<(string, Kind)>());
            yield return new("delete_time_entry_external_reference", Act.Delete, "/time_entries/{id}/external_reference", "Remove an external reference", "A time entry's link to another tool removed - n8n's Harvest Delete External.", Array.Empty<(string, Kind)>());
        }

        private static readonly string[] States = { "draft", "open", "paid", "closed", "sent", "accepted", "declined" };

        public string ConnectorType => "harvest";

        public string DisplayName => "Harvest";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://www.getharvest.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://help.getharvest.com/api-v2/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("account_id", "Account", ConnectorConfigFieldType.Text, Required: true, Description: "The account's id - beside the token on Harvest's Developers page."),
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = Specs.Select(spec => new ConnectorOperation(spec.Name, spec.Title, spec.Description) { Parameters = Schema(spec) }).ToArray();

        private static ConnectorConfigSchema Schema(Spec spec)
        {
            var fields = new List<ConnectorConfigField>();
            if (spec.Path.Contains("{id}", StringComparison.Ordinal))
            {
                fields.Add(new ConnectorConfigField("id", "Id", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - a number."));
            }

            if (spec.Act is Act.Create or Act.Update)
            {
                fields.Add(new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object of fields as Harvest names them - {\"name\": \"Café AB\", \"is_active\": true}."));
            }

            foreach (var (name, kind) in spec.Filters)
            {
                fields.Add(new ConnectorConfigField(name, name, ConnectorConfigFieldType.Text, Required: false, Description: kind switch
                {
                    Kind.Id => "An id - a number.",
                    Kind.Flag => "true or false.",
                    Kind.Date => "A date - 2026-10-01.",
                    Kind.Moment => "A date and time with its zone - 2026-10-01T00:00:00Z.",
                    Kind.State => "draft, open, paid, closed, sent, accepted or declined.",
                    _ => "Its text.",
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
                var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
                using var client = session.Client;
                var company = await session.SendAsync(HttpMethod.Get, "/company", null, cancellationToken);
                return ConnectionTestResult.Ok($"Harvest: {Text(company?["name"]) ?? "the account"} answers.");
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
            var spec = Specs.FirstOrDefault(known => string.Equals(known.Name, (context.OperationName ?? "company").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Harvest has no operation '{context.OperationName}' - {string.Join(", ", Specs[..^1].Select(known => known.Name))} or {Specs[^1].Name}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent: the id, the filters, the fields.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? id = null;
            if (spec.Path.Contains("{id}", StringComparison.Ordinal))
            {
                id = Values.Get(p, "id") is { } given ? Number.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id - a number.") : throw new InvalidOperationException("Give the id.");
            }

            var query = new List<string>();
            foreach (var (name, kind) in spec.Filters)
            {
                if (Values.Get(p, name) is not { } value)
                {
                    continue;
                }

                query.Add(name + "=" + Uri.EscapeDataString(kind switch
                {
                    Kind.Id => Number.IsMatch(value) ? value : throw new InvalidOperationException($"'{value}' is not an id - a number ({name})."),
                    Kind.Flag => value.ToLowerInvariant() is "true" or "false" ? value.ToLowerInvariant() : throw new InvalidOperationException($"'{value}' is not true or false ({name})."),
                    Kind.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : throw new InvalidOperationException($"'{value}' is not a date - 2026-10-01 ({name})."),
                    Kind.Moment => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment) && Regex.IsMatch(value, "(Z|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant)
                        ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                        : throw new InvalidOperationException($"'{value}' is not a date and time with its zone ({name})."),
                    Kind.State => States.FirstOrDefault(state => string.Equals(state, value, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{value}' is not {string.Join(", ", States[..^1])} or {States[^1]} ({name})."),
                    _ => value.Length <= 255 && !value.Any(char.IsControl) ? value : throw new InvalidOperationException($"The {name} is one line of up to 255 characters."),
                }));
            }

            JsonObject? body = null;
            if (spec.Act is Act.Create or Act.Update)
            {
                body = (Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object as Harvest names them.")) is var fields && Parse(fields) is JsonObject { Count: > 0 } given
                    ? given
                    : throw new InvalidOperationException("The fields are not a JSON object with a field or more - {\"name\": \"Café AB\"}.");
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
            using var client = session.Client;
            var path = spec.Path.Replace("{id}", id, StringComparison.Ordinal);
            switch (spec.Act)
            {
                case Act.List:
                    {
                        // Harvest pages by page number, every page the same size, and names the next one.
                        var key = spec.Path.TrimStart('/');
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"page={page}").Append($"per_page={size}")), null, cancellationToken);
                            foreach (var item in (answer?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (answer?["next_page"] is not JsonValue)
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
                case Act.Action:
                    yield return Record(await session.SendAsync(HttpMethod.Patch, path, body, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    await session.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
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

        // A client, a project, an invoice, a time entry...: its fields as Harvest gives them - a nested client or project, a
        // list of line items, as its JSON.
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

        // One connection's calls: Harvest's API, the token as a bearer token and the account it acts in; a throttled call
        // waiting as Harvest says; a failure thrown with its status and Harvest's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string token;
            private readonly string account;

            private Session(string api, string token, string account, HttpClient client)
            {
                this.api = api;
                this.token = token;
                this.account = account;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Harvest connection has no access token.");
                var account = Values.Get(settings, "account_id") is { } given
                    ? Number.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a Harvest account's id - a number.")
                    : throw new InvalidOperationException("The Harvest connection has no account - its id, beside the token on Harvest's Developers page.");
                return new Session(api, token, account, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Harvest-Account-Id", account);
                    request.Headers.TryAddWithoutValidation("User-Agent", "Stomme pipelines");
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
                    // A page that is not Harvest's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"Harvest answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["error_description"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
