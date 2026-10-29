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

namespace Stomme.Modules.Connectors.GoogleDocs
{
    /// <summary>
    /// Google Docs (n8n's Google Docs node: a document created, got and updated): documents over the Docs API
    /// (<c>https://docs.googleapis.com/v1</c>) - and the Drive API (<c>https://www.googleapis.com/drive/v3</c>, the
    /// <c>drive_endpoint</c> setting) to make one in a folder - and the OAuth2 REFRESH-TOKEN scheme: a Google Cloud OAuth client's
    /// id and secret and a refresh token granted the documents and drive.file scopes, exchanged at Google's token endpoint (the
    /// <c>token_url</c> setting) through the shared <see cref="OAuth2TokenProvider"/>, as Google Calendar's is. A test reads the
    /// connection's document, when it names one.
    /// </summary>
    /// <remarks>
    /// A document is its id or its address (<c>docs.google.com/document/d/{id}/edit</c>), a folder and a shared drive their ids -
    /// each held to Google's shape before it reaches the path. <c>get_document</c>: its id, title, revision and address and its
    /// text - every paragraph's runs, its tables' and contents' too, in order. <c>create_document</c>: a title, in a folder (My
    /// Drive's root by default) or a shared drive. <c>insert_text</c>: text, at an index or at the end of the body (or of a header,
    /// a footer, a footnote by its segment id). <c>replace_text</c>: every occurrence of a text, its case counting or not - how
    /// many it changed said. <c>update_document</c>: a list of Docs API requests as JSON, sent as they are (n8n's actions are
    /// Docs requests), with a revision required or targeted; the replies said. A throttled call waits as Google says; a failure is
    /// thrown with its status, Google's status word and its message.
    /// </remarks>
    public sealed class GoogleDocsConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://docs.googleapis.com/v1";

        public const string DefaultDriveEndpoint = "https://www.googleapis.com/drive/v3";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string GetDocumentOperation = "get_document";

        public const string CreateDocumentOperation = "create_document";

        public const string InsertTextOperation = "insert_text";

        public const string ReplaceTextOperation = "replace_text";

        public const string UpdateDocumentOperation = "update_document";

