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

namespace Stomme.Modules.Connectors.Dropbox
{
    /// <summary>
    /// Dropbox (n8n's Dropbox node: files, folders and search) over Dropbox's API v2 - its RPC calls at
    /// <c>https://api.dropboxapi.com/2</c>, a file's bytes at <c>https://content.dropboxapi.com/2</c> - and the OAuth2
    /// REFRESH-TOKEN scheme: the app's key and secret and a refresh token of an offline grant, exchanged at Dropbox's token
    /// endpoint through the shared <see cref="OAuth2TokenProvider"/>, as Google Drive's is (Dropbox issues no long-lived access
    /// token any more). A download is written into the pipeline run's folder and an upload read from it
    /// (<see cref="IConnectorFiles"/>). A test names whose account the token signs in to.
    /// </summary>
    /// <remarks>
    /// <para><c>list</c> (a folder's entries, its subfolders' too when recursive, page by page up to <c>max</c>), <c>search</c>
    /// (by name, in a folder, by category and extension, deleted files instead when asked), <c>get</c>, <c>download</c>,
    /// <c>upload</c> (a file of the run's folder or text, overwriting as n8n's node does or added beside, renamed when asked),
    /// <c>create_folder</c>, <c>copy</c>, <c>move</c> and <c>delete</c>. A list's and a search's entries carry the names n8n's
    /// node gives them (<c>pathDisplay</c>, <c>lastModifiedServer</c>...); every other answer is Dropbox's metadata as Dropbox
    /// names it, its kind as <c>type</c>.</para>
    /// <para>A file or a folder is its path from the root (<c>/Kvitton/2026/1042.pdf</c> - a missing leading slash added, a
    /// trailing one dropped) or its id (<c>id:a4ayc_80_OEAAAAAAAAAXw</c>); every value held to its shape before a token is asked
    /// for. A team's space is read from its root (the <c>root</c> setting, as n8n's node does for Full Dropbox access). A call's
    /// arguments for the content host travel in its <c>Dropbox-API-Arg</c> header, as ASCII JSON (anything else escaped).
    /// Dropbox's refusals said with their status and Dropbox's own summary.</para>
    /// </remarks>
    public sealed class DropboxConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.dropboxapi.com";

        public const string ContentEndpoint = "https://content.dropboxapi.com";

        public const string DefaultTokenUrl = "https://api.dropboxapi.com/oauth2/token";

        public const string ListOperation = "list";

        public const string SearchOperation = "search";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string CreateFolderOperation = "create_folder";

        public const string CopyOperation = "copy";

        public const string MoveOperation = "move";

        public const string DeleteOperation = "delete";

        public const int DefaultMax = 1000;

        /// <summary>The most Dropbox takes in one upload call, 150 MiB.</summary>
        public const long MaxUploadBytes = 150L * 1024 * 1024;

