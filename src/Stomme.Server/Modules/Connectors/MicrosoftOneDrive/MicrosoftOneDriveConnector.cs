#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.MicrosoftOneDrive
{
    /// <summary>
    /// Microsoft OneDrive (n8n's Microsoft OneDrive node: files and folders) over Microsoft Graph with an Entra ID app's
    /// client credentials - as Microsoft Excel 365's and SharePoint's - in a user's OneDrive (the <c>user</c> setting: an address
    /// or an id) or a drive by its id (the <c>drive</c> setting). A download is written into the pipeline run's folder and an upload
    /// read from it (<see cref="IConnectorFiles"/>, <see cref="GraphDrive"/>). A test reads the drive.
    /// </summary>
    /// <remarks>
    /// An item is its id, <c>root</c>, or its path from the drive's root (<c>/Kvitton/2026/1042.pdf</c>). <c>search</c> (Graph's
    /// search of the drive, files or folders, as n8n's two searches), <c>children</c>, <c>get</c>, <c>download</c> (Graph's
    /// redirect to Microsoft's download host followed once, without the token), <c>upload</c> (a file of the run's folder or text,
    /// into a folder, replacing, renaming or refusing a name in use), <c>update</c> (its name, its content), <c>copy</c> (Graph
    /// copies in the background: the address that follows it said), <c>move</c>, <c>delete</c> (to the drive's recycle bin),
    /// <c>share</c> (a view, edit or embed link, anyone's or the organization's) and <c>create_folder</c> (a path of folders made
    /// one by one, as n8n's node does). Every value held to its shape before a token is asked for; a success that is not JSON and
    /// Graph's own failures said as such.
    /// </remarks>
    public sealed class MicrosoftOneDriveConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string SearchOperation = "search";

        public const string ChildrenOperation = "children";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string UpdateOperation = "update";

        public const string CopyOperation = "copy";

        public const string MoveOperation = "move";

        public const string DeleteOperation = "delete";

        public const string ShareOperation = "share";

        public const string CreateFolderOperation = "create_folder";

        public const int DefaultMax = 1000;

        private static readonly Regex User = new("^(?:[^@\\s/]+@[^@\\s/]+\\.[^@\\s/]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.CultureInvariant);

        private static readonly Regex DriveId = new("^[A-Za-z0-9!_.-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly string[] LinkTypes = { "view", "edit", "embed" };

        private static readonly string[] LinkScopes = { "anonymous", "organization" };

        private static readonly string[] Conflicts = { "replace", "rename", "fail" };

        private static readonly string[] Known = { SearchOperation, ChildrenOperation, GetOperation, DownloadOperation, UploadOperation, UpdateOperation, CopyOperation, MoveOperation, DeleteOperation, ShareOperation, CreateFolderOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsoftonedrive";

        public string DisplayName => "Microsoft OneDrive";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) id (Entra ID > App registrations > the app)",
            ["client-secret"] = "Client secret (Certificates & secrets)",
            ["token-url"] = "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://www.microsoft.com/microsoft-365/onedrive/online-cloud-storage");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/onedrive");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: false, Description: "Whose OneDrive - ada@kafe.example.com, or the user's id."),
            new ConnectorConfigField("drive", "Drive", ConnectorConfigFieldType.Text, Required: false, Description: "Or a drive by its id - a document library's, a shared drive's."),
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false, Description: "The token's scope - change it only for a sovereign cloud.") { Default = DefaultScope },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ItemField = Field("item", "Item", "Its id, or its path from the drive's root - /Kvitton/2026/1042.pdf.", required: true);

        private static readonly ConnectorConfigField FolderField = Field("folder", "Folder", "A folder's id or path, or root. Empty: the drive's root.");

        private static ConnectorConfigField Max() => Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField[] ContentFields =
        {
            Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
            Field("text", "Text", "Text to store, as UTF-8, where no file is given."),
            Field("content_type", "Content type", "Its media type - application/pdf. Empty: by its name's extension (text/plain for text)."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search files and folders", "Graph's search of the drive - n8n's file and folder Search.")
            {
                Parameters = Schema(Field("query", "Query", "What to find - kvitto.", required: true), Field("what", "What", "all, files or folders.") with { Default = "all" }, Max()),
            },
            new ConnectorOperation(ChildrenOperation, "Get a folder's items", "A folder's files and folders - n8n's folder Get Children.") { Parameters = Schema(FolderField, Max()) },
            new ConnectorOperation(GetOperation, "Get an item", "A file's or a folder's name, size, address and parent - n8n's file Get.") { Parameters = Schema(ItemField) },
            new ConnectorOperation(DownloadOperation, "Download a file", "A file written into the run's pipeline folder - n8n's file Download.")
            {
                Parameters = Schema(ItemField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation(UploadOperation, "Upload a file", "A file of the run's pipeline folder, or text, stored in a folder - n8n's file Upload.")
            {
                Parameters = Schema(new[] { Field("name", "Name", "Its name in the drive. Empty: the file's own name."), FolderField }
                    .Concat(ContentFields).Append(Field("conflict", "If the name is taken", "replace (the default), rename or fail.") with { Default = "replace" }).ToArray()),
            },
            new ConnectorOperation(UpdateOperation, "Update a file", "A file's name, and its content from the run's folder or text - n8n's file Rename, and SharePoint's file Update.")
            {
                Parameters = Schema(new[] { ItemField, Field("name", "Name", "Its new name. Empty: as it is.") }.Concat(ContentFields).ToArray()),
            },
            new ConnectorOperation(CopyOperation, "Copy a file", "A copy, in a folder or another drive - n8n's file Copy; Graph copies in the background.")
            {
                Parameters = Schema(ItemField, Field("name", "Name", "The copy's name. Empty: its name."), Field("folder", "Folder", "Where the copy goes - a folder's id. Empty: beside it."), Field("drive", "Drive", "Another drive's id, when the folder is in one.")),
            },
            new ConnectorOperation(MoveOperation, "Move an item", "A file or a folder into another folder, renamed or not - n8n's file and folder Move.")
            {
                Parameters = Schema(ItemField, Field("folder", "Folder", "Its new folder's id or path, or root."), Field("name", "Name", "Its new name. Empty: as it is.")),
            },
            new ConnectorOperation(DeleteOperation, "Delete an item", "A file or a folder to the drive's recycle bin - n8n's file and folder Delete.") { Parameters = Schema(ItemField) },
            new ConnectorOperation(ShareOperation, "Share an item", "A link to a file or a folder - n8n's file and folder Share.")
            {
                Parameters = Schema(ItemField, Field("type", "Link", "view, edit or embed.", required: true), Field("scope", "For", "anonymous (anyone with the link) or organization.", required: true)),
            },
            new ConnectorOperation(CreateFolderOperation, "Create a folder", "A folder - a path of them, a/b/c, made one by one - n8n's folder Create.")
            {
                Parameters = Schema(Field("name", "Name", "Its name, or a path of names - Kvitton/2026.", required: true), FolderField, Field("conflict", "If the name is taken", "fail (the default), rename or replace.") with { Default = "fail" }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var (drive, client) = await OpenAsync(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                using (client)
                {
                    var found = await drive.JsonAsync(HttpMethod.Get, drive.Root + "?$select=id,name,driveType,owner", null, cancellationToken);
                    var owner = found["owner"]?["user"]?["displayName"] is JsonValue named && named.TryGetValue<string>(out var who) ? who : null;
                    return ConnectionTestResult.Ok($"OneDrive: {(owner is null ? "the drive" : owner + "'s drive")} answers.");
                }
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SearchOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Microsoft OneDrive has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for: an item's address is checked under a stand-in drive's.
            const string Probe = "https://graph.microsoft.com/v1.0/drives/probe";
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var item = operation is GetOperation or DownloadOperation or UpdateOperation or CopyOperation or MoveOperation or DeleteOperation or ShareOperation
                ? Values.Get(p, "item") ?? throw new InvalidOperationException("Give the item - its id, or its path from the drive's root.")
                : null;
            if (item is not null)
            {
                _ = GraphDrive.Address(Probe, item, "item");
            }

            var folder = Values.Get(p, "folder");
            if (folder is not null && operation != CopyOperation)
            {
                _ = GraphDrive.Address(Probe, folder, "folder");
            }

            var name = Values.Get(p, "name") is { } named ? OneLine(named, "name") : null;
            if (name is not null && operation is UpdateOperation or MoveOperation or CopyOperation)
            {
                _ = GraphDrive.Child(Probe + "/root", name); // a new name is one name
            }

            var target = Values.Get(p, "file");
            var text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw : null; // the content as given, not trimmed
            var contentType = Values.Get(p, "content_type");
            if (contentType is not null && !MediaTypeHeaderValue.TryParse(contentType, out _))
            {
                throw new InvalidOperationException($"'{contentType}' is not a media type - application/pdf (content_type).");
            }

            if (operation is UploadOperation or UpdateOperation && target is not null && text is not null)
            {
                throw new InvalidOperationException("Give the file or text, not both.");
            }

            string? query = null;
            string? linkType = null;
            string? linkScope = null;
            var what = "all";
            switch (operation)
            {
                case SearchOperation:
                    query = Values.Get(p, "query") is { } q ? OneLine(q, "query") : throw new InvalidOperationException("Give the query - what to find.");
                    what = Values.GetOrDefault(p, "what", "all").ToLowerInvariant();
                    if (what is not ("all" or "files" or "folders"))
                    {
                        throw new InvalidOperationException($"'{what}' is not all, files or folders (what).");
                    }

                    break;
                case DownloadOperation when target is null:
                    throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                case UploadOperation:
                    if (target is null && text is null)
                    {
                        throw new InvalidOperationException("Give the file in the site's pipeline folder to store, or text.");
                    }

                    var uploaded = name ?? (target is null ? throw new InvalidOperationException("Give the name - text has none of its own.") : System.IO.Path.GetFileName(target.Replace('\\', '/')));
                    _ = GraphDrive.Child(Probe + "/root", uploaded);
                    break;
                case UpdateOperation when name is null && target is null && text is null:
                    throw new InvalidOperationException("Give a new name, or new content - a file or text.");
                case ShareOperation:
                    linkType = Choice(p, "type", LinkTypes, "view, edit or embed") ?? throw new InvalidOperationException("Give the link - view, edit or embed.");
                    linkScope = Choice(p, "scope", LinkScopes, "anonymous or organization") ?? throw new InvalidOperationException("Give the scope - anonymous or organization.");
                    break;
                case CreateFolderOperation:
                    var path = name ?? throw new InvalidOperationException("Give the name - a folder's, or a path of them.");
                    foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        _ = GraphDrive.Child(Probe + "/root", segment);
                    }

                    break;
                case MoveOperation when folder is null && name is null:
                    throw new InvalidOperationException("Give the folder it moves to, or its new name.");
                case CopyOperation:
                    if (folder is not null && !DriveId.IsMatch(folder))
                    {
                        throw new InvalidOperationException($"'{folder}' is not a folder's id (folder).");
                    }

                    if (Values.Get(p, "drive") is { } other && !DriveId.IsMatch(other))
                    {
                        throw new InvalidOperationException($"'{other}' is not a drive's id (drive).");
                    }

                    break;
            }

            var conflict = operation is UploadOperation or CreateFolderOperation ? Choice(p, "conflict", Conflicts, "replace, rename or fail") ?? (operation == UploadOperation ? "replace" : "fail") : null;
            if ((operation == DownloadOperation || (operation is UploadOperation or UpdateOperation && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            var (drive, client) = await OpenAsync(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            using (client)
            {
                switch (operation)
                {
                    case SearchOperation:
                        {
                            var escaped = Uri.EscapeDataString(query!.Replace("'", "''", StringComparison.Ordinal));
                            Func<ConnectorRecord, bool> keep = what switch
                            {
                                "files" => record => record.Fields.ContainsKey("file"),
                                "folders" => record => record.Fields.ContainsKey("folder"),
                                _ => _ => true,
                            };
                            await foreach (var found in drive.PagesAsync($"{drive.Root}/root/search(q='{escaped}')", max, keep, cancellationToken))
                            {
                                yield return found;
                            }

                            break;
                        }

                    case ChildrenOperation:
                        await foreach (var child in drive.PagesAsync(drive.Item(folder ?? "root", "folder") + "/children", max, null, cancellationToken))
                        {
                            yield return child;
                        }

                        break;

                    case GetOperation:
                        yield return GraphDrive.Record(await drive.JsonAsync(HttpMethod.Get, drive.Item(item!, "item"), null, cancellationToken));
                        break;

                    case DownloadOperation:
                        yield return await drive.DownloadRecordAsync(drive.Item(item!, "item"), context.Files!, target!, cancellationToken);
                        break;

                    case UploadOperation:
                        yield return await drive.UploadRecordAsync(drive.Item(folder ?? "root", "folder"), name ?? System.IO.Path.GetFileName(target!.Replace('\\', '/')), context.Files, target, text, contentType, conflict!, cancellationToken);
                        break;

                    case UpdateOperation:
                        yield return await drive.UpdateRecordAsync(drive.Item(item!, "item"), name, context.Files, target, text, contentType, cancellationToken);
                        break;

                    case CopyOperation:
                        {
                            var body = new JsonObject();
                            if (name is not null)
                            {
                                body["name"] = name;
                            }

                            if (folder is not null || Values.Get(p, "drive") is not null)
                            {
                                var parent = new JsonObject();
                                if (Values.Get(p, "drive") is { } other)
                                {
                                    parent["driveId"] = other;
                                }

                                if (folder is not null)
                                {
                                    parent["id"] = folder;
                                }

                                body["parentReference"] = parent;
                            }

                            // Graph copies in the background (202): the address that follows the copy is said, not fetched.
                            var (status, location) = await drive.AcceptedAsync(drive.Item(item!, "item") + "/copy", body, cancellationToken);
                            yield return Fields(("item", item), ("status", status), ("location", location));
                            break;
                        }

                    case MoveOperation:
                        {
                            var body = new JsonObject();
                            if (folder is not null)
                            {
                                var into = await drive.JsonAsync(HttpMethod.Get, drive.Item(folder, "folder") + "?$select=id", null, cancellationToken);
                                body["parentReference"] = new JsonObject { ["id"] = Text(into["id"]) };
                            }

                            if (name is not null)
                            {
                                body["name"] = name;
                            }

                            yield return GraphDrive.Record(await drive.JsonAsync(HttpMethod.Patch, drive.Item(item!, "item"), body, cancellationToken));
                            break;
                        }

                    case DeleteOperation:
                        await drive.JsonAsync(HttpMethod.Delete, drive.Item(item!, "item"), null, cancellationToken);
                        yield return Fields(("item", item), ("deleted", true));
                        break;

                    case ShareOperation:
                        {
                            var link = await drive.JsonAsync(HttpMethod.Post, drive.Item(item!, "item") + "/createLink", new JsonObject { ["type"] = linkType, ["scope"] = linkScope }, cancellationToken);
                            yield return Fields(("item", item), ("id", Text(link["id"])), ("roles", link["roles"]?.ToJsonString()), ("link", Text(link["link"]?["webUrl"])), ("type", Text(link["link"]?["type"])), ("scope", Text(link["link"]?["scope"])));
                            break;
                        }

                    case CreateFolderOperation:
                        {
                            // A path of folders made one by one, each in the one before it, as n8n's node does.
                            var parent = drive.Item(folder ?? "root", "folder");
                            JsonObject made = new();
                            foreach (var segment in name!.Split('/', StringSplitOptions.RemoveEmptyEntries))
                            {
                                made = await drive.JsonAsync(HttpMethod.Post, parent + "/children", new JsonObject { ["name"] = segment, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = conflict }, cancellationToken);
                                parent = drive.Root + "/items/" + Uri.EscapeDataString(Text(made["id"]) ?? throw new InvalidOperationException("Microsoft OneDrive made a folder and named no id."));
                            }

                            yield return GraphDrive.Record(made);
                            break;
                        }
                }
            }
        }

        // The drive the connection names - a user's OneDrive or a drive by its id - and a client with a token for it.
        private async Task<(GraphDrive Drive, HttpClient Client)> OpenAsync(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var api = Values.BaseUrl(endpoint, DefaultEndpoint) + "/v1.0";
            string root;
            if (Values.Get(settings, "drive") is { } drive)
            {
                root = DriveId.IsMatch(drive) ? $"{api}/drives/{Uri.EscapeDataString(drive)}" : throw new InvalidOperationException($"'{drive}' is not a drive's id.");
            }
            else
            {
                var user = Values.Get(settings, "user") ?? throw new InvalidOperationException("Give whose OneDrive - the user's address or id - or a drive's id.");
                root = User.IsMatch(user) ? $"{api}/users/{Uri.EscapeDataString(user)}/drive" : throw new InvalidOperationException($"'{user}' is not a user - an address or a user id.");
            }

            var token = await tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);
            var client = egress.CreateHttpClient(timeout);
            return (new GraphDrive(client, token, root, DisplayName), client);
        }

        // One of the values allowed, in its own spelling; null when none is given.
        private static string? Choice(IReadOnlyDictionary<string, string> p, string name, string[] allowed, string listed) =>
            Values.Get(p, name) is { } given
                ? allowed.FirstOrDefault(known => string.Equals(known, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not {listed} ({name}).")
                : null;

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
