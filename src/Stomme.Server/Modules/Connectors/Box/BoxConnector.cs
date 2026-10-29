#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.Box
{
    /// <summary>
    /// Box (n8n's Box node: files and folders) over Box's API 2.0 - <c>https://api.box.com/2.0</c>, uploads at
    /// <c>https://upload.box.com/api/2.0</c> - as a SERVER app: Box's Client Credentials Grant, the app's client id and secret
    /// and the enterprise or the user it acts as (<c>box_subject_type</c> and <c>box_subject_id</c>), exchanged through the shared
    /// <see cref="OAuth2TokenProvider"/>. A refresh token a user granted is not taken: Box rotates it at every use, and a
    /// connection's credentials are not written back (n8n's OAuth2 said at import). A download is written into the pipeline
    /// run's folder and an upload read from it (<see cref="IConnectorFiles"/>). A test names whose account the app acts as.
    /// </summary>
    /// <remarks>
    /// <para><c>list</c> (a folder's items, page by page through Box's marker - n8n's node has none), <c>search</c> (by a query,
    /// files or folders, in folders, by extension, in the trash, as n8n's node asks), <c>get</c>, <c>download</c> (Box answers
    /// with its download host's address - followed through the same egress, the token never sent there), <c>upload</c> (a file
    /// of the run's folder or text, into a folder - refused when its name is taken, as n8n's node is, or its new version when
    /// asked), <c>copy</c>, <c>move</c> (and rename), <c>delete</c>, <c>folder_get</c>, <c>folder_create</c>,
    /// <c>folder_update</c> (its name, description, parent and tags), <c>folder_delete</c> (with what is in it when asked),
    /// <c>share</c> (a collaboration: a user by address or id, or a group, as a role - n8n's Share) and <c>link</c> (a shared
    /// link: open, company or collaborators). Each item answers as Box names its fields.</para>
    /// <para>An item is its id, Box's digits (<c>0</c> the root folder); every value held to its shape before a token is asked
    /// for. Box's refusals said with their status, its code and its message.</para>
    /// </remarks>
    public sealed class BoxConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.box.com";

        public const string UploadEndpoint = "https://upload.box.com";

        public const string DefaultTokenUrl = "https://api.box.com/oauth2/token";

        public const int DefaultMax = 100;

        /// <summary>The most Box lists in one page of a folder's items.</summary>
        public const int PageSize = 1000;

        private static readonly string[] Known =
        {
            "list", "search", "get", "download", "upload", "copy", "move", "delete", "folder_get", "folder_create", "folder_update", "folder_delete", "share", "link",
        };

        private static readonly Regex Id = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        // A collaboration's role as Box names it, n8n's names read as well.
        private static readonly (string N8n, string Box)[] Roles =
        {
            ("coOwner", "co-owner"), ("editor", "editor"), ("previewer", "previewer"), ("previewerUploader", "previewer uploader"), ("uploader", "uploader"), ("viewer", "viewer"), ("viewerUploader", "viewer uploader"),
        };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "box";

        public string DisplayName => "Box";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://www.box.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.box.com/reference/");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Client id (Developer Console > the app > Configuration)",
            ["client-secret"] = "Client secret (the same app)",
            ["token-url"] = "Token endpoint - https://api.box.com/oauth2/token",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("subject_type", "Acts as", ConnectorConfigFieldType.Text, Required: false, Description: "enterprise - the app's service account, in the enterprise (the default); user - a managed user of it.") { Default = "enterprise" },
            new ConnectorConfigField("subject_id", "Enterprise or user id", ConnectorConfigFieldType.Text, Required: true, Description: "The enterprise's id (Admin Console > Account & Billing), or the user's."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField Flag(string name, string label, string description, bool fallback) => Field(name, label, description, type: ConnectorConfigFieldType.Boolean) with { Default = fallback ? "true" : "false" };

        private static ConnectorConfigField Max() => Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField FileField = Field("file_id", "File", "Its id - 12345.", required: true);

        private static readonly ConnectorConfigField FolderField = Field("folder_id", "Folder", "Its id - 12345; 0 the root.", required: true);

        private static readonly ConnectorConfigField ParentField = Field("parent", "Into folder", "The folder's id - 0 (the default) the root.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("list", "List a folder", "A folder's files, folders and links.") { Parameters = Schema(Field("folder_id", "Folder", "Its id; 0 (the default) the root."), Max()) },
            new ConnectorOperation("search", "Search", "Files or folders by a query - n8n's file and folder Search.")
            {
                Parameters = Schema(
                    Field("query", "Query", "What their names, descriptions or contents hold.", required: true),
                    Field("type", "Kind", "file or folder. Empty: both."),
                    Field("folders", "In folders", "Comma-separated folder ids to search under."),
                    Field("extensions", "Extensions", "Comma-separated, without the dot - pdf, csv."),
                    Field("content_types", "Searched in", "Comma-separated: name, description, file_content, comments, tag."),
                    Flag("trash", "In the trash", "Search what is in the trash instead.", fallback: false),
                    Max()),
            },
            new ConnectorOperation("get", "Get a file", "A file's name, size, dates, path and owner - n8n's file Get.") { Parameters = Schema(FileField) },
            new ConnectorOperation("download", "Download a file", "A file written into the run's pipeline folder - n8n's file Download.")
            {
                Parameters = Schema(FileField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation("upload", "Upload a file", "A file of the run's pipeline folder, or text, into a folder - n8n's file Upload.")
            {
                Parameters = Schema(
                    Field("name", "Name", "Its name in Box - 1042.pdf. Empty: the file's own.", required: false),
                    ParentField,
                    Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
                    Field("text", "Text", "Text to store, as UTF-8 - over several lines in the step's Query box."),
                    Flag("new_version", "A new version when taken", "Store it as the existing file's new version when its name is taken; refuse otherwise, as n8n's node does.", fallback: false)),
            },
            new ConnectorOperation("copy", "Copy a file", "Into a folder, under its name or another - n8n's file Copy.") { Parameters = Schema(FileField, ParentField, Field("name", "New name", "Its name there. Empty: its own.")) },
            new ConnectorOperation("move", "Move or rename a file", "Into another folder, or under another name.") { Parameters = Schema(FileField, Field("parent", "Into folder", "The folder's id. Empty: where it is."), Field("name", "New name", "Its new name. Empty: its own.")) },
            new ConnectorOperation("delete", "Delete a file", "A file, to the trash - n8n's file Delete.") { Parameters = Schema(FileField) },
            new ConnectorOperation("folder_get", "Get a folder", "A folder's name, path and size - n8n's folder Get.") { Parameters = Schema(FolderField) },
            new ConnectorOperation("folder_create", "Create a folder", "A folder in another - n8n's folder Create.")
            {
                Parameters = Schema(Field("name", "Name", "Its name - Kvitton 2026.", required: true), ParentField, Field("access", "Its shared link", "open or collaborators - a shared link made with it. Empty: none.")),
            },
            new ConnectorOperation("folder_update", "Update a folder", "Its name, description, parent and tags - n8n's folder Update.")
            {
                Parameters = Schema(FolderField, Field("name", "Name", "Its new name."), Field("description", "Description", "Its description."), Field("parent", "Into folder", "The folder it moves into."), Field("tags", "Tags", "Comma-separated tags.")),
            },
            new ConnectorOperation("folder_delete", "Delete a folder", "A folder, to the trash - n8n's folder Delete.") { Parameters = Schema(FolderField, Flag("recursive", "With what is in it", "Delete it with everything in it; Box refuses one that is not empty otherwise.", fallback: false)) },
            new ConnectorOperation("share", "Share a file or folder", "A collaboration: a user or a group invited as a role - n8n's file and folder Share.")
            {
                Parameters = Schema(
                    Field("item_type", "Item", "file or folder.", required: true),
                    Field("item_id", "Its id", "The file's or folder's id.", required: true),
                    Field("email", "User's address", "The user's address - a user or a group by id instead."),
                    Field("user_id", "User's id", "A Box user's id."),
                    Field("group_id", "Group's id", "A Box group's id."),
                    Field("role", "Role", "editor (the default), viewer, previewer, uploader, previewer uploader, viewer uploader or co-owner.") with { Default = "editor" },
                    Flag("can_view_path", "Sees the path", "Let them see the folders above it.", fallback: false),
                    Field("expires", "Expires", "When the access ends - 2026-12-31T00:00:00Z."),
                    Flag("notify", "Tell them", "Send them Box's e-mail.", fallback: false)),
            },
            new ConnectorOperation("link", "Make a shared link", "A file's or folder's shared link - open, company or collaborators.")
            {
                Parameters = Schema(
                    Field("item_type", "Item", "file or folder.", required: true),
                    Field("item_id", "Its id", "The file's or folder's id.", required: true),
                    Field("access", "Who opens it", "open (anyone with the link), company or collaborators.", required: true),
                    Flag("can_download", "Downloadable", "Let those who open it download it.", fallback: true),
                    Field("expires", "Expires", "When the link stops working - 2026-12-31T00:00:00Z.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var me = await session.JsonAsync(HttpMethod.Get, "/2.0/users/me?fields=name,login", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Box: the app acts as {Text(me?["name"]) ?? "its account"}{(Text(me?["login"]) is { } login ? " (" + login + ")" : string.Empty)}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "list").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Box has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string route = string.Empty, id = string.Empty;
            string? target = null, text = null;
            var body = new JsonObject();
            var query = new List<string>();
            switch (operation)
            {
                case "list":
                    id = IdOf(Values.Get(p, "folder_id") ?? "0", "folder_id");
                    break;
                case "search":
                    query.Add("query=" + Uri.EscapeDataString(OneLine(Values.Get(p, "query") ?? throw new InvalidOperationException("Give what to search for (query)."), "query")));
                    if (Values.Get(p, "type") is { } kind)
                    {
                        query.Add("type=" + (kind.ToLowerInvariant() is ("file" or "folder") and var known ? known : throw new InvalidOperationException($"'{kind}' is not file or folder (type).")));
                    }

                    if (List(p, "folders") is { Count: > 0 } folders)
                    {
                        query.Add("ancestor_folder_ids=" + string.Join(",", folders.Select(folder => IdOf(folder, "folders"))));
                    }

                    if (List(p, "extensions") is { Count: > 0 } extensions)
                    {
                        query.Add("file_extensions=" + Uri.EscapeDataString(string.Join(",", extensions.Select(extension => extension.TrimStart('.')))));
                    }

                    if (List(p, "content_types") is { Count: > 0 } contentTypes)
                    {
                        var allowed = new[] { "name", "description", "file_content", "comments", "tag" };
                        var unknown = contentTypes.FirstOrDefault(type => !allowed.Contains(type, StringComparer.OrdinalIgnoreCase));
                        query.Add("content_types=" + (unknown is null ? string.Join(",", contentTypes.Select(type => type.ToLowerInvariant())) : throw new InvalidOperationException($"'{unknown}' is not name, description, file_content, comments or tag (content_types).")));
                    }

                    if (Bool(p, "trash", false))
                    {
                        query.Add("trash_content=trashed_only");
                    }

                    break;
                case "get" or "delete":
                    id = IdOf(Values.Get(p, "file_id"), "file_id");
                    break;
                case "download":
                    id = IdOf(Values.Get(p, "file_id"), "file_id");
                    target = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    break;
                case "upload":
                    {
                        target = Values.Get(p, "file");
                        text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw // the content as given, not trimmed
                            : p.TryGetValue("query", out var box) && !string.IsNullOrEmpty(box) ? box : null;
                        if ((target is null) == (text is null))
                        {
                            throw new InvalidOperationException(target is null ? "Give the file in the site's pipeline folder to store, or text." : "Give the file or text, not both.");
                        }

                        var name = Values.Get(p, "name") ?? (target is null ? throw new InvalidOperationException("Give its name - text has none of its own (name).") : Path.GetFileName(target.Replace('\\', '/')));
                        body["name"] = NameOf(name, "name");
                        body["parent"] = new JsonObject { ["id"] = IdOf(Values.Get(p, "parent") ?? "0", "parent") };
                        break;
                    }

                case "copy":
                    id = IdOf(Values.Get(p, "file_id"), "file_id");
                    body["parent"] = new JsonObject { ["id"] = IdOf(Values.Get(p, "parent") ?? "0", "parent") };
                    if (Values.Get(p, "name") is { } copyName)
                    {
                        body["name"] = NameOf(copyName, "name");
                    }

                    break;
                case "move":
                    id = IdOf(Values.Get(p, "file_id"), "file_id");
                    if (Values.Get(p, "parent") is { } into)
                    {
                        body["parent"] = new JsonObject { ["id"] = IdOf(into, "parent") };
                    }

                    if (Values.Get(p, "name") is { } newName)
                    {
                        body["name"] = NameOf(newName, "name");
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give where it moves (parent) or its new name (name).");
                    }

                    break;
                case "folder_get":
                    id = IdOf(Values.Get(p, "folder_id"), "folder_id");
                    break;
                case "folder_delete":
                    id = IdOf(Values.Get(p, "folder_id"), "folder_id");
                    if (Bool(p, "recursive", false))
                    {
                        query.Add("recursive=true");
                    }

                    break;
                case "folder_create":
                    body["name"] = NameOf(Values.Get(p, "name") ?? throw new InvalidOperationException("Give the folder's name (name)."), "name");
                    body["parent"] = new JsonObject { ["id"] = IdOf(Values.Get(p, "parent") ?? "0", "parent") };
                    if (Values.Get(p, "access") is { } folderAccess)
                    {
                        body["shared_link_access"] = folderAccess.ToLowerInvariant() is ("open" or "collaborators") and var level ? level : throw new InvalidOperationException($"'{folderAccess}' is not open or collaborators (access).");
                    }

                    break;
                case "folder_update":
                    id = IdOf(Values.Get(p, "folder_id"), "folder_id");
                    if (Values.Get(p, "name") is { } rename)
                    {
                        body["name"] = NameOf(rename, "name");
                    }

                    if (Values.Get(p, "description") is { } description)
                    {
                        body["description"] = description;
                    }

                    if (Values.Get(p, "parent") is { } parent)
                    {
                        body["parent"] = new JsonObject { ["id"] = IdOf(parent, "parent") };
                    }

                    if (List(p, "tags") is { Count: > 0 } tags)
                    {
                        body["tags"] = new JsonArray(tags.Select(tag => (JsonNode)OneLine(tag, "tags")).ToArray());
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what changes - name, description, parent or tags.");
                    }

                    break;
                case "share":
                    {
                        var item = ItemOf(Values.Get(p, "item_type"), Values.Get(p, "item_id"));
                        var whom = new[] { ("email", Values.Get(p, "email")), ("user_id", Values.Get(p, "user_id")), ("group_id", Values.Get(p, "group_id")) }.Where(pair => pair.Item2 is not null).ToList();
                        if (whom.Count != 1)
                        {
                            throw new InvalidOperationException("Give whom it is shared with - one of a user's address (email), a user's id (user_id) or a group's id (group_id).");
                        }

                        var (key, value) = whom[0];
                        body["item"] = item;
                        body["accessible_by"] = key switch
                        {
                            "email" => new JsonObject { ["type"] = "user", ["login"] = value!.Contains('@') && !value.Any(char.IsWhiteSpace) ? value : throw new InvalidOperationException($"'{value}' is not an address (email).") },
                            "user_id" => new JsonObject { ["type"] = "user", ["id"] = IdOf(value, "user_id") },
                            _ => new JsonObject { ["type"] = "group", ["id"] = IdOf(value, "group_id") },
                        };
                        body["role"] = RoleOf(Values.Get(p, "role") ?? "editor");
                        if (Bool(p, "can_view_path", false))
                        {
                            body["can_view_path"] = true;
                        }

                        if (Values.Get(p, "expires") is { } expires)
                        {
                            body["expires_at"] = WhenOf(expires, "expires");
                        }

                        if (Bool(p, "notify", false))
                        {
                            query.Add("notify=true");
                        }

                        break;
                    }

                case "link":
                    {
                        var item = ItemOf(Values.Get(p, "item_type"), Values.Get(p, "item_id"));
                        route = $"/2.0/{item["type"]}s/{item["id"]}?fields=shared_link";
                        var access = (Values.Get(p, "access") ?? throw new InvalidOperationException("Give who opens it - open, company or collaborators (access).")).ToLowerInvariant();
                        var link = new JsonObject
                        {
                            ["access"] = access is "open" or "company" or "collaborators" ? access : throw new InvalidOperationException($"'{access}' is not open, company or collaborators (access)."),
                            ["permissions"] = new JsonObject { ["can_download"] = Bool(p, "can_download", true) },
                        };
                        if (Values.Get(p, "expires") is { } expires)
                        {
                            link["unshared_at"] = WhenOf(expires, "expires");
                        }

                        body["shared_link"] = link;
                        break;
                    }
            }

            if ((operation == "download" || (operation == "upload" && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            var suffix = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);
            switch (operation)
            {
                case "list":
                    await foreach (var item in session.MarkerPagesAsync($"/2.0/folders/{id}/items", max, cancellationToken))
                    {
                        yield return Fields(item);
                    }

                    break;

                case "search":
                    await foreach (var item in session.OffsetPagesAsync("/2.0/search" + suffix, max, cancellationToken))
                    {
                        yield return Fields(item);
                    }

                    break;

                case "get":
                    yield return Fields(await session.JsonAsync(HttpMethod.Get, $"/2.0/files/{id}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "download":
                    yield return await session.DownloadAsync(id, context.Files!, target!, cancellationToken);
                    break;

                case "upload":
                    yield return Fields(await session.UploadAsync(body, text, context.Files, target, Bool(p, "new_version", false), cancellationToken));
                    break;

                case "copy":
                    yield return Fields(await session.JsonAsync(HttpMethod.Post, $"/2.0/files/{id}/copy", body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "move":
                    yield return Fields(await session.JsonAsync(HttpMethod.Put, $"/2.0/files/{id}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "delete":
                    await session.JsonAsync(HttpMethod.Delete, $"/2.0/files/{id}", null, cancellationToken);
                    yield return Record(("id", id), ("type", "file"), ("deleted", true));
                    break;

                case "folder_get":
                    yield return Fields(await session.JsonAsync(HttpMethod.Get, $"/2.0/folders/{id}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "folder_create":
                    {
                        var access = body["shared_link_access"]?.GetValue<string>();
                        body.Remove("shared_link_access");
                        var made = await session.JsonAsync(HttpMethod.Post, "/2.0/folders", body, cancellationToken) as JsonObject ?? new JsonObject();
                        if (access is not null && Text(made["id"]) is { } madeId)
                        {
                            made = await session.JsonAsync(HttpMethod.Put, $"/2.0/folders/{madeId}?fields=id,type,name,shared_link", new JsonObject { ["shared_link"] = new JsonObject { ["access"] = access } }, cancellationToken) as JsonObject ?? made;
                        }

                        yield return Fields(made);
                        break;
                    }

                case "folder_update":
                    yield return Fields(await session.JsonAsync(HttpMethod.Put, $"/2.0/folders/{id}", body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "folder_delete":
                    await session.JsonAsync(HttpMethod.Delete, $"/2.0/folders/{id}" + suffix, null, cancellationToken);
                    yield return Record(("id", id), ("type", "folder"), ("deleted", true));
                    break;

                case "share":
                    yield return Fields(await session.JsonAsync(HttpMethod.Post, "/2.0/collaborations" + suffix, body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case "link":
                    {
                        var shared = await session.JsonAsync(HttpMethod.Put, route, body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Fields(shared["shared_link"] as JsonObject is { } got ? (JsonObject)got.DeepClone() : new JsonObject(), ("id", Text(shared["id"])), ("type", Text(shared["type"])));
                        break;
                    }
            }
        }

        /// <summary>A Box id: its digits (0 the root folder).</summary>
        public static string IdOf(string? given, string what)
        {
            var value = (given ?? throw new InvalidOperationException($"Give its id - 12345 ({what}).")).Trim();
            return Id.IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not a Box id - its digits ({what}).");
        }

        private static string NameOf(string value, string what) =>
            value.Length is > 0 and <= 255 && !value.Any(c => char.IsControl(c) || c is '/' or '\\') && value.Trim() is not ("." or "..") && value == value.Trim()
                ? value
                : throw new InvalidOperationException($"'{value}' is not a name Box takes - 1 to 255 characters, no slash, not . or .. ({what}).");

        private static JsonObject ItemOf(string? type, string? id)
        {
            var kind = (type ?? throw new InvalidOperationException("Give whether it is a file or a folder (item_type).")).ToLowerInvariant();
            return kind is "file" or "folder"
                ? new JsonObject { ["type"] = kind, ["id"] = IdOf(id, "item_id") }
                : throw new InvalidOperationException($"'{type}' is not file or folder (item_type).");
        }

        private static string RoleOf(string given)
        {
            foreach (var (n8n, box) in Roles)
            {
                if (string.Equals(given, n8n, StringComparison.OrdinalIgnoreCase) || string.Equals(given, box, StringComparison.OrdinalIgnoreCase))
                {
                    return box;
                }
            }

            throw new InvalidOperationException($"'{given}' is not editor, viewer, previewer, uploader, previewer uploader, viewer uploader or co-owner (role).");
        }

        private static string WhenOf(string value, string what) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{value}' is not a time - 2026-12-31T00:00:00Z ({what}).");

        private static List<string> List(IReadOnlyDictionary<string, string> p, string name) =>
            (Values.Get(p, name) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private static bool Bool(IReadOnlyDictionary<string, string> p, string name, bool fallback) => Values.Get(p, name) switch
        {
            null => fallback,
            var value when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            var value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            var value => throw new InvalidOperationException($"'{value}' is not true or false ({name})."),
        };

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        // An item as Box names its fields; a nested value (a path, an owner, a shared link) a JSON object.
        private static ConnectorRecord Fields(JsonObject item, params (string Name, object? Value)[] added)
        {
            var fields = item.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue value ? JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(value)) : pair.Value?.ToJsonString(Wire), StringComparer.Ordinal);
            foreach (var (name, value) in added.Where(field => field.Value is not null))
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        /// <summary>One signed-in Box app: its API address, its upload address and its token.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;
            private readonly string upload;

            private Session(HttpClient client, string token, string api, string upload)
            {
                this.client = client;
                this.token = token;
                this.api = api;
                this.upload = upload;
            }

            public static async Task<Session> OpenAsync(BoxConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Box's API - its address alone, {DefaultEndpoint}.");
                }

                var subjectType = Values.GetOrDefault(settings, "subject_type", "enterprise").ToLowerInvariant();
                if (subjectType is not ("enterprise" or "user"))
                {
                    throw new InvalidOperationException($"'{Values.Get(settings, "subject_type")}' is not enterprise or user (subject_type).");
                }

                var subject = IdOf(Values.Get(settings, "subject_id") ?? throw new InvalidOperationException("Give the enterprise's id, or the user's, the app acts as (subject_id)."), "subject_id");

                // Box's own API takes uploads at its upload host, under /api; a stand-in serves both, the same way.
                var content = (string.Equals(api, DefaultEndpoint, StringComparison.OrdinalIgnoreCase) ? UploadEndpoint : api) + "/api";
                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(Values.Get(credentials, "token-url") ?? DefaultTokenUrl, Values.Get(credentials, "client-id") ?? string.Empty, Values.Get(credentials, "client-secret") ?? string.Empty, string.Empty)
                {
                    Timeout = timeout,
                    Extra = new KeyValuePair<string, string>[] { new("box_subject_type", subjectType), new("box_subject_id", subject) },
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, api, content);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A JSON call: its answer, or Box's refusal with its status, code and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = Request(method, api + route);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                return await ReadAsync(response, cancellationToken);
            }

            /// <summary>A folder's items page by page through Box's marker, up to <paramref name="max"/>.</summary>
            public async IAsyncEnumerable<JsonObject> MarkerPagesAsync(string route, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                string? marker = null;
                while (read < max)
                {
                    var page = await JsonAsync(HttpMethod.Get, $"{route}?usemarker=true&limit={Math.Min(max - read, PageSize).ToString(CultureInfo.InvariantCulture)}{(marker is null ? string.Empty : "&marker=" + Uri.EscapeDataString(marker))}", null, cancellationToken) as JsonObject ?? new JsonObject();
                    foreach (var entry in (page["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone()))
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        read++;
                        yield return entry;
                    }

                    marker = Text(page["next_marker"]) is { Length: > 0 } next ? next : null;
                    if (marker is null)
                    {
                        yield break;
                    }
                }
            }

            /// <summary>A search page by page through Box's offset (its total said), up to <paramref name="max"/>.</summary>
            public async IAsyncEnumerable<JsonObject> OffsetPagesAsync(string route, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                var joiner = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                while (read < max)
                {
                    var limit = Math.Min(max - read, 200);
                    var page = await JsonAsync(HttpMethod.Get, $"{route}{joiner}limit={limit.ToString(CultureInfo.InvariantCulture)}&offset={read.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken) as JsonObject ?? new JsonObject();
                    var entries = (page["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone()).ToList();
                    foreach (var entry in entries)
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        read++;
                        yield return entry;
                    }

                    var total = page["total_count"] is JsonValue counted && counted.TryGetValue<long>(out var all) ? all : 0;
                    if (entries.Count < limit || read >= total)
                    {
                        yield break;
                    }
                }
            }

            /// <summary>A file's bytes: Box answers with its download host's address, followed through the same egress - the token not sent there - and written into the run's folder.</summary>
            public async Task<ConnectorRecord> DownloadAsync(string id, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                var item = await JsonAsync(HttpMethod.Get, $"/2.0/files/{id}?fields=name,size", null, cancellationToken) as JsonObject ?? new JsonObject();
                var name = Text(item["name"]) ?? id;
                if (item["size"] is JsonValue sized && sized.TryGetValue<long>(out var size) && size > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{name}' is {size} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(HttpMethod.Get, $"{api}/2.0/files/{id}/content"), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                try
                {
                    if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther && response.Headers.Location is { } location)
                    {
                        // Box's download host: a signed address of its own - the egress decides whether it may be reached; no token travels there.
                        var next = location.IsAbsoluteUri ? location : new Uri(new Uri(api), location);
                        if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
                        {
                            throw new InvalidOperationException($"Box sent the download to '{next.Scheme}:' - not an http(s) address.");
                        }

                        response.Dispose();
                        response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, next), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw Failure(response, await response.Content.ReadAsStringAsync(cancellationToken));
                    }

                    if (response.Content.Headers.ContentLength is { } known && known > files.MaxBytes)
                    {
                        throw new InvalidOperationException($"'{name}' is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                    }

                    var written = file.EndsWith('/') ? file + Safe(name) : file;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var length = await files.WriteAsync(written, stream, cancellationToken);
                    return Record(("id", id), ("name", name), ("file", written), ("size", length));
                }
                finally
                {
                    response.Dispose();
                }
            }

            /// <summary>An upload to the upload host: its attributes, then its bytes; a taken name refused, or - when asked - the file's new version.</summary>
            public async Task<JsonObject> UploadAsync(JsonObject attributes, string? text, IConnectorFiles? files, string? file, bool newVersion, CancellationToken cancellationToken)
            {
                ConnectorFileContent? opened = file is null ? null : await files!.OpenReadAsync(file, cancellationToken);
                try
                {
                    var bytes = text is null ? null : Encoding.UTF8.GetBytes(text);
                    HttpContent Bytes()
                    {
                        if (bytes is not null)
                        {
                            return new ByteArrayContent(bytes);
                        }

                        opened!.Content.Position = 0;
                        return new StreamContent(new LentStream(opened.Content)) { Headers = { ContentLength = opened.Length } };
                    }

                    async Task<HttpResponseMessage> Send(string route, JsonObject sent)
                    {
                        var head = sent.ToJsonString(Wire);
                        return await RetryAfterPolicy.Default.SendAsync(client, () =>
                        {
                            var form = new MultipartFormDataContent
                            {
                                { new StringContent(head, Encoding.UTF8), "attributes" },
                            };
                            var part = Bytes();
                            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                            form.Add(part, "file", (string)sent["name"]! ?? "file");
                            var request = Request(HttpMethod.Post, upload + route);
                            request.Content = form;
                            return request;
                        }, cancellationToken);
                    }

                    using var first = await Send("/2.0/files/content", attributes);
                    var payload = await first.Content.ReadAsStringAsync(cancellationToken);
                    if (first.StatusCode == HttpStatusCode.Conflict && newVersion
                        && (JsonNode.Parse(payload) as JsonObject)?["context_info"]?["conflicts"]?["id"] is JsonValue conflict && conflict.TryGetValue<string>(out var existing))
                    {
                        using var version = await Send($"/2.0/files/{IdOf(existing, "the conflicting file")}/content", new JsonObject { ["name"] = attributes["name"]!.DeepClone() });
                        return Entry(await ReadAsync(version, cancellationToken));
                    }

                    if (!first.IsSuccessStatusCode)
                    {
                        throw Failure(first, payload);
                    }

                    return Entry(JsonNode.Parse(payload));
                }
                finally
                {
                    if (opened is not null)
                    {
                        await opened.DisposeAsync();
                    }
                }
            }

            // Box answers an upload as a list of one: that one.
            private static JsonObject Entry(JsonNode? answer) => ((answer as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } entry ? (JsonObject)entry.DeepClone() : answer as JsonObject ?? new JsonObject();

            private HttpRequestMessage Request(HttpMethod method, string url)
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }

            private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, payload);
                }

                try
                {
                    return string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException($"Box answered {(int)response.StatusCode} with something that is not JSON - check the connection's address.");
                }
            }

            // Box's refusal: its status, its code and its message.
            private static InvalidOperationException Failure(HttpResponseMessage response, string payload)
            {
                string? code = null, message = null;
                try
                {
                    var error = JsonNode.Parse(payload) as JsonObject;
                    code = Text(error?["code"]);
                    message = Text(error?["message"]);
                }
                catch (JsonException)
                {
                    message = payload.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line.Length > 300 ? line[..300] + "..." : line : null;
                }

                var said = string.Join(": ", new[] { code, message }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return new InvalidOperationException($"Box answered {(int)response.StatusCode}{(said.Length == 0 ? "." : " " + said)}");
            }

            private static string Safe(string name)
            {
                var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
                var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
                return safe.Length == 0 ? "file" : safe;
            }
        }
    }
}