        private static readonly Regex Id = new("^id:[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly string[] FileCategories = { "image", "document", "pdf", "spreadsheet", "presentation", "audio", "video", "folder", "paper", "other" };

        private static readonly string[] Known = { ListOperation, SearchOperation, GetOperation, DownloadOperation, UploadOperation, CreateFolderOperation, CopyOperation, MoveOperation, DeleteOperation };

        // A list's and a search's entry, as n8n's node names its fields.
        private static readonly (string Name, string From)[] Entry =
        {
            ("id", "id"), ("name", "name"), ("rev", "rev"), ("lastModifiedClient", "client_modified"), ("lastModifiedServer", "server_modified"),
            ("contentSize", "size"), ("type", ".tag"), ("contentHash", "content_hash"), ("pathLower", "path_lower"), ("pathDisplay", "path_display"),
            ("hasExplicitSharedMembers", "has_explicit_shared_members"), ("isDownloadable", "is_downloadable"),
        };

        // JSON as written in a body: names, accents and text as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "dropbox";

        public string DisplayName => "Dropbox";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://www.dropbox.com");

        public Uri ApiDocumentationUrl { get; } = new("https://www.dropbox.com/developers/documentation/http/documentation");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "App key (App Console > the app > Settings)",
            ["client-secret"] = "App secret (the same app)",
            ["refresh-token"] = "Refresh token (an offline grant of the app's permissions)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("root", "Root", ConnectorConfigFieldType.Text, Required: false, Description: "home - the account's own folders (the default); team - a team's space, from its root, as n8n's Full Dropbox access.") { Default = "home" },
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Dropbox's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField Flag(string name, string label, string description, bool fallback = false) => Field(name, label, description, type: ConnectorConfigFieldType.Boolean) with { Default = fallback ? "true" : "false" };

        private static ConnectorConfigField Max() => Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField PathField = Field("path", "Path", "Its path - /Kvitton/2026/1042.pdf - or its id, id:...", required: true);

        private static readonly ConnectorConfigField RenameField = Flag("autorename", "Rename when taken", "Give it a free name when its name is taken, rather than refuse.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ListOperation, "List a folder", "A folder's files and folders - its subfolders' too when recursive - n8n's folder List.")
            {
                Parameters = Schema(
                    Field("path", "Folder", "Its path - /Kvitton - or its id. Empty: the root."),
                    Flag("recursive", "Subfolders too", "Every subfolder's entries as well."),
                    Flag("deleted", "Deleted too", "Deleted files and folders as well."),
                    Flag("mounted", "Mounted folders", "Folders shared with the account and mounted in it.", fallback: true),
                    Flag("non_downloadable", "Files that cannot be downloaded", "Cloud documents and the like, which have no bytes to download.", fallback: true),
                    Flag("shared_members", "Shared or not", "Say whether each entry is shared with members of its own."),
                    Max()),
            },
            new ConnectorOperation(SearchOperation, "Search", "Files and folders by name - in a folder, by category and extension - n8n's Search.")
            {
                Parameters = Schema(
                    Field("query", "Query", "What their names contain - kvitto.", required: true),
                    Field("path", "Folder", "Search only under this folder - /Kvitton."),
                    Field("categories", "Categories", "Comma-separated: image, document, pdf, spreadsheet, presentation, audio, video, folder, paper or other."),
                    Field("extensions", "Extensions", "Comma-separated, without the dot - pdf, csv."),
                    Flag("contents", "In their contents too", "Search what files contain as well as their names (a Dropbox Business feature)."),
                    Flag("deleted", "Deleted files", "Search deleted files instead."),
                    Max()),
            },
            new ConnectorOperation(GetOperation, "Get a file or a folder", "Its name, path, size, dates and revision.") { Parameters = Schema(PathField) },
            new ConnectorOperation(DownloadOperation, "Download a file", "A file written into the run's pipeline folder - n8n's file Download.")
            {
                Parameters = Schema(PathField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation(UploadOperation, "Upload a file", "A file of the run's pipeline folder, or text, stored at a path - n8n's file Upload.")
            {
                Parameters = Schema(
                    Field("path", "Path", "Where it is stored - /Kvitton/1042.pdf; ending with / the file's own name in that folder.", required: true),
                    Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
                    Field("text", "Text", "Text to store, as UTF-8, where no file is given."),
                    Field("mode", "If it exists", "overwrite (the default, as n8n's node) or add - stored beside it.") with { Default = "overwrite" },
                    RenameField),
            },
            new ConnectorOperation(CreateFolderOperation, "Create a folder", "A folder, and the folders on its path - n8n's folder Create.") { Parameters = Schema(Field("path", "Folder", "Its path - /Kvitton/2026.", required: true), RenameField) },
            new ConnectorOperation(CopyOperation, "Copy a file or a folder", "To another path - n8n's file and folder Copy.")
            {
                Parameters = Schema(Field("from", "From", "Its path, or its id.", required: true), Field("to", "To", "Its new path - /Arkiv/1042.pdf.", required: true), RenameField),
            },
            new ConnectorOperation(MoveOperation, "Move a file or a folder", "To another path, or another name - n8n's file and folder Move.")
            {
                Parameters = Schema(Field("from", "From", "Its path, or its id.", required: true), Field("to", "To", "Its new path - /Arkiv/1042.pdf.", required: true), RenameField),
            },
            new ConnectorOperation(DeleteOperation, "Delete a file or a folder", "A file, or a folder and everything in it - n8n's file and folder Delete.") { Parameters = Schema(PathField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var account = session.Account ?? await session.RpcAsync("users/get_current_account", null, cancellationToken);
                var name = Text(account["name"]?["display_name"]);
                return ConnectionTestResult.Ok($"Dropbox: {(name is null ? "the account" : name + "'s account")} answers.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ListOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Dropbox has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var autorename = Values.GetBool(p, "autorename", false);
            string? path = null;
            string? from = null;
            string? to = null;
            string? target = null;
            string? text = null;
            string? query = null;
            var mode = "overwrite";
            JsonObject? search = null;
            switch (operation)
            {
                case ListOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: true);
                    break;
                case SearchOperation:
                    query = Values.Get(p, "query") is { } q ? OneLine(q, "query") : throw new InvalidOperationException("Give the query - what their names contain.");
                    search = new JsonObject
                    {
                        ["max_results"] = Math.Min(max, 1000),
                        ["file_status"] = Values.GetBool(p, "deleted", false) ? "deleted" : "active",
                        ["filename_only"] = !Values.GetBool(p, "contents", false),
                    };
                    if (Values.Get(p, "path") is { } under && PathOf(under, "path", root: true) is { Length: > 0 } folder)
                    {
                        search["path"] = folder;
                    }

                    if (List(p, "categories") is { Count: > 0 } categories)
                    {
                        var unknown = categories.FirstOrDefault(category => !FileCategories.Contains(category, StringComparer.OrdinalIgnoreCase));
                        search["file_categories"] = unknown is null
                            ? new JsonArray(categories.Select(category => (JsonNode)category.ToLowerInvariant()).ToArray())
                            : throw new InvalidOperationException($"'{unknown}' is not a category - {string.Join(", ", FileCategories)} (categories).");
                    }

                    if (List(p, "extensions") is { Count: > 0 } extensions)
                    {
                        search["file_extensions"] = new JsonArray(extensions.Select(extension => (JsonNode)extension.TrimStart('.')).ToArray());
                    }

                    break;
                case GetOperation or DeleteOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: false);
                    break;
                case DownloadOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: false);
                    target = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    break;
                case UploadOperation:
                    target = Values.Get(p, "file");
                    text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw : null; // the content as given, not trimmed
                    if ((target is null) == (text is null))
                    {
                        throw new InvalidOperationException(target is null ? "Give the file in the site's pipeline folder to store, or text." : "Give the file or text, not both.");
                    }

                    var given = Values.Get(p, "path") ?? throw new InvalidOperationException("Give the path it is stored at - /Kvitton/1042.pdf.");
                    if (given.EndsWith('/'))
                    {
                        given += target is null ? throw new InvalidOperationException("Give the path with its name - text has none of its own.") : System.IO.Path.GetFileName(target.Replace('\\', '/'));
                    }

                    path = PathOf(given, "path", root: false);
                    mode = Values.GetOrDefault(p, "mode", "overwrite").ToLowerInvariant() is ("overwrite" or "add") and var chosen ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "mode")}' is not overwrite or add (mode).");
                    break;
                case CreateFolderOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: false);
                    break;
                case CopyOperation or MoveOperation:
                    from = PathOf(Values.Get(p, "from"), "from", root: false);
                    to = PathOf(Values.Get(p, "to"), "to", root: false);
                    break;
            }

            if ((operation == DownloadOperation || (operation == UploadOperation && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case ListOperation:
                    {
                        var first = new JsonObject
                        {
                            ["path"] = path,
                            ["recursive"] = Values.GetBool(p, "recursive", false),
                            ["include_deleted"] = Values.GetBool(p, "deleted", false),
                            ["include_mounted_folders"] = Values.GetBool(p, "mounted", true),
                            ["include_non_downloadable_files"] = Values.GetBool(p, "non_downloadable", true),
                            ["include_has_explicit_shared_members"] = Values.GetBool(p, "shared_members", false),
                            ["limit"] = Math.Min(max, 2000),
                        };
                        await foreach (var entry in session.PagesAsync("files/list_folder", first, "entries", max, item => item, cancellationToken))
                        {
                            yield return Named(entry);
                        }

                        break;
                    }

                case SearchOperation:
                    {
                        var first = new JsonObject { ["query"] = query, ["options"] = search };
                        await foreach (var match in session.PagesAsync("files/search_v2", first, "matches", max, item => item["metadata"]?["metadata"] as JsonObject, cancellationToken))
                        {
                            yield return Named(match);
                        }

                        break;
                    }

                case GetOperation:
                    yield return Metadata(await session.RpcAsync("files/get_metadata", new JsonObject { ["path"] = path }, cancellationToken));
                    break;

                case DownloadOperation:
                    yield return await session.DownloadAsync(path!, context.Files!, target!, cancellationToken);
                    break;

                case UploadOperation:
                    {
                        var arg = new JsonObject { ["path"] = path, ["mode"] = mode, ["autorename"] = autorename, ["mute"] = false };
                        var stored = target is null
                            ? await session.UploadAsync(arg, Encoding.UTF8.GetBytes(text!), null, null, cancellationToken)
                            : await session.UploadAsync(arg, null, context.Files, target, cancellationToken);
                        stored[".tag"] ??= "file";
                        yield return Metadata(stored);
                        break;
                    }

                case CreateFolderOperation:
                    {
                        var made = await session.RpcAsync("files/create_folder_v2", new JsonObject { ["path"] = path, ["autorename"] = autorename }, cancellationToken);
                        var folder = made["metadata"] as JsonObject ?? new JsonObject();
                        folder[".tag"] ??= "folder";
                        yield return Metadata(folder);
                        break;
                    }

                case CopyOperation or MoveOperation:
                    {
                        var body = new JsonObject { ["from_path"] = from, ["to_path"] = to, ["autorename"] = autorename };
                        var done = await session.RpcAsync(operation == CopyOperation ? "files/copy_v2" : "files/move_v2", body, cancellationToken);
                        yield return Metadata(done["metadata"] as JsonObject ?? new JsonObject());
                        break;
                    }

                case DeleteOperation:
                    {
                        var deleted = await session.RpcAsync("files/delete_v2", new JsonObject { ["path"] = path }, cancellationToken);
                        yield return Metadata(deleted["metadata"] as JsonObject ?? new JsonObject(), ("deleted", true));
                        break;
                    }
            }
        }

        /// <summary>
        /// A Dropbox path from what a step gives: <c>""</c> for the root (where <paramref name="root"/> allows it), an id as it is
        /// (<c>id:...</c>), or a path from the root - a leading slash added, a trailing one dropped, no empty, <c>.</c> or <c>..</c>
        /// segment and no control character.
        /// </summary>
        public static string PathOf(string? given, string what, bool root)
        {
            var value = (given ?? string.Empty).Trim();
            if (value.Length == 0 || value == "/")
            {
                return root ? string.Empty : throw new InvalidOperationException($"Give a path - /Kvitton/1042.pdf - or an id ({what}).");
            }

            if (value.StartsWith("id:", StringComparison.Ordinal))
            {
                return Id.IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not an id - id: and Dropbox's letters ({what}).");
            }

            var segments = value.Trim('/').Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
            {
                throw new InvalidOperationException($"'{given}' is not a path - /Kvitton/2026/1042.pdf ({what}).");
            }

            return "/" + string.Join("/", segments);
        }

        private static List<string> List(IReadOnlyDictionary<string, string> p, string name) =>
            (Values.Get(p, name) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // A list's or a search's entry, as n8n's node names its fields - only those Dropbox gave.
        private static ConnectorRecord Named(JsonObject entry)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, from) in Entry)
            {
                if (entry.TryGetPropertyValue(from, out var value))
                {
                    fields[name] = Clr(value);
                }
            }

            return new ConnectorRecord(fields);
        }

