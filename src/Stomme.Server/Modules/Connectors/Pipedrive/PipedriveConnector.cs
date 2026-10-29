#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Pipedrive
{
    /// <summary>
    /// Pipedrive (BL-229, n8n's Pipedrive node: deals, persons, organizations, activities and products listed, got, searched,
    /// created, updated and deleted, notes added): Pipedrive's API v2 (<c>https://api.pipedrive.com/api/v2</c>, or a company's
    /// own <c>https://your-company.pipedrive.com</c>), the account's API token in Pipedrive's <c>x-api-token</c> header - never in an
    /// address. A note goes through API v1, the only one with notes. A test names the user.
    /// </summary>
    /// <remarks>
    /// Every operation but <c>add_note</c> names its <c>object</c> - deals, persons, organizations, activities or products - and a
    /// record by its id (digits), each held to its shape before it reaches the path. <c>list</c>: the object's records - an owner's,
    /// a person's, an organization's, a stage's or a pipeline's, a deal's status, changed since a time (in UTC) - oldest first by
    /// id unless the newest are asked for, page by page by Pipedrive's cursor up to <c>max</c>; one record each, its fields by
    /// Pipedrive's names, a list or an object (emails, phones, custom fields) as JSON. <c>search</c>: what Pipedrive's search finds
    /// for a <c>term</c> (of deals, persons, organizations or products), each its score. <c>get</c> one. <c>create</c>: a record
    /// from <c>fields</c> - Pipedrive's own JSON, an object of at least one field, each named as Pipedrive names its fields;
    /// <c>update</c> changes those given (PATCH); <c>delete</c> removes one (Pipedrive keeps a deleted deal 30 days). <c>add_note</c>:
    /// a note's content on a deal, a person, an organization or a lead. A throttled call waits as Pipedrive says; a failure is
    /// thrown with its status and Pipedrive's error.
    /// </remarks>
    public sealed class PipedriveConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.pipedrive.com";

        public const string ListOperation = "list";

        public const string SearchOperation = "search";

        public const string GetOperation = "get";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        public const string DeleteOperation = "delete";

        public const string AddNoteOperation = "add_note";

        public const int DefaultMax = 1000;

        private static readonly HashSet<string> Objects = new(StringComparer.Ordinal) { "deals", "persons", "organizations", "activities", "products" };

        private static readonly HashSet<string> Searchable = new(StringComparer.Ordinal) { "deals", "persons", "organizations", "products" };

        private static readonly HashSet<string> DealStatuses = new(StringComparer.Ordinal) { "open", "won", "lost", "deleted" };

        private static readonly Regex Digits = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex LeadId = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "pipedrive";

        public string DisplayName => "Pipedrive";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        /// <summary>The key as Pipedrive names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.pipedrive.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.pipedrive.com/docs/api/v1");

        private static readonly ConnectorConfigField ObjectField = new("object", "Object", ConnectorConfigFieldType.Text, Required: true, Description: "deals, persons, organizations, activities or products.");

        private static readonly ConnectorConfigField IdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "The record's id (digits).");

        private static readonly ConnectorConfigField FieldsField = new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: "Pipedrive's own JSON - {\"title\": \"Big deal\", \"value\": 5000, \"currency\": \"SEK\"}.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ListOperation, "Get records", "An object's records - an owner's, a person's, an organization's, a stage's, a status - one record each, its fields by Pipedrive's names - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("owner_id", "Owner", ConnectorConfigFieldType.Text, Required: false, Description: "Only this user's (an id)."),
                    new ConnectorConfigField("person_id", "Person", ConnectorConfigFieldType.Text, Required: false, Description: "Only this person's (an id)."),
                    new ConnectorConfigField("org_id", "Organization", ConnectorConfigFieldType.Text, Required: false, Description: "Only this organization's (an id)."),
                    new ConnectorConfigField("stage_id", "Stage", ConnectorConfigFieldType.Text, Required: false, Description: "Deals: only this stage's (an id)."),
                    new ConnectorConfigField("pipeline_id", "Pipeline", ConnectorConfigFieldType.Text, Required: false, Description: "Deals: only this pipeline's (an id)."),
                    new ConnectorConfigField("filter_id", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "A filter saved in Pipedrive (an id)."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "Deals: open, won, lost or deleted - comma-separated. Empty: all but deleted."),
                    new ConnectorConfigField("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A date or a time."),
                    new ConnectorConfigField("newest_first", "Newest first", ConnectorConfigFieldType.Boolean, Required: false, Description: "The most recently changed first; otherwise by id."),
                    MaxField,
                }),
            },
            new ConnectorOperation(SearchOperation, "Search records", "What Pipedrive's search finds for a term - deals, persons, organizations or products, each its score - n8n's Search.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("object", "Object", ConnectorConfigFieldType.Text, Required: true, Description: "deals, persons, organizations or products."),
                    new ConnectorConfigField("term", "Term", ConnectorConfigFieldType.Text, Required: true, Description: "At least two characters - one with an exact match."),
                    new ConnectorConfigField("exact_match", "Exact match", ConnectorConfigFieldType.Boolean, Required: false, Description: "Only exact matches."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetOperation, "Get a record", "One record - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField }),
            },
            new ConnectorOperation(CreateOperation, "Create a record", "Adds a record - one record: the record - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, FieldsField }),
            },
            new ConnectorOperation(UpdateOperation, "Update a record", "Changes a record's fields - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField, FieldsField }),
            },
            new ConnectorOperation(DeleteOperation, "Delete a record", "Removes a record - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField }),
            },
            new ConnectorOperation(AddNoteOperation, "Add a note", "A note on a deal, a person, an organization or a lead - n8n's note Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Multiline, Required: true, Description: "The note - HTML."),
                    new ConnectorConfigField("deal_id", "Deal", ConnectorConfigFieldType.Text, Required: false, Description: "A deal's id."),
                    new ConnectorConfigField("person_id", "Person", ConnectorConfigFieldType.Text, Required: false, Description: "A person's id."),
                    new ConnectorConfigField("org_id", "Organization", ConnectorConfigFieldType.Text, Required: false, Description: "An organization's id."),
                    new ConnectorConfigField("lead_id", "Lead", ConnectorConfigFieldType.Text, Required: false, Description: "A lead's id (a UUID)."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var origin = Origin(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, origin + "/v1/users/me", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {Text(me?["data"]?["name"])} ({Text(me?["data"]?["company_name"])}).");
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
            var origin = Origin(context.Endpoint);
            var api = origin + "/api/v2";
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? ListOperation;
            var parameters = context.Parameters;

            // What could be refused is refused before anything is sent.
            if (Is(operation, AddNoteOperation))
            {
                var note = new JsonObject { ["content"] = Values.Get(parameters, "content") ?? throw new InvalidOperationException("Give the note's content.") };
                foreach (var field in new[] { "deal_id", "person_id", "org_id" })
                {
                    if (Values.Get(parameters, field) is { } id)
                    {
                        note[field] = long.Parse(Id(id, field.Replace("_id", "", StringComparison.Ordinal)), CultureInfo.InvariantCulture);
                    }
                }

                if (Values.Get(parameters, "lead_id") is { } lead)
                {
                    note["lead_id"] = LeadId.IsMatch(lead.Trim()) ? lead.Trim() : throw new InvalidOperationException($"'{lead}' is not a Pipedrive lead's id (a UUID).");
                }

                if (note.Count == 1)
                {
                    throw new InvalidOperationException("Give what the note is on - a deal, a person, an organization or a lead.");
                }

                using var noteClient = context.Egress.CreateHttpClient(context.Timeout);
                var added = await SendAsync(noteClient, token, HttpMethod.Post, origin + "/v1/notes", note, cancellationToken);
                yield return Record(added?["data"] as JsonObject ?? new JsonObject());
                yield break;
            }

            var objectName = Values.Get(parameters, "object")?.Trim().ToLowerInvariant() ?? "";
            if (!(Is(operation, SearchOperation) ? Searchable : Objects).Contains(objectName))
            {
                throw new InvalidOperationException($"'{Values.Get(parameters, "object")}' is not an object Pipedrive {(Is(operation, SearchOperation) ? "searches" : "keeps")} - {string.Join(", ", Is(operation, SearchOperation) ? Searchable : Objects)}.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, ListOperation) || Is(operation, SearchOperation))
            {
                var search = Is(operation, SearchOperation);
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var query = search ? SearchQuery(parameters) : ListQuery(parameters, objectName);
                var read = 0;
                string? cursor = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = new List<string>(query) { "limit=" + Math.Min(search ? 100 : 500, max - read).ToString(CultureInfo.InvariantCulture) };
                    if (cursor is not null)
                    {
                        page.Add("cursor=" + Uri.EscapeDataString(cursor));
                    }

                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/{objectName}{(search ? "/search" : "")}?{string.Join("&", page)}", null, cancellationToken);
                    var items = search
                        ? (answer?["data"]?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList()
                        : (answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        read++;
                        yield return search
                            ? Record(item["item"] as JsonObject ?? new JsonObject(), item["result_score"] is JsonValue score && score.TryGetValue<double>(out var value) ? value : null)
                            : Record(item);

                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    cursor = Text(answer?["additional_data"]?["next_cursor"]);
                    if (cursor is null || items.Count == 0)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, CreateOperation))
            {
                var made = await SendAsync(client, token, HttpMethod.Post, $"{api}/{objectName}", Fields(Values.Get(parameters, "fields")), cancellationToken);
                yield return Record(made?["data"] as JsonObject ?? new JsonObject());
                yield break;
            }

            var recordId = Id(Values.Get(parameters, "id"), "record");
            if (Is(operation, GetOperation))
            {
                var found = await SendAsync(client, token, HttpMethod.Get, $"{api}/{objectName}/{recordId}", null, cancellationToken);
                yield return Record(found?["data"] as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateOperation))
            {
                var changed = await SendAsync(client, token, HttpMethod.Patch, $"{api}/{objectName}/{recordId}", Fields(Values.Get(parameters, "fields")), cancellationToken);
                yield return Record(changed?["data"] as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, DeleteOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, $"{api}/{objectName}/{recordId}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(recordId, CultureInfo.InvariantCulture), ["object"] = objectName, ["deleted"] = true });
                yield break;
            }

            throw new InvalidOperationException($"Pipedrive has no operation '{operation}' - list, search, get, create, update, delete or add_note.");
        }

        // A list's filters as Pipedrive takes them: ids held to digits, a deal's statuses to Pipedrive's words, a time in UTC.
        private static List<string> ListQuery(IReadOnlyDictionary<string, string> parameters, string objectName)
        {
            var query = new List<string>();
            foreach (var field in new[] { "owner_id", "person_id", "org_id", "stage_id", "pipeline_id", "filter_id" })
            {
                if (Values.Get(parameters, field) is { } id)
                {
                    query.Add(field + "=" + Id(id, field.Replace("_id", "", StringComparison.Ordinal)));
                }
            }

            if (Values.Get(parameters, "status") is { } statuses)
            {
                if (objectName != "deals")
                {
                    throw new InvalidOperationException("Only deals are listed by status.");
                }

                var words = statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(status => status.ToLowerInvariant()).ToList();
                if (words.Count == 0 || words.Any(word => !DealStatuses.Contains(word)))
                {
                    throw new InvalidOperationException($"'{statuses}' is not a deal's status - open, won, lost or deleted.");
                }

                query.Add("status=" + string.Join(",", words));
            }

            if (Values.Get(parameters, "updated_since") is { } since)
            {
                query.Add("updated_since=" + Uri.EscapeDataString(DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{since}' is not a date or a time.")));
            }

            if (Values.GetBool(parameters, "newest_first", false))
            {
                query.Add("sort_by=update_time");
                query.Add("sort_direction=desc");
            }

            return query;
        }

        private static List<string> SearchQuery(IReadOnlyDictionary<string, string> parameters)
        {
            var term = Values.Get(parameters, "term")?.Trim() ?? "";
            var exact = Values.GetBool(parameters, "exact_match", false);
            if (term.Length < (exact ? 1 : 2))
            {
                throw new InvalidOperationException("Give a term of at least two characters - one with an exact match.");
            }

            var query = new List<string> { "term=" + Uri.EscapeDataString(term) };
            if (exact)
            {
                query.Add("exact_match=true");
            }

            return query;
        }

        // A record as Pipedrive answers it: each field by its name, a number, a yes/no or a text as itself, a list or an object as
        // JSON; a search's find its score beside them.
        private static ConnectorRecord Record(JsonObject record, double? score = null)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (score is not null)
            {
                fields["result_score"] = score;
            }

            foreach (var (name, value) in record)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var words) => words,
                    JsonValue whole when whole.TryGetValue<long>(out var number) => number,
                    JsonValue real when real.TryGetValue<double>(out var fraction) => fraction,
                    JsonValue flag when flag.TryGetValue<bool>(out var yes) => yes,
                    _ => value.ToJsonString(),
                };
            }

            return new ConnectorRecord(fields);
        }

        // A record's fields, Pipedrive's own JSON, each field's name held to Pipedrive's (lower-case letters, digits and _).
        private static JsonObject Fields(string? given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given ?? throw new InvalidOperationException("Give the record's fields - Pipedrive's JSON."));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The fields are not JSON - {\"title\": \"Big deal\"}.");
            }

            var fields = parsed as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object.");
            if (fields.Count == 0)
            {
                throw new InvalidOperationException("Give at least one field.");
            }

            foreach (var (name, _) in fields)
            {
                if (!FieldName.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a Pipedrive field's name - a custom field goes inside custom_fields.");
                }
            }

            return fields;
        }

        // The account's address: Pipedrive's API, or the company's own; a path given is dropped.
        private static string Origin(string endpoint)
        {
            var given = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
                ? address.GetLeftPart(UriPartial.Authority)
                : throw new InvalidOperationException($"'{endpoint}' is not Pipedrive's address - {DefaultEndpoint}.");
        }

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Pipedrive {what}'s id (digits).");

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("api-key", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The Pipedrive connection has no API token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Pipedrive's answer as JSON, the token in its header; a throttled call waits as Pipedrive says; a failure thrown with its
        // status and Pipedrive's error.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Add("x-api-token", token);
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
                throw new InvalidOperationException($"Pipedrive answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode || json?["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
            {
                var said = Text(json?["error"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
