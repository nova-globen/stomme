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

namespace Stomme.Modules.Connectors.MicrosoftSharePoint
{
    /// <summary>
    /// Microsoft SharePoint (n8n's Microsoft SharePoint node: a site's lists, their items and its files): Microsoft
    /// Graph's sites API with an app's client credentials - the tenant's token endpoint - through the family's token provider,
    /// over the host's egress. A test names the site the connection's address points at, or the tenant's root site.
    /// </summary>
    /// <remarks>
    /// A site by its id (<c>kafe.sharepoint.com,{guid},{guid}</c>), its address (<c>kafe.sharepoint.com:/sites/Kassa</c>) or its host alone (the root site);
    /// a list by its id or its name. <c>lists</c> and <c>list</c>; <c>items</c> - by an OData filter on their columns
    /// (<c>fields/Status eq 'Open'</c>, asked of SharePoint even on columns it has not indexed), page by page by Graph's next
    /// link - followed only while it stays on the same list - up to <c>max</c> (1,000); <c>item</c>; <c>create_item</c>,
    /// <c>update_item</c> (its columns changed, the rest kept) and <c>upsert_item</c> - by a column's value: one item that has it
    /// updated, none created, more than one refused; <c>delete_item</c>; and a file of the site's documents - <c>download_file</c>
    /// into the pipeline run's folder, <c>upload_file</c> and <c>update_file</c> from it or as text (<see cref="GraphDrive"/>, as
    /// OneDrive's). An item is its columns, its id, address and times; a
    /// list or an object among its columns as its JSON. Every site, list, item, column and value held to its shape before a token
    /// is asked for. A throttled call waits as Graph says; a failure is thrown with its status and Graph's code and message.
    /// </remarks>
    public sealed class MicrosoftSharePointConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string ListsOperation = "lists";

        public const string ListOperation = "list";

        public const string ItemsOperation = "items";

        public const string ItemOperation = "item";

        public const string CreateItemOperation = "create_item";

        public const string UpdateItemOperation = "update_item";

        public const string UpsertItemOperation = "upsert_item";

        public const string DeleteItemOperation = "delete_item";

        public const string DownloadFileOperation = "download_file";

        public const string UploadFileOperation = "upload_file";

        public const string UpdateFileOperation = "update_file";

        private static readonly string[] Conflicts = { "replace", "rename", "fail" };

        public const int DefaultMax = 1000;

        private const int PageSize = 200;

        private static readonly string[] Known = { ListsOperation, ListOperation, ItemsOperation, ItemOperation, CreateItemOperation, UpdateItemOperation, UpsertItemOperation, DeleteItemOperation, DownloadFileOperation, UploadFileOperation, UpdateFileOperation };

        private static readonly Regex SiteById = new("^[a-z0-9.\\-]+\\.sharepoint\\.(com|us|de|cn)(,[0-9a-fA-F\\-]{36}){2}$", RegexOptions.CultureInvariant);

        private static readonly Regex SiteByHost = new("^[a-z0-9.\\-]+\\.sharepoint\\.(com|us|de|cn)$", RegexOptions.CultureInvariant);

        private static readonly Regex SiteByPath = new("^[a-z0-9.\\-]+\\.sharepoint\\.(com|us|de|cn):/(sites|teams)/[A-Za-z0-9_.\\-]{1,128}:?$", RegexOptions.CultureInvariant);