        // Dropbox's metadata as Dropbox names it, its kind ('.tag') as 'type', and what the operation adds.
        private static ConnectorRecord Metadata(JsonObject metadata, params (string Name, object? Value)[] added)
        {
            var fields = metadata.ToDictionary(pair => pair.Key == ".tag" ? "type" : pair.Key, pair => Clr(pair.Value), StringComparer.Ordinal);
            foreach (var (name, value) in added)
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        private static object? Clr(JsonNode? node) => node is null ? null : JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(node));

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        /// <summary>One signed-in Dropbox: its RPC and content addresses, its token, and a team's root when the connection reads one.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;
            private readonly string content;
            private string? pathRoot;

            private Session(HttpClient client, string token, string api, string content)
            {
                this.client = client;
                this.token = token;
                this.api = api;
                this.content = content;
            }

            /// <summary>The account, when opening read it (a team's root).</summary>
            public JsonObject? Account { get; private set; }

            public static async Task<Session> OpenAsync(DropboxConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.GetOrDefault(settings, "root", "home").ToLowerInvariant();
                if (root is not ("home" or "team"))
                {
                    throw new InvalidOperationException($"'{Values.Get(settings, "root")}' is not home or team (root).");
                }

                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Dropbox's API - its address alone, {DefaultEndpoint}.");
                }

