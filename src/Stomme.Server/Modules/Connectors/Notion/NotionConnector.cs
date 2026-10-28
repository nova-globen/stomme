#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Notion
{
    /// <summary>
    /// Notion (BL-229, n8n's Notion node: a database's pages got, created and updated): Notion's API
    /// (<c>https://api.notion.com/v1</c>, version 2022-06-28), an integration's secret as the Bearer token. A test asks Notion
    /// who the integration is (<c>users/me</c>).
    /// </summary>
    /// <remarks>
    /// <c>query</c>: a database's pages - those a <c>filter</c> keeps, in a <c>sorts</c>' order, both Notion's own JSON - every page
    /// of 100 followed by its cursor; one record each: its <c>id</c>, <c>url</c>, <c>created_time</c>, <c>last_edited_time</c> and
    /// each property as a plain value (a title or a text its words, a select its option's name, a date its start - a range
    /// start/end - a checkbox true or false, a list - options, people, relations, files - as JSON), as n8n's "simplify" gives them.
    /// <c>create</c>: <c>properties</c>, a record or a list of records as JSON, each a page in the database, its fields written to
    /// the properties of the same name as each property's type takes them (the database's properties read first; a field named
    /// <c>title</c> fills its title property when none is named so); <c>update</c>: <c>pages</c>, each its <c>id</c> - or its
    /// address - and the properties to change, typed by the page's own. A field no property names, or one Notion computes, is
    /// left out and named in <c>left_out</c>. A database's or a page's id is held to Notion's shape - thirty-two hex digits, an
    /// address's last ones - before it reaches the path. A failure is thrown with its status and Notion's own code
    /// (<c>HTTP 404 object_not_found.</c>), the port answering it in its words (D110); a throttled call waits as Notion says.
    /// </remarks>
    public sealed class NotionConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.notion.com/v1";

        /// <summary>The API version every call names.</summary>
        public const string ApiVersion = "2022-06-28";

        public const string QueryOperation = "query";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        /// <summary>The most characters Notion takes in one piece of text.</summary>
        public const int TextLimit = 2000;

        // Thirty-two hex digits standing alone in an address's path - never the first of a longer run.
        private static readonly Regex Hex32 = new("(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.CultureInvariant);

        private static readonly Regex BareId = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "notion";

        public string DisplayName => "Notion";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Notion names it: an integration's secret.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Integration secret" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.notion.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.notion.com/reference/intro");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: false,
                Description: "A database's id or its address - shared with the integration - which an operation naming none uses."),
        });

        private static readonly ConnectorConfigField DatabaseParameter = new("database", "Database", ConnectorConfigFieldType.Text, Required: false, Description: "The database's id or address. Empty: the connection's.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(QueryOperation, "Get database pages", "A database's pages - those a filter keeps, in a sort's order - one record each: its id, url, times and each property as a plain value - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    DatabaseParameter,
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Multiline, Required: false, Description: "Notion's filter as JSON - {\"property\": \"Status\", \"select\": {\"equals\": \"Open\"}}."),
                    new ConnectorConfigField("sorts", "Sorts", ConnectorConfigFieldType.Multiline, Required: false, Description: "Notion's sorts as JSON - [{\"property\": \"Name\", \"direction\": \"ascending\"}]."),
                }),
            },
            new ConnectorOperation(CreateOperation, "Create database pages", "Creates a page in the database for a record, or for each of a list, its fields written to the properties of the same name - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    DatabaseParameter,
                    new ConnectorConfigField("properties", "Properties", ConnectorConfigFieldType.Multiline, Required: true, Description: "A page's properties as JSON, or a list of pages - {\"Name\": \"Ada\", \"Status\": \"Open\", \"Due\": \"2026-10-12\"}."),
                }),
            },
            new ConnectorOperation(UpdateOperation, "Update database pages", "Changes properties of pages named by their ids or addresses - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("pages", "Pages", ConnectorConfigFieldType.Multiline, Required: true, Description: "A page, or a list, each its id and the properties to change - {\"id\": \"...\", \"Status\": \"Done\"}."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, baseUrl + "/users/me", null, cancellationToken);
                var workspace = me["bot"]?["workspace_name"]?.GetValue<string>();
                return ConnectionTestResult.Ok($"Signed in as {me["name"]?.GetValue<string>() ?? "the integration"}{(workspace is null ? "" : " in " + workspace)}.");
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
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var token = Token(context.Credentials);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (string.Equals(context.OperationName, UpdateOperation, StringComparison.OrdinalIgnoreCase))
            {
                var pages = Records(Values.Get(context.Parameters, "pages"), "pages to update")
                    .Select(record => (Id: Id(Field(record, "id"), "page"), Record: record))
                    .ToList();
                foreach (var (id, record) in pages)
                {
                    var page = await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/pages/{id}", null, cancellationToken);
                    var (properties, leftOut) = Typed(Schema(page), record.Where(field => field.Key != "id"));
                    var updated = await SendAsync(client, token, HttpMethod.Patch, $"{baseUrl}/pages/{id}", new JsonObject { ["properties"] = properties }, cancellationToken);
                    yield return Record(updated, leftOut);
                }

                yield break;
            }

            var database = Id(Values.Get(context.Parameters, "database") ?? Values.Get(context.Settings, "database"), "database");
            if (string.Equals(context.OperationName, CreateOperation, StringComparison.OrdinalIgnoreCase))
            {
                var records = Records(Values.Get(context.Parameters, "properties"), "pages to create");
                var schema = Schema(await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/databases/{database}", null, cancellationToken));
                var typed = records.Select(record => Typed(schema, record)).ToList(); // every record typed before the first is sent
                foreach (var (properties, leftOut) in typed)
                {
                    var created = await SendAsync(client, token, HttpMethod.Post, $"{baseUrl}/pages", new JsonObject
                    {
                        ["parent"] = new JsonObject { ["database_id"] = database },
                        ["properties"] = properties,
                    }, cancellationToken);
                    yield return Record(created, leftOut);
                }

                yield break;
            }

            var filter = Json(Values.Get(context.Parameters, "filter"), "The filter is not JSON - Notion's filter object.");
            if (filter is not null and not JsonObject)
            {
                throw new InvalidOperationException("The filter is not Notion's filter - a JSON object.");
            }

            var sorts = Json(Values.Get(context.Parameters, "sorts"), "The sorts are not JSON - Notion's list of sorts.") switch
            {
                null => null,
                JsonArray list => list,
                JsonObject one => new JsonArray(one),
                _ => throw new InvalidOperationException("The sorts are not Notion's sorts - a JSON list."),
            };
            string? cursor = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new JsonObject { ["page_size"] = 100 };
                if (filter is not null)
                {
                    body["filter"] = filter.DeepClone();
                }

                if (sorts is not null)
                {
                    body["sorts"] = sorts.DeepClone();
                }

                if (cursor is not null)
                {
                    body["start_cursor"] = cursor;
                }

                var page = await SendAsync(client, token, HttpMethod.Post, $"{baseUrl}/databases/{database}/query", body, cancellationToken);
                foreach (var found in (page["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return Record(found, null);
                }

                cursor = page["has_more"]?.GetValue<bool>() == true ? page["next_cursor"]?.GetValue<string>() : null;
            }
            while (!string.IsNullOrEmpty(cursor));
        }

        // A database's or a page's id: thirty-two hex digits, dashed or not, or an address's last ones (its path's, never a view's).
        private static string Id(string? given, string what)
        {
            var text = given?.Trim() ?? throw new InvalidOperationException($"Name the {what} - its id or its address.");
            var candidate = Uri.TryCreate(text, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
                ? Hex32.Matches(address.AbsolutePath).LastOrDefault()?.Value
                : text.Replace("-", "", StringComparison.Ordinal);
            if (candidate is null || !BareId.IsMatch(candidate))
            {
                throw new InvalidOperationException($"'{text}' is not a Notion {what} id - thirty-two hex digits, or its address.");
            }

            var id = candidate.ToLowerInvariant();
            return $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}";
        }

        private static string? Field(JsonObject record, string name) =>
            record[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : record[name]?.ToJsonString();

        private static JsonNode? Json(string? given, string notJson)
        {
            if (given is null)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(notJson);
            }
        }

        // The records given: a record or a list of records, as JSON.
        private static List<JsonObject> Records(string? given, string what)
        {
            if (string.IsNullOrWhiteSpace(given))
            {
                throw new InvalidOperationException($"Give the {what}.");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {what} are not JSON - a record or a list of records.");
            }

            return parsed switch
            {
                JsonObject one => new List<JsonObject> { one },
                JsonArray many when many.All(item => item is JsonObject) => many.Cast<JsonObject>().ToList(),
                _ => throw new InvalidOperationException($"The {what} are not a record or a list of records."),
            };
        }

        // Each property's name and type, from a database's schema or a page's own properties.
        private static Dictionary<string, string> Schema(JsonObject holder) =>
            (holder["properties"] as JsonObject ?? new JsonObject())
                .Where(property => property.Value?["type"] is not null)
                .ToDictionary(property => property.Key, property => property.Value!["type"]!.GetValue<string>(), StringComparer.Ordinal);

        // A record's fields as the properties Notion takes, each in its property's type; what no property takes, left out.
        private static (JsonObject Properties, List<string> LeftOut) Typed(IReadOnlyDictionary<string, string> schema, IEnumerable<KeyValuePair<string, JsonNode?>> fields)
        {
            var properties = new JsonObject();
            var leftOut = new List<string>();
            var titleProperty = schema.FirstOrDefault(property => property.Value == "title").Key;
            foreach (var (name, value) in fields)
            {
                var property = schema.ContainsKey(name) ? name : name == "title" && titleProperty is not null ? titleProperty : null;
                if (property is null || Value(schema[property], property, value) is not { } typed)
                {
                    leftOut.Add(name);
                    continue;
                }

                properties[property] = typed;
            }

            return (properties, leftOut);
        }

        // The property types written; the rest Notion computes (formula, rollup, the created and edited stamps, unique_id...) or
        // this does not write (files).
        private static readonly HashSet<string> Writable = new(StringComparer.Ordinal)
        {
            "title", "rich_text", "number", "checkbox", "select", "status", "multi_select", "date", "url", "email", "phone_number", "relation", "people",
        };

        // A value as a property of the type takes it; null for a type not written. A value the type cannot take is refused,
        // naming the property.
        private static JsonObject? Value(string type, string property, JsonNode? value)
        {
            if (!Writable.Contains(type))
            {
                return null;
            }

            var text = value switch
            {
                null => null,
                JsonValue scalar when scalar.TryGetValue<string>(out var given) => given,
                _ => value.ToJsonString(),
            };
            JsonNode? content = type switch
            {
                "title" or "rich_text" => RichText(text),
                "number" => value switch
                {
                    null => null,
                    JsonValue number when number.TryGetValue<double>(out var real) => JsonValue.Create(real),
                    _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => JsonValue.Create(parsed),
                    _ when string.IsNullOrWhiteSpace(text) => null,
                    _ => throw new InvalidOperationException($"'{text}' is not a number - for {property}."),
                },
                "checkbox" => value switch
                {
                    null => JsonValue.Create(false),
                    JsonValue flag when flag.TryGetValue<bool>(out var given) => JsonValue.Create(given),
                    _ when bool.TryParse(text, out var parsed) => JsonValue.Create(parsed),
                    _ => throw new InvalidOperationException($"'{text}' is not true or false - for {property}."),
                },
                "select" or "status" => string.IsNullOrWhiteSpace(text) ? null : new JsonObject { ["name"] = text },
                "multi_select" => new JsonArray(Names(value).Select(name => (JsonNode?)new JsonObject { ["name"] = name }).ToArray()),
                "date" => Date(value, text, property),
                "url" or "email" or "phone_number" => string.IsNullOrWhiteSpace(text) ? null : JsonValue.Create(text),
                "relation" => new JsonArray(Names(value).Select(id => (JsonNode?)new JsonObject { ["id"] = Id(id, "page") }).ToArray()),
                _ => new JsonArray(Names(value).Select(id => (JsonNode?)new JsonObject { ["id"] = Id(id, "person") }).ToArray()), // people
            };

            return new JsonObject { [type] = content };
        }

        // Text in Notion's pieces of at most 2000 characters; nothing, none.
        private static JsonArray RichText(string? text) =>
            new(Enumerable.Range(0, string.IsNullOrEmpty(text) ? 0 : (text.Length + TextLimit - 1) / TextLimit)
                .Select(index => (JsonNode?)new JsonObject { ["text"] = new JsonObject { ["content"] = text!.Substring(index * TextLimit, Math.Min(TextLimit, text.Length - index * TextLimit)) } })
                .ToArray());

        // A list's names: a JSON list's items, or a text's comma-separated parts.
        private static IEnumerable<string> Names(JsonNode? value) => value switch
        {
            null => Array.Empty<string>(),
            JsonArray list => list.Select(item => item is JsonValue scalar && scalar.TryGetValue<string>(out var name) ? name : item?.ToJsonString() ?? "").Where(name => name.Length > 0),
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => new[] { value.ToJsonString() },
        };

        // A date: its start, a range start/end (ISO 8601's interval), or Notion's own {start, end}; nothing clears it.
        private static JsonNode? Date(JsonNode? value, string? text, string property)
        {
            if (value is JsonObject given)
            {
                return given.DeepClone();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Any(part => !DateTimeOffset.TryParse(part, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            {
                throw new InvalidOperationException($"'{text}' is not a date - for {property}.");
            }

            var date = new JsonObject { ["start"] = parts[0] };
            if (parts.Length == 2)
            {
                date["end"] = parts[1];
            }

            return date;
        }

        // A page as the pipeline reads it: its id, address and times, then each property as a plain value.
        private static ConnectorRecord Record(JsonObject page, List<string>? leftOut)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = page["id"]?.GetValue<string>(),
                ["url"] = page["url"]?.GetValue<string>(),
                ["created_time"] = page["created_time"]?.GetValue<string>(),
                ["last_edited_time"] = page["last_edited_time"]?.GetValue<string>(),
            };
            foreach (var (name, property) in page["properties"] as JsonObject ?? new JsonObject())
            {
                fields[name] = property is JsonObject typed ? Plain(typed) : null;
            }

            if (leftOut is not null)
            {
                fields["left_out"] = string.Join(", ", leftOut);
            }

            return new ConnectorRecord(fields);
        }

        // A property's value, plain: as n8n's "simplify" gives it.
        private static object? Plain(JsonObject property)
        {
            var type = property["type"]?.GetValue<string>() ?? "";
            var value = property[type];
            return type switch
            {
                "title" or "rich_text" => string.Concat((value as JsonArray ?? new JsonArray()).Select(piece => piece?["plain_text"]?.GetValue<string>() ?? "")),
                "number" => Scalar(value),
                "select" or "status" => value?["name"]?.GetValue<string>(),
                "multi_select" => List((value as JsonArray)?.Select(option => option?["name"])),
                "date" => DateText(value),
                "checkbox" or "url" or "email" or "phone_number" or "created_time" or "last_edited_time" => Scalar(value),
                "created_by" or "last_edited_by" => value?["name"]?.GetValue<string>() ?? value?["id"]?.GetValue<string>(),
                "people" => List((value as JsonArray)?.Select(person => person?["name"] ?? person?["id"])),
                "relation" => List((value as JsonArray)?.Select(related => related?["id"])),
                "files" => List((value as JsonArray)?.Select(file => file?["file"]?["url"] ?? file?["external"]?["url"] ?? file?["name"])),
                "formula" => value?["type"]?.GetValue<string>() is { } kind ? kind == "date" ? DateText(value[kind]) : Scalar(value[kind]) : null,
                "rollup" => value?["type"]?.GetValue<string>() switch
                {
                    "number" => Scalar(value["number"]),
                    "date" => DateText(value["date"]),
                    "array" => List((value["array"] as JsonArray)?.Select(item => item is JsonObject inner ? JsonValue.Create(Plain(inner)?.ToString()) : null)),
                    _ => null,
                },
                "unique_id" => value?["prefix"]?.GetValue<string>() is { Length: > 0 } prefix ? $"{prefix}-{value["number"]}" : Scalar(value?["number"]),
                _ => value?.ToJsonString(),
            };
        }

        private static object? Scalar(JsonNode? value) => value switch
        {
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            JsonValue scalar when scalar.TryGetValue<long>(out var number) => number,
            JsonValue scalar when scalar.TryGetValue<double>(out var real) => real,
            JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
            null => null,
            _ => value.ToJsonString(),
        };

        private static string? DateText(JsonNode? date) =>
            date?["start"]?.GetValue<string>() is not { } start ? null : date["end"]?.GetValue<string>() is { } end ? $"{start}/{end}" : start;

        private static string List(IEnumerable<JsonNode?>? items) =>
            new JsonArray((items ?? Array.Empty<JsonNode?>()).Where(item => item is not null).Select(item => item!.DeepClone()).ToArray()).ToJsonString();

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") ?? throw new InvalidOperationException("The connection has no integration secret.");

        // Notion's answer as JSON, a throttled call waiting as Notion says; a failure thrown with its status and Notion's code.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("Notion-Version", ApiVersion);
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
                throw new InvalidOperationException($"Notion answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = json?["code"] is JsonValue given && given.TryGetValue<string>(out var text) ? text : null;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}.");
            }

            return json as JsonObject ?? throw new InvalidOperationException("Notion answered with no JSON.");
        }
    }
}