        private static readonly Regex DocumentId = new("^[A-Za-z0-9_-]{10,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex DocumentAddress = new("^https://docs\\.google\\.com/document/(?:u/[0-9]+/)?d/([A-Za-z0-9_-]{10,200})(?:[/?#].*)?$", RegexOptions.CultureInvariant);

        private static readonly Regex DriveId = new("^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex Segment = new("^[A-Za-z0-9._-]{1,200}$", RegexOptions.CultureInvariant);

        // JSON as written: text, its accents and its markup, as it is.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { GetDocumentOperation, CreateDocumentOperation, InsertTextOperation, ReplaceTextOperation, UpdateDocumentOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googledocs";

        public string DisplayName => "Google Docs";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://docs.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/workspace/docs/api/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("document", "Document", ConnectorConfigFieldType.Text, Required: false,
                Description: "A document's id or address, which a test reads the title of and an operation naming none uses."),
            new ConnectorConfigField("drive_endpoint", "Drive endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google Drive's API, through which a document is made in a folder. Leave it as it is.") { Default = DefaultDriveEndpoint },
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField DocumentField = new("document", "Document", ConnectorConfigFieldType.Text, Required: false,
            Description: "The document's id or address. Empty: the connection's.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(GetDocumentOperation, "Get a document", "A document's title, revision and text - n8n's Get.") { Parameters = new ConnectorConfigSchema(new[] { DocumentField }) },
            new ConnectorOperation(CreateDocumentOperation, "Create a document", "Makes an empty document in a folder - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "A Drive folder's id. Empty: My Drive."),
                    new ConnectorConfigField("drive", "Shared drive", ConnectorConfigFieldType.Text, Required: false, Description: "A shared drive's id, when the folder is in one."),
                }),
            },
            new ConnectorOperation(InsertTextOperation, "Insert text", "Inserts text at an index, or at the end of the body - n8n's text Insert.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    DocumentField,
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "As written - a line break a new paragraph."),
                    new ConnectorConfigField("index", "Index", ConnectorConfigFieldType.Integer, Required: false, Description: "Where it goes, 1 the start. Empty: the end."),
                    new ConnectorConfigField("segment", "Segment", ConnectorConfigFieldType.Text, Required: false, Description: "A header's, a footer's or a footnote's id. Empty: the body."),
                }),
            },
            new ConnectorOperation(ReplaceTextOperation, "Replace text", "Replaces every occurrence of a text - n8n's text Find and Replace.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    DocumentField,
                    new ConnectorConfigField("find", "Find", ConnectorConfigFieldType.Text, Required: true, Description: "The text to find."),
                    new ConnectorConfigField("replace", "Replace with", ConnectorConfigFieldType.Text, Required: false, Description: "Empty: the text removed."),
                    new ConnectorConfigField("match_case", "Match case", ConnectorConfigFieldType.Boolean, Required: false, Description: "Its case counting.") { Default = "false" },
                }),
            },
            new ConnectorOperation(UpdateDocumentOperation, "Update a document", "Sends Docs API requests as they are - insert a table, a page break, bullets, a header... - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    DocumentField,
                    new ConnectorConfigField("requests", "Requests", ConnectorConfigFieldType.Multiline, Required: true,
                        Description: "A JSON list of Docs API requests - [{\"insertPageBreak\": {\"endOfSegmentLocation\": {\"segmentId\": \"\"}}}]."),
                    new ConnectorConfigField("required_revision", "Required revision", ConnectorConfigFieldType.Text, Required: false, Description: "Refused if the document has changed since this revision."),
                    new ConnectorConfigField("target_revision", "Target revision", ConnectorConfigFieldType.Text, Required: false, Description: "Applied as if to this revision, merged with changes since."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var document = Values.Get(context.Settings, "document") is { } named ? Document(named) : null;
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                if (document is null)
                {
                    return ConnectionTestResult.Ok("Signed in.");
                }

                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/documents/{document}?fields=title", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in - {Text(answer["title"]) ?? "the document"} read.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var operation = context.OperationName ?? GetDocumentOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Google Docs has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before a token is asked for.
            var document = Is(operation, CreateDocumentOperation) ? null
                : Document(Values.Get(parameters, "document") ?? Values.Get(context.Settings, "document") ?? throw new InvalidOperationException("Name the document - on the operation or the connection."));
            JsonObject? body = null;
            if (Is(operation, InsertTextOperation))
            {
                var text = parameters.TryGetValue("text", out var given) && given.Length > 0 ? given : throw new InvalidOperationException("Give the text to insert.");
                var segment = Values.Get(parameters, "segment") is { } id ? Segment.IsMatch(id) ? id : throw new InvalidOperationException($"'{id}' is not a segment's id.") : "";
                var insert = new JsonObject { ["text"] = text };
                if (Values.Get(parameters, "index") is { } at)
                {
                    insert["location"] = new JsonObject { ["segmentId"] = segment, ["index"] = int.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 ? index : throw new InvalidOperationException($"'{at}' is not an index - 1 the start.") };
                }
                else
                {
                    insert["endOfSegmentLocation"] = new JsonObject { ["segmentId"] = segment };
                }

                body = new JsonObject { ["requests"] = new JsonArray(new JsonObject { ["insertText"] = insert }) };
            }
            else if (Is(operation, ReplaceTextOperation))
            {
                var find = parameters.TryGetValue("find", out var wanted) && wanted.Length > 0 ? wanted : throw new InvalidOperationException("Give the text to find.");
                body = new JsonObject
                {
                    ["requests"] = new JsonArray(new JsonObject
                    {
                        ["replaceAllText"] = new JsonObject
                        {
                            ["replaceText"] = parameters.TryGetValue("replace", out var replacement) ? replacement : "",
                            ["containsText"] = new JsonObject { ["text"] = find, ["matchCase"] = Flag(parameters, "match_case") },
                        },
                    }),
                };
            }
            else if (Is(operation, UpdateDocumentOperation))
            {
                JsonNode? requests;
                try
                {
                    requests = JsonNode.Parse(Values.Get(parameters, "requests") ?? throw new InvalidOperationException("Give the requests - a JSON list of Docs API requests."));
                }
                catch (JsonException)
                {
                    requests = null;
                }

                if (requests is not JsonArray { Count: > 0 } list || list.Any(request => request is not JsonObject { Count: 1 }))
                {
                    throw new InvalidOperationException("The requests are not a JSON list of Docs API requests, each one request - [{\"insertPageBreak\": {...}}].");
                }

                body = new JsonObject { ["requests"] = list };
                var required = Values.Get(parameters, "required_revision");
                var target = Values.Get(parameters, "target_revision");
                if (required is not null && target is not null)
                {
                    throw new InvalidOperationException("A revision is required or targeted, not both.");
                }

                if ((required ?? target) is { } revision)
                {
                    body["writeControl"] = new JsonObject { [required is not null ? "requiredRevisionId" : "targetRevisionId"] = Segment.IsMatch(revision) ? revision : throw new InvalidOperationException($"'{revision}' is not a revision's id.") };
                }
            }

            var title = Is(operation, CreateDocumentOperation) ? Values.Get(parameters, "title") is { } named ? OneLine(named, "title") : throw new InvalidOperationException("Give the document's title.") : null;
            var folder = Values.Get(parameters, "folder") is { } folderId ? DriveId.IsMatch(folderId) ? folderId : throw new InvalidOperationException($"'{folderId}' is not a folder's id.") : null;
            var drive = Values.Get(parameters, "drive") is { } driveId ? DriveId.IsMatch(driveId) ? driveId : throw new InvalidOperationException($"'{driveId}' is not a shared drive's id.") : null;

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, GetDocumentOperation))
            {
                var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/documents/{document}", null, cancellationToken);
                var text = new StringBuilder();
                Collect(answer["body"]?["content"] as JsonArray, text);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Text(answer["documentId"]) ?? document,
                    ["title"] = Text(answer["title"]),
                    ["revision"] = Text(answer["revisionId"]),
                    ["url"] = $"https://docs.google.com/document/d/{Text(answer["documentId"]) ?? document}/edit",
                    ["text"] = text.ToString(),
                });
                yield break;
            }

            if (Is(operation, CreateDocumentOperation))
            {
                var file = new JsonObject { ["name"] = title, ["mimeType"] = "application/vnd.google-apps.document" };
                if (folder is not null)
                {
                    file["parents"] = new JsonArray(folder);
                }

                var made = await SendAsync(client, token, HttpMethod.Post, $"{Values.BaseUrl(Values.Get(context.Settings, "drive_endpoint") ?? "", DefaultDriveEndpoint)}/files?fields=id,name,webViewLink"
                    + (drive is null && folder is null ? "" : "&supportsAllDrives=true"), file, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Text(made["id"]),
                    ["title"] = Text(made["name"]),
                    ["url"] = Text(made["webViewLink"]) ?? (Text(made["id"]) is { } id ? $"https://docs.google.com/document/d/{id}/edit" : null),
                });
                yield break;
            }

            var replies = await SendAsync(client, token, HttpMethod.Post, $"{api}/documents/{document}:batchUpdate", body, cancellationToken);
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(replies["documentId"]) ?? document,
                ["revision"] = Text(replies["writeControl"]?["requiredRevisionId"]),
            };
            if (Is(operation, ReplaceTextOperation))
            {
                fields["changed"] = (replies["replies"] as JsonArray)?.FirstOrDefault()?["replaceAllText"]?["occurrencesChanged"] is JsonValue changed && changed.TryGetValue<long>(out var count) ? count : 0L;
            }
            else if (Is(operation, UpdateDocumentOperation))
            {
                fields["replies"] = (replies["replies"] as JsonArray ?? new JsonArray()).ToJsonString(Wire);
            }

            yield return new ConnectorRecord(fields);
        }

        // A document's text in order: every paragraph's text runs, a table's cells and a table of contents walked into.
        private static void Collect(JsonArray? content, StringBuilder text)
        {
            foreach (var element in (content ?? new JsonArray()).OfType<JsonObject>())
            {
                foreach (var run in (element["paragraph"]?["elements"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    text.Append(Text(run["textRun"]?["content"]));
                }

                foreach (var row in (element["table"]?["tableRows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    foreach (var cell in (row["tableCells"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        Collect(cell["content"] as JsonArray, text);
                    }
                }

                Collect(element["tableOfContents"]?["content"] as JsonArray, text);
            }
        }

        private static string Document(string given)
        {
            var trimmed = given.Trim();
            if (DocumentAddress.Match(trimmed) is { Success: true } address)
            {
                return address.Groups[1].Value;
            }

            return DocumentId.IsMatch(trimmed) ? trimmed : throw new InvalidOperationException($"'{given}' is not a document - its id, or its address (docs.google.com/document/d/{{id}}/edit).");
        }

        private static bool Flag(IReadOnlyDictionary<string, string> parameters, string name) =>
            Values.Get(parameters, name) is not { } given ? false
                : given.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                : given.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidOperationException($"'{given}' is not true or false ({name.Replace('_', ' ')}).");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer an empty object), a throttled call waiting as Google says; a failure thrown with
        // its status, Google's status word and its message.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                throw new InvalidOperationException($"Google Docs answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