                // Dropbox's own API sends a file's bytes through its content host; a stand-in serves both.
                var content = string.Equals(api, DefaultEndpoint, StringComparison.OrdinalIgnoreCase) ? ContentEndpoint : api;
                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                var session = new Session(egress.CreateHttpClient(timeout), token, api + "/2/", content + "/2/");
                if (root == "team")
                {
                    // A team's space from its root: the account's root namespace named on every call after this one.
                    session.Account = await session.RpcAsync("users/get_current_account", null, cancellationToken);
                    var id = Text(session.Account["root_info"]?["root_namespace_id"]) ?? throw new InvalidOperationException("Dropbox named no root for the account's team.");
                    session.pathRoot = new JsonObject { [".tag"] = "root", ["root"] = id }.ToJsonString();
                }

                return session;
            }

            public void Dispose() => client.Dispose();

            public async Task<JsonObject> RpcAsync(string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = Request(api + route);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                return await ReadAsync(response, cancellationToken);
            }

            /// <summary>
            /// A listing page by page - its first call, then <c>{route}/continue</c> (a search's <c>search/continue_v2</c>) with
            /// the cursor Dropbox gave while it says it has more - each page's <paramref name="items"/>, up to <paramref name="max"/>.
            /// </summary>
            public async IAsyncEnumerable<JsonObject> PagesAsync(string route, JsonObject first, string items, int max, Func<JsonObject, JsonObject?> pick, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                var page = await RpcAsync(route, first, cancellationToken);
                var next = route == "files/search_v2" ? "files/search/continue_v2" : route + "/continue";
                while (true)
                {
                    foreach (var item in (page[items] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        if (pick(item) is { } picked)
                        {
                            read++;
                            yield return picked;
                        }
                    }

                    // On while Dropbox says there is more and names where it goes on from; never the same call again.
                    if (read == max || page["has_more"] is not JsonValue more || !more.TryGetValue<bool>(out var has) || !has || Text(page["cursor"]) is not { Length: > 0 } cursor)
                    {
                        yield break;
                    }

                    page = await RpcAsync(next, new JsonObject { ["cursor"] = cursor }, cancellationToken);
                }
            }

            /// <summary>A file's bytes from the content host written into the run's folder; its metadata (Dropbox-API-Result) checked against the folder's cap first.</summary>
            public async Task<ConnectorRecord> DownloadAsync(string path, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                var arg = Ascii(new JsonObject { ["path"] = path });
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = Request(content + "files/download");
                    request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
                    return request;
                }, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, await response.Content.ReadAsStringAsync(cancellationToken));
                }

                var meta = response.Headers.TryGetValues("Dropbox-API-Result", out var results) && JsonNode.Parse(results.First()) is JsonObject parsed ? parsed : new JsonObject();
                var name = Text(meta["name"]) ?? System.IO.Path.GetFileName(path);
                var size = meta["size"] is JsonValue sized && sized.TryGetValue<long>(out var bytes) ? bytes : response.Content.Headers.ContentLength;
                if (size is { } known && known > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{name}' is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var written = file.EndsWith('/') ? file + Safe(name) : file;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var length = await files.WriteAsync(written, stream, cancellationToken);
                meta[".tag"] ??= "file";
                return Metadata(meta, ("file", written), ("size", length));
            }

            /// <summary>An upload to the content host: text, or a file of the run's folder - the same bytes from the start for each attempt - its metadata back.</summary>
            public async Task<JsonObject> UploadAsync(JsonObject arg, byte[]? text, IConnectorFiles? files, string? file, CancellationToken cancellationToken)
            {
                var header = Ascii(arg);
                HttpRequestMessage Build(HttpContent body, long length)
                {
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    body.Headers.ContentLength = length;
                    var request = Request(content + "files/upload");
                    request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", header);
                    request.Content = body;
                    return request;
                }

                if (text is not null)
                {
                    if (text.LongLength > MaxUploadBytes)
                    {
                        throw new InvalidOperationException($"The text is {text.LongLength} bytes; Dropbox takes at most {MaxUploadBytes} in one upload.");
                    }

                    using var sent = await RetryAfterPolicy.Default.SendAsync(client, () => Build(new ByteArrayContent(text), text.LongLength), cancellationToken);
                    return await ReadAsync(sent, cancellationToken);
                }

                await using var opened = await files!.OpenReadAsync(file!, cancellationToken);
                if (opened.Length > MaxUploadBytes)
                {
                    throw new InvalidOperationException($"'{opened.Name}' is {opened.Length} bytes; Dropbox takes at most {MaxUploadBytes} in one upload.");
                }

                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    opened.Content.Position = 0;
                    return Build(new StreamContent(new LentStream(opened.Content)), opened.Length);
                }, cancellationToken);
                return await ReadAsync(response, cancellationToken);
            }

            private HttpRequestMessage Request(string url)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (pathRoot is not null)
                {
                    request.Headers.TryAddWithoutValidation("Dropbox-API-Path-Root", pathRoot);
                }

                return request;
            }

            // A header's JSON: ASCII alone, anything else escaped (å), as Dropbox asks of its Dropbox-API-Arg.
            private static string Ascii(JsonObject arg) => arg.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });

            private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, payload);
                }

                try
                {
                    return payload.Length == 0 ? new JsonObject() : JsonNode.Parse(payload) as JsonObject ?? throw new InvalidOperationException("Dropbox answered with JSON that is not an object - check the connection's address.");
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException($"Dropbox answered {(int)response.StatusCode} with something that is not JSON - check the connection's address.");
                }
            }

            // Dropbox's refusal: its status and its own summary (path/not_found/..), or the first line of its text.
            private static InvalidOperationException Failure(HttpResponseMessage response, string payload)
            {
                string? summary = null;
                try
                {
                    summary = Text((JsonNode.Parse(payload) as JsonObject)?["error_summary"]);
                }
                catch (JsonException)
                {
                }

                summary ??= payload.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line.Length > 300 ? line[..300] + "..." : line : null;
                return new InvalidOperationException($"Dropbox answered {(int)response.StatusCode}{(summary is null ? "." : ": " + summary)}");
            }

            // A file's own name as a file of the folder: its unsafe characters replaced.
            private static string Safe(string name)
            {
                var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
                var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
                return safe.Length == 0 ? "file" : safe;
            }
        }
    }
}