        private static readonly Regex ListId = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex ListName = new("^[^\\x00-\\x1F/\\\\:*?\"<>|#%]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly Regex ItemId = new("^[0-9]{1,10}$", RegexOptions.CultureInvariant);

        private static readonly Regex Column = new("^[A-Za-z_][A-Za-z0-9_]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsoftsharepoint";

        public string DisplayName => "Microsoft SharePoint";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) id (Entra ID > App registrations > the app)",
            ["client-secret"] = "Client secret (Certificates & secrets)",
            ["token-url"] = "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://www.microsoft.com/microsoft-365/sharepoint/collaboration");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/sharepoint");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("site", "Site", ConnectorConfigFieldType.Text, Required: false, Description: "The site a step names none of - kafe.sharepoint.com:/sites/Kassa, or its id."),
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false, Description: "The token's scope - change it only for a sovereign cloud.") { Default = DefaultScope },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField SiteField = Field("site", "Site", "kafe.sharepoint.com:/sites/Kassa, or its id - the connection's when left empty.");

        private static readonly ConnectorConfigField ListField = Field("list", "List", "Its name or id.", required: true);

        private static readonly ConnectorConfigField ItemField = Field("id", "Item", "Its id - a number.", required: true);

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of columns and values by their internal names - {\"Title\": \"Kassan\", \"Status\": \"Open\"}.", ConnectorConfigFieldType.Multiline, required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ListsOperation, "Get lists", "n8n's Microsoft SharePoint List Get Many.") { Parameters = new ConnectorConfigSchema(new[] { SiteField }) },
            new ConnectorOperation(ListOperation, "Get a list", "n8n's Microsoft SharePoint List Get.") { Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField }) },
            new ConnectorOperation(ItemsOperation, "Get items", "n8n's Microsoft SharePoint Item Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    SiteField, ListField, Field("filter", "Filter", "An OData filter on its columns - fields/Status eq 'Open'."),
                    Field("max", "At most", "How many, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(ItemOperation, "Get an item", "n8n's Microsoft SharePoint Item Get.") { Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField, ItemField }) },
            new ConnectorOperation(CreateItemOperation, "Create an item", "n8n's Microsoft SharePoint Item Create.") { Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField, ValuesField }) },
            new ConnectorOperation(UpdateItemOperation, "Update an item", "Its columns changed, the rest kept - n8n's Microsoft SharePoint Item Update.") { Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField, ItemField, ValuesField }) },
            new ConnectorOperation(UpsertItemOperation, "Create or update an item", "By a column's value - n8n's Microsoft SharePoint Item Create or Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField, Field("match", "Match on", "The column whose value finds the item - Title.", required: true), ValuesField }),
            },
            new ConnectorOperation(DeleteItemOperation, "Delete an item", "n8n's Microsoft SharePoint Item Delete.") { Parameters = new ConnectorConfigSchema(new[] { SiteField, ListField, ItemField }) },
            new ConnectorOperation(DownloadFileOperation, "Download a file", "A file of the site's documents written into the run's pipeline folder - n8n's Microsoft SharePoint File Download.")
            {
                Parameters = new ConnectorConfigSchema(new[] { SiteField, DocumentField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true) }),
            },
            new ConnectorOperation(UploadFileOperation, "Upload a file", "A file of the run's pipeline folder, or text, stored in a folder of the site's documents - n8n's Microsoft SharePoint File Upload.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    SiteField, Field("folder", "Folder", "A folder's id or path in the site's documents, or root. Empty: the documents' root."),
                    Field("name", "Name", "Its name. Empty: the file's own name."),
                }.Concat(ContentFields).Append(Field("conflict", "If the name is taken", "replace (the default), rename or fail.") with { Default = "replace" }).ToArray()),
            },
            new ConnectorOperation(UpdateFileOperation, "Update a file", "A file's name, and its content from the run's folder or text - n8n's Microsoft SharePoint File Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { SiteField, DocumentField, Field("name", "Name", "Its new name. Empty: as it is.") }.Concat(ContentFields).ToArray()),
            },
        };

        private static readonly ConnectorConfigField DocumentField = Field("item", "File", "The file in the site's documents - its id, or its path from their root: /Kvitton/1042.pdf.", required: true);

        private static readonly ConnectorConfigField[] ContentFields =
        {
            Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
            Field("text", "Text", "Text to store, as UTF-8, where no file is given."),
            Field("content_type", "Content type", "Its media type - application/pdf. Empty: by its name's extension (text/plain for text)."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v1.0";
                var site = Values.Get(context.Settings, "site") is { } named ? SitePath(named) : "root";
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/sites/{site}?$select=displayName,webUrl", null, null, cancellationToken);
                return ConnectionTestResult.Ok($"Microsoft SharePoint: signed in - the site {Words(answer?["displayName"]) ?? "?"} at {Words(answer?["webUrl"]) ?? "?"}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ItemsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Microsoft SharePoint has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v1.0";
            var site = SitePath(Values.Get(p, "site") ?? Values.Get(context.Settings, "site") ?? throw new InvalidOperationException("Give the site - kafe.sharepoint.com:/sites/Kassa, or its id (site), or the connection's."));
            var files = operation is DownloadFileOperation or UploadFileOperation or UpdateFileOperation;
            var list = operation == ListsOperation || files ? null
                : Values.Get(p, "list") is { } named ? ListId.IsMatch(named) || ListName.IsMatch(named) && named.Trim('.').Length > 0 ? Uri.EscapeDataString(named) : throw new InvalidOperationException($"'{named}' is not a list's name or id (list).")
                : throw new InvalidOperationException("Give the list - its name or id (list).");
            var id = operation is ItemOperation or UpdateItemOperation or DeleteItemOperation
                ? Values.Get(p, "id") is { } given ? ItemId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an item's id - a number (id).") : throw new InvalidOperationException("Give the item's id (id).")
                : null;
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var filter = operation == ItemsOperation && Values.Get(p, "filter") is { } asked ? Text.IsMatch(asked) ? asked : throw new InvalidOperationException($"'{asked}' is not a filter without control characters (filter).") : null;
            var values = operation is CreateItemOperation or UpdateItemOperation or UpsertItemOperation ? ValuesOf(p) : null;
            var match = operation == UpsertItemOperation
                ? Values.Get(p, "match") is { } column ? Column.IsMatch(column) ? column : throw new InvalidOperationException($"'{column}' is not a column's internal name (match).") : throw new InvalidOperationException("Give the column to match on (match).")
                : null;
            if (match is not null && !(values![match] is JsonValue key && key.GetValueKind() is JsonValueKind.String or JsonValueKind.Number))
            {
                throw new InvalidOperationException($"Give the value of '{match}' to match on, a text or a number, among the values (values).");
            }

            // A file of the site's documents: its address, its content and where it lands, each held to its shape first.
            var documents = $"{api}/sites/{site}/drive";
            string? document = null, folder = null, name = null, target = null, text = null, contentType = null, conflict = null;
            if (files)
            {
                document = operation == UploadFileOperation ? null : GraphDrive.Address(documents, Values.Get(p, "item") ?? throw new InvalidOperationException("Give the file - its id, or its path from the documents' root (item)."), "item");
                folder = operation == UploadFileOperation ? GraphDrive.Address(documents, Values.Get(p, "folder") ?? "root", "folder") : null;
                name = Values.Get(p, "name");
                target = Values.Get(p, "file");
                text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw : null; // the content as given, not trimmed
                contentType = Values.Get(p, "content_type");
                if (contentType is not null && !MediaTypeHeaderValue.TryParse(contentType, out _))
                {
                    throw new InvalidOperationException($"'{contentType}' is not a media type - application/pdf (content_type).");
                }

                if (target is not null && text is not null && operation != DownloadFileOperation)
                {
                    throw new InvalidOperationException("Give the file or text, not both.");
                }

                switch (operation)
                {
                    case DownloadFileOperation when target is null:
                        throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    case UploadFileOperation:
                        if (target is null && text is null)
                        {
                            throw new InvalidOperationException("Give the file in the site's pipeline folder to store, or text.");
                        }

                        name ??= target is null ? throw new InvalidOperationException("Give the name - text has none of its own.") : System.IO.Path.GetFileName(target.Replace('\\', '/'));
                        _ = GraphDrive.Child(folder!, name);
                        conflict = Values.Get(p, "conflict") is { } taken ? Conflicts.FirstOrDefault(known => string.Equals(known, taken, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{taken}' is not replace, rename or fail (conflict).") : "replace";
                        break;
                    case UpdateFileOperation when name is null && target is null && text is null:
                        throw new InvalidOperationException("Give a new name, or new content - a file or text.");
                    case UpdateFileOperation when name is not null:
                        _ = GraphDrive.Child(document!, name); // a new name is one name
                        break;
                }

                if ((operation == DownloadFileOperation || target is not null) && context.Files is null)
                {
                    throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
                }
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (files)
            {
                var drive = new GraphDrive(client, token, documents, DisplayName);
                yield return operation switch
                {
                    DownloadFileOperation => await drive.DownloadRecordAsync(document!, context.Files!, target!, cancellationToken),
                    UploadFileOperation => await drive.UploadRecordAsync(folder!, name!, context.Files, target, text, contentType, conflict!, cancellationToken),
                    _ => await drive.UpdateRecordAsync(document!, name, context.Files, target, text, contentType, cancellationToken),
                };
                yield break;
            }

            var lists = $"{api}/sites/{site}/lists";
            if (operation == ListsOperation)
            {
                var answer = await SendAsync(client, token, HttpMethod.Get, lists + "?$select=id,name,displayName,description,webUrl,list,createdDateTime,lastModifiedDateTime", null, null, cancellationToken);
                foreach (var item in (answer?["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return Record(item);
                }

                yield break;
            }

            var items = $"{lists}/{list}/items";
            if (operation == ListOperation)
            {
                yield return Record(await SendAsync(client, token, HttpMethod.Get, $"{lists}/{list}?$expand=columns($select=name,displayName,required,readOnly)", null, null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Microsoft SharePoint answered with no list."));
                yield break;
            }

            if (operation == ItemsOperation)
            {
                // Graph pages by its next link: followed only while it stays on the same list and names another page.
                string? next = $"{items}?$expand=fields&$top={Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture)}" + (filter is null ? "" : "&$filter=" + Uri.EscapeDataString(filter));
                var read = 0;
                while (next is not null)
                {
                    var answer = await SendAsync(client, token, HttpMethod.Get, next, null, filter is null ? null : "HonorNonIndexedQueriesWarningMayFailRandomly", cancellationToken);
                    foreach (var item in (answer?["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Item(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    var link = answer?["@odata.nextLink"] is JsonValue value && value.TryGetValue<string>(out var linked) ? linked : null;
                    if (link is not null && !(link.StartsWith(items + "?", StringComparison.OrdinalIgnoreCase) && link != next))
                    {
                        throw new InvalidOperationException("Microsoft Graph named a next page outside the list - it is not followed.");
                    }

                    next = link;
                }

                yield break;
            }

            if (operation == UpsertItemOperation)
            {
                var keyValue = values![match!]!;
                var literal = keyValue.GetValueKind() == JsonValueKind.String ? "'" + keyValue.GetValue<string>().Replace("'", "''", StringComparison.Ordinal) + "'" : keyValue.ToJsonString();
                var found = (await SendAsync(client, token, HttpMethod.Get, $"{items}?$expand=fields&$top=2&$filter={Uri.EscapeDataString($"fields/{match} eq {literal}")}", null, "HonorNonIndexedQueriesWarningMayFailRandomly", cancellationToken))?["value"] as JsonArray ?? new JsonArray();
                if (found.Count > 1)
                {
                    throw new InvalidOperationException($"More than one item has {match} {literal} - nothing is written; match on a column whose values are unique.");
                }

                id = found.Count == 1 ? Words(found[0]?["id"]) ?? throw new InvalidOperationException("Microsoft SharePoint answered with an item without an id.") : null;
            }

            if (operation == DeleteItemOperation)
            {
                await SendAsync(client, token, HttpMethod.Delete, $"{items}/{id}", null, null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["deleted"] = true });
                yield break;
            }

            if (operation == ItemOperation)
            {
                yield return Item(await SendAsync(client, token, HttpMethod.Get, $"{items}/{id}?$expand=fields", null, null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Microsoft SharePoint answered with no item."));
                yield break;
            }

            if (id is null)
            {
                // Created: Graph answers with the item and its columns.
                var created = await SendAsync(client, token, HttpMethod.Post, items, new JsonObject { ["fields"] = values!.DeepClone() }, null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Microsoft SharePoint answered with no item.");
                var record = Item(created);
                yield return operation == UpsertItemOperation ? With(record, "created", true) : record;
                yield break;
            }

            // Updated: Graph answers with the item's columns as they are now.
            var fields = await SendAsync(client, token, HttpMethod.Patch, $"{items}/{id}/fields", values!.DeepClone().AsObject(), null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Microsoft SharePoint answered with no columns.");
            var updated = Item(new JsonObject { ["id"] = id, ["fields"] = fields.DeepClone() });
            yield return operation == UpsertItemOperation ? With(updated, "created", false) : updated;
        }

        // A site's path in Graph: its id as it is, its address closed by a colon.
        private static string SitePath(string given) =>
            SiteById.IsMatch(given) || SiteByHost.IsMatch(given) ? given
            : SiteByPath.IsMatch(given) ? given.TrimEnd(':') + ":"
            : throw new InvalidOperationException($"'{given}' is not a site - kafe.sharepoint.com:/sites/Kassa, or its id (site).");

        // An item's values: a JSON object of columns by their internal names and their values.
        private static JsonObject ValuesOf(IReadOnlyDictionary<string, string> parameters)
        {
            JsonNode? parsed;
            try
            {
                parsed = Values.Get(parameters, "values") is { } given ? JsonNode.Parse(given) : throw new InvalidOperationException("Give the values - a JSON object of columns and values (values).");
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonObject values || values.Count == 0)
            {
                throw new InvalidOperationException("The values are not a JSON object of columns and values - {\"Title\": \"Kassan\"} (values).");
            }

            foreach (var (name, value) in values)
            {
                if (!Column.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a column's internal name - Title, Status, Due_x0020_date (values).");
                }

                if (value is JsonValue text && text.TryGetValue<string>(out var s) && s.Length > 0 && !Text.IsMatch(s))
                {
                    throw new InvalidOperationException($"The value of '{name}' has a control character (values).");
                }
            }

            return values;
        }

        // An item as one record: its columns, then its id, address and times.
        private static ConnectorRecord Item(JsonObject item)
        {
            var record = (item["fields"] as JsonObject ?? new JsonObject()).Where(pair => !pair.Key.StartsWith("@odata.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);
            foreach (var name in new[] { "id", "webUrl", "createdDateTime", "lastModifiedDateTime" })
            {
                if (item[name] is not null)
                {
                    record[name] = Value(item[name]);
                }
            }

            return new ConnectorRecord(record);
        }

        private static ConnectorRecord With(ConnectorRecord record, string name, object? value)
        {
            var fields = new Dictionary<string, object?>(record.Fields, StringComparer.Ordinal) { [name] = value };
            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => !pair.Key.StartsWith("@odata.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Graph's answer as JSON, a throttled call waiting as Graph says; a failure thrown with its status and Graph's code and
        // message; a success that is not JSON a failure that says so (a delete's 204 - nothing).
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, string? prefer, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (prefer is not null)
                {
                    request.Headers.TryAddWithoutValidation("Prefer", prefer);
                }

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
                throw new InvalidOperationException($"Microsoft Graph answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Words(json?["error"]?["code"]);
                var said = Words(json?["error"]?["message"]);
                var text = string.Join(": ", new[] { code, said }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }
    }
}
