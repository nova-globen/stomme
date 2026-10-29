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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Nextcloud
{
    /// <summary>
    /// Nextcloud (n8n's Nextcloud node: files, folders, shares and users) over a Nextcloud server's own interfaces - its
    /// files through WebDAV (<c>/remote.php/dav/files/{user}</c>), its shares and users through OCS
    /// (<c>/ocs/v2.php/...</c>, JSON) - signed in by a user's name and an app password (Basic, as n8n's access-token credential
    /// does). The address is the server's own, <c>https://cloud.example.com</c> or a folder it is installed under; n8n's WebDAV
    /// address (<c>.../remote.php/webdav</c>) is read as the server's. A download is written into the pipeline run's folder and
    /// an upload read from it (<see cref="IConnectorFiles"/>). A test names whose account signs in, and whether it administers.
    /// </summary>
    /// <remarks>
    /// <para><c>list</c> (a folder's files and folders, each as n8n's folder List names them - <c>path</c>, <c>type</c>,
    /// <c>lastModified</c>, <c>contentLength</c>, <c>contentType</c>, <c>eTag</c> - with its <c>name</c>, <c>fileId</c> and
    /// <c>size</c>), <c>get</c>, <c>download</c>, <c>upload</c> (a file of the run's folder or text, replacing what is there
    /// as n8n's node does, or refused when it exists), <c>create_folder</c>, <c>copy</c>, <c>move</c>, <c>delete</c>,
    /// <c>share</c> (with a user, a group, an address or a team, as a public link or an internal one), and the users an
    /// administrator manages - <c>users</c>, <c>user_get</c>, <c>user_create</c> (by an address Nextcloud sends a link to set a
    /// password to), <c>user_update</c> (its fields, and enabled or not) and <c>user_delete</c>. <b>A password is never a step's
    /// parameter</b>: a new user sets their own; a public link's password is set in Nextcloud.</para>
    /// <para>A file or a folder is its path from the user's root (<c>Kvitton/2026/1042.pdf</c>, a leading or trailing slash
    /// ignored), each segment escaped on the wire; every value held to its shape before anything is sent. Nextcloud's refusals
    /// said with their status and Nextcloud's own message (WebDAV's <c>s:message</c>, OCS's <c>meta.message</c>).</para>
    /// </remarks>
    public sealed class NextcloudConnector : IConnector, IConnectorExtractor
    {
        public const string ListOperation = "list";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string CreateFolderOperation = "create_folder";

        public const string CopyOperation = "copy";

        public const string MoveOperation = "move";

        public const string DeleteOperation = "delete";

        public const string ShareOperation = "share";

        public const string UsersOperation = "users";

        public const string UserGetOperation = "user_get";

        public const string UserCreateOperation = "user_create";

        public const string UserUpdateOperation = "user_update";

        public const string UserDeleteOperation = "user_delete";

        public const int DefaultMaxUsers = 50;

        /// <summary>The most users asked for in one call; more are read page by page.</summary>
        public const int UsersPage = 500;

        private static readonly string[] Known =
        {
            ListOperation, GetOperation, DownloadOperation, UploadOperation, CreateFolderOperation, CopyOperation, MoveOperation, DeleteOperation,
            ShareOperation, UsersOperation, UserGetOperation, UserCreateOperation, UserUpdateOperation, UserDeleteOperation,
        };

        // A share's kind as Nextcloud numbers it - n8n's numbers read as well.
        private static readonly (string Name, int Type)[] ShareTypes = { ("user", 0), ("group", 1), ("link", 3), ("email", 4), ("team", 7), ("internal", 200) };

        // A share's permissions as Nextcloud adds them up.
        private static readonly (string Name, int Bits)[] Permissions = { ("read", 1), ("update", 2), ("create", 4), ("delete", 8), ("share", 16), ("all", 31) };

        // A user's fields a step may change, as a step names them and as Nextcloud's provisioning API does - never the password.
        private static readonly (string Name, string Key)[] UserFields =
        {
            ("display_name", "displayname"), ("email", "email"), ("phone", "phone"), ("address", "address"), ("website", "website"), ("twitter", "twitter"), ("quota", "quota"),
        };

        private static readonly Regex UserId = new(@"^[A-Za-z0-9 _.@\-']{1,64}$", RegexOptions.CultureInvariant);

        private static readonly XNamespace Dav = "DAV:";

        private static readonly XNamespace Oc = "http://owncloud.org/ns";

        private static readonly XNamespace Sabre = "http://sabredav.org/ns";

        private const string PropfindBody = "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\" xmlns:oc=\"http://owncloud.org/ns\"><d:prop><d:getlastmodified/><d:getcontentlength/><d:getcontenttype/><d:resourcetype/><d:getetag/><oc:fileid/><oc:size/></d:prop></d:propfind>";

        public string ConnectorType => "nextcloud";

        public string DisplayName => "Nextcloud";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://cloud.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://nextcloud.com");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.nextcloud.com/server/latest/developer_manual/client_apis/");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "User (its login name)",
            ["password"] = "App password (Personal settings > Security > Devices & sessions)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField Flag(string name, string label, string description, bool fallback) => Field(name, label, description, type: ConnectorConfigFieldType.Boolean) with { Default = fallback ? "true" : "false" };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField PathField = Field("path", "Path", "Its path from the user's root - Kvitton/2026/1042.pdf.", required: true);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's id - their login name, ada.", required: true);

        private static readonly ConnectorConfigField OverwriteField = Flag("overwrite", "Replace what is there", "Replace a file or folder already at the path, as n8n's node does; refuse otherwise.", fallback: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ListOperation, "List a folder", "A folder's files and folders - n8n's folder List.") { Parameters = Schema(Field("path", "Folder", "Its path - Kvitton. Empty: the user's root.")) },
            new ConnectorOperation(GetOperation, "Get a file or a folder", "Its type, size, dates, tag and id.") { Parameters = Schema(PathField) },
            new ConnectorOperation(DownloadOperation, "Download a file", "A file written into the run's pipeline folder - n8n's file Download.")
            {
                Parameters = Schema(PathField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation(UploadOperation, "Upload a file", "A file of the run's pipeline folder, or text, stored at a path - n8n's file Upload.")
            {
                Parameters = Schema(
                    Field("path", "Path", "Where it is stored - Kvitton/1042.pdf; ending with / the file's own name in that folder.", required: true),
                    Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
                    Field("text", "Text", "Text to store, as UTF-8, where no file is given - over several lines in the step's Query box."),
                    OverwriteField),
            },
            new ConnectorOperation(CreateFolderOperation, "Create a folder", "A folder in one that exists - n8n's folder Create.") { Parameters = Schema(Field("path", "Folder", "Its path - Kvitton/2026.", required: true)) },
            new ConnectorOperation(CopyOperation, "Copy a file or a folder", "To another path - n8n's file and folder Copy.")
            {
                Parameters = Schema(Field("from", "From", "Its path.", required: true), Field("to", "To", "Its new path - Arkiv/1042.pdf.", required: true), OverwriteField),
            },
            new ConnectorOperation(MoveOperation, "Move a file or a folder", "To another path, or another name - n8n's file and folder Move.")
            {
                Parameters = Schema(Field("from", "From", "Its path.", required: true), Field("to", "To", "Its new path - Arkiv/1042.pdf.", required: true), OverwriteField),
            },
            new ConnectorOperation(DeleteOperation, "Delete a file or a folder", "A file, or a folder and everything in it, to the trash bin - n8n's file and folder Delete.") { Parameters = Schema(PathField) },
            new ConnectorOperation(ShareOperation, "Share a file or a folder", "With a user, a group, an address or a team, or as a link - n8n's file and folder Share.")
            {
                Parameters = Schema(
                    PathField,
                    Field("type", "Shared as", "user, group, email, team, link (a public link) or internal (a link for those who already have it).", required: true),
                    Field("with", "With", "The user's or group's id, the address, or the team's id - not for a link."),
                    Field("permissions", "Permissions", "Comma-separated: read, update, create, delete and share - or all, or Nextcloud's number. Empty: Nextcloud's default."),
                    Field("expire", "Expires", "The day it ends - 2026-12-31."),
                    Field("note", "Note", "A note to whom it is shared with."),
                    Field("label", "Label", "A link's label.")),
            },
            new ConnectorOperation(UsersOperation, "Get users", "The server's users, by a search - n8n's user Get Many. An administrator's.")
            {
                Parameters = Schema(
                    Field("search", "Search", "What their ids, names or addresses contain."),
                    Flag("details", "Every field", "Each user's fields - name, address, groups, quota - rather than the id alone.", fallback: false),
                    Field("offset", "Skip", "How many to skip first.", type: ConnectorConfigFieldType.Integer),
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMaxUsers.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(UserGetOperation, "Get a user", "A user's fields - n8n's user Get.") { Parameters = Schema(UserField) },
            new ConnectorOperation(UserCreateOperation, "Create a user", "A user, sent a link to set their password to - n8n's user Create. An administrator's.")
            {
                Parameters = Schema(
                    UserField,
                    Field("email", "Address", "Where Nextcloud sends the link to set a password.", required: true),
                    Field("display_name", "Display name", "Their name as shown."),
                    Field("groups", "Groups", "Comma-separated: the groups they join.")),
            },
            new ConnectorOperation(UserUpdateOperation, "Update a user", "A user's fields, and enabled or not - n8n's user Update. An administrator's.")
            {
                Parameters = Schema(
                    UserField,
                    Field("display_name", "Display name", "Their name as shown."),
                    Field("email", "Address", "Their address."),
                    Field("phone", "Phone", "Their phone number."),
                    Field("address", "Postal address", "Where they are."),
                    Field("website", "Website", "Their website."),
                    Field("twitter", "Twitter", "Their handle."),
                    Field("quota", "Quota", "Their space - 5 GB, or none."),
                    Field("enabled", "Enabled", "true to let them sign in, false to stop them.")),
            },
            new ConnectorOperation(UserDeleteOperation, "Delete a user", "A user and their files - n8n's user Delete. An administrator's.") { Parameters = Schema(UserField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var admin = session.Account["groups"] is JsonArray groups && groups.Any(group => Text(group) == "admin");
                return ConnectionTestResult.Ok($"Nextcloud: {Text(session.Account["displayname"]) ?? session.UserId}'s account answers{(admin ? " (an administrator)" : string.Empty)}.");
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
                ?? throw new InvalidOperationException($"Nextcloud has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var overwrite = Bool(p, "overwrite", true);
            string path = string.Empty, from = string.Empty, to = string.Empty, user = string.Empty;
            string? target = null, text = null;
            var form = new List<KeyValuePair<string, string>>();
            var share = 0;
            var offset = 0;
            var max = DefaultMaxUsers;
            var changes = new List<(string Name, string Key, string Value)>();
            bool? enabled = null;
            switch (operation)
            {
                case ListOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: true);
                    break;
                case GetOperation or DeleteOperation or CreateFolderOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: false);
                    break;
                case DownloadOperation:
                    path = PathOf(Values.Get(p, "path"), "path", root: false);
                    target = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    break;
                case UploadOperation:
                    {
                        target = Values.Get(p, "file");
                        text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw // the content as given, not trimmed
                            : p.TryGetValue("query", out var query) && !string.IsNullOrEmpty(query) ? query : null;
                        if ((target is null) == (text is null))
                        {
                            throw new InvalidOperationException(target is null ? "Give the file in the site's pipeline folder to store, or text." : "Give the file or text, not both.");
                        }

                        var given = Values.Get(p, "path") ?? throw new InvalidOperationException("Give the path it is stored at - Kvitton/1042.pdf.");
                        if (given.EndsWith('/'))
                        {
                            given += target is null ? throw new InvalidOperationException("Give the path with its name - text has none of its own.") : Path.GetFileName(target.Replace('\\', '/'));
                        }

                        path = PathOf(given, "path", root: false);
                        break;
                    }

                case CopyOperation or MoveOperation:
                    from = PathOf(Values.Get(p, "from"), "from", root: false);
                    to = PathOf(Values.Get(p, "to"), "to", root: false);
                    break;
                case ShareOperation:
                    {
                        path = PathOf(Values.Get(p, "path"), "path", root: false);
                        share = ShareTypeOf(Values.Get(p, "type"));
                        var with = Values.Get(p, "with");
                        if (share is 3 or 200)
                        {
                            if (with is not null)
                            {
                                throw new InvalidOperationException("A link is shared with no one in particular - leave 'with' empty.");
                            }
                        }
                        else
                        {
                            form.Add(new("shareWith", OneLine(with ?? throw new InvalidOperationException("Give whom it is shared with - a user's or group's id, an address or a team's id (with)."), "with")));
                        }

                        if (Values.Get(p, "permissions") is { } permissions)
                        {
                            form.Add(new("permissions", PermissionsOf(permissions).ToString(CultureInfo.InvariantCulture)));
                        }

                        if (Values.Get(p, "expire") is { } expire)
                        {
                            form.Add(new("expireDate", DateTime.TryParseExact(expire, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                : throw new InvalidOperationException($"'{expire}' is not a day - 2026-12-31 (expire).")));
                        }

                        if (Values.Get(p, "note") is { } note)
                        {
                            form.Add(new("note", note));
                        }

                        if (Values.Get(p, "label") is { } label)
                        {
                            form.Add(new("label", OneLine(label, "label")));
                        }

                        if (share == 200 && form.Count > 0)
                        {
                            throw new InvalidOperationException("An internal link is the file's own address - it takes no permissions, expiry, note or label.");
                        }

                        break;
                    }

                case UsersOperation:
                    max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMaxUsers;
                    offset = Values.Get(p, "offset") is { } skip ? int.TryParse(skip, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : throw new InvalidOperationException($"'{skip}' is not a number from 0 (offset).") : 0;
                    if (Values.Get(p, "search") is { } search)
                    {
                        form.Add(new("search", OneLine(search, "search")));
                    }

                    Bool(p, "details", false);
                    break;
                case UserGetOperation or UserDeleteOperation:
                    user = UserOf(Values.Get(p, "user"));
                    break;
                case UserCreateOperation:
                    {
                        user = UserOf(Values.Get(p, "user"));
                        form.Add(new("userid", user));
                        var email = Values.Get(p, "email") ?? throw new InvalidOperationException("Give the user's address - Nextcloud sends the link to set a password there (email).");
                        form.Add(new("email", EmailOf(email, "email")));
                        if (Values.Get(p, "display_name") is { } name)
                        {
                            form.Add(new("displayName", OneLine(name, "display_name")));
                        }

                        foreach (var group in (Values.Get(p, "groups") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            form.Add(new("groups[]", OneLine(group, "groups")));
                        }

                        break;
                    }

                case UserUpdateOperation:
                    user = UserOf(Values.Get(p, "user"));
                    foreach (var (name, key) in UserFields)
                    {
                        if (Values.Get(p, name) is { } value)
                        {
                            changes.Add((name, key, key == "email" ? EmailOf(value, name) : OneLine(value, name)));
                        }
                    }

                    if (Values.Get(p, "enabled") is not null)
                    {
                        enabled = Bool(p, "enabled", true);
                    }

                    if (p.Keys.Any(key => string.Equals(key.Trim(), "password", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("A password is never a step's parameter - the user sets their own in Nextcloud.");
                    }

                    if (changes.Count == 0 && enabled is null)
                    {
                        throw new InvalidOperationException($"Give what changes - {string.Join(", ", UserFields.Select(field => field.Name))} or enabled.");
                    }

                    break;
            }

            if ((operation == DownloadOperation || (operation == UploadOperation && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case ListOperation:
                    {
                        var entries = await session.PropfindAsync(path, folder: true, depth: 1, cancellationToken);
                        var self = entries.FindIndex(entry => entry.Relative.TrimEnd('/') == path);
                        var own = self < 0 ? 0 : self;
                        if (entries.Count > 0 && entries[own].Fields["type"] as string != "folder")
                        {
                            throw new InvalidOperationException($"'{path}' is a file, not a folder - get it, or list the folder it is in.");
                        }

                        foreach (var (_, fields) in entries.Where((_, index) => index != own))
                        {
                            yield return new ConnectorRecord(fields);
                        }

                        break;
                    }

                case GetOperation:
                    {
                        var entries = await session.PropfindAsync(path, folder: false, depth: 0, cancellationToken);
                        yield return new ConnectorRecord(entries.FirstOrDefault().Fields ?? throw new InvalidOperationException($"Nextcloud said nothing of '{path}'."));
                        break;
                    }

                case DownloadOperation:
                    yield return await session.DownloadAsync(path, context.Files!, target!, cancellationToken);
                    break;

                case UploadOperation:
                    yield return target is null
                        ? await session.UploadAsync(path, Encoding.UTF8.GetBytes(text!), null, null, overwrite, cancellationToken)
                        : await session.UploadAsync(path, null, context.Files, target, overwrite, cancellationToken);
                    break;

                case CreateFolderOperation:
                    {
                        using var made = await session.DavAsync(new HttpMethod("MKCOL"), path, null, null, cancellationToken);
                        yield return Record(("path", path + "/"), ("name", NameOf(path)), ("type", "folder"), ("fileId", FileId(made)));
                        break;
                    }

                case CopyOperation or MoveOperation:
                    {
                        var headers = new[] { ("Destination", session.DavUrl(to)), ("Overwrite", overwrite ? "T" : "F") };
                        using var done = await session.DavAsync(new HttpMethod(operation == CopyOperation ? "COPY" : "MOVE"), from, null, headers, cancellationToken);
                        yield return Record(("from", from), ("to", to), ("path", to), ("name", NameOf(to)), ("replaced", done.StatusCode == HttpStatusCode.NoContent), ("fileId", FileId(done)));
                        break;
                    }

                case DeleteOperation:
                    {
                        using var deleted = await session.DavAsync(HttpMethod.Delete, path, null, null, cancellationToken);
                        yield return Record(("path", path), ("name", NameOf(path)), ("deleted", true));
                        break;
                    }

                case ShareOperation:
                    if (share == 200)
                    {
                        // An internal link is the file's own address, by its id: for those who can already reach it.
                        var found = (await session.PropfindAsync(path, folder: false, depth: 0, cancellationToken)).FirstOrDefault().Fields;
                        var id = found is not null && found.TryGetValue("fileId", out var fileId) && fileId is not null ? fileId : throw new InvalidOperationException($"Nextcloud named no id for '{path}'.");
                        yield return Record(("path", path), ("type", "internal"), ("fileId", id), ("link", $"{session.Base}/f/{id}"));
                    }
                    else
                    {
                        form.Insert(0, new("path", "/" + path));
                        form.Insert(1, new("shareType", share.ToString(CultureInfo.InvariantCulture)));
                        var made = await session.OcsAsync(HttpMethod.Post, "apps/files_sharing/api/v1/shares", form, cancellationToken);
                        yield return Fields(made as JsonObject ?? new JsonObject());
                    }

                    break;

                case UsersOperation:
                    {
                        var details = Bool(p, "details", false);
                        var read = 0;
                        while (read < max)
                        {
                            var page = Math.Min(max - read, UsersPage);
                            var query = new List<KeyValuePair<string, string>>(form) { new("limit", page.ToString(CultureInfo.InvariantCulture)), new("offset", (offset + read).ToString(CultureInfo.InvariantCulture)) };
                            var data = await session.OcsAsync(HttpMethod.Get, details ? "cloud/users/details" : "cloud/users", query, cancellationToken);
                            var users = data?["users"];
                            var got = 0;
                            if (users is JsonArray ids)
                            {
                                foreach (var id in ids.TakeWhile(_ => read + got < max))
                                {
                                    got++;
                                    yield return Record(("id", Text(id)));
                                }
                            }
                            else if (users is JsonObject keyed)
                            {
                                foreach (var (id, fields) in keyed.TakeWhile(_ => read + got < max))
                                {
                                    got++;
                                    var record = Fields(fields as JsonObject ?? new JsonObject());
                                    yield return record.Fields.ContainsKey("id") ? record : new ConnectorRecord(new Dictionary<string, object?>(record.Fields) { ["id"] = id });
                                }
                            }

                            read += got;
                            if (got < page)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case UserGetOperation:
                    yield return Fields(await session.OcsAsync(HttpMethod.Get, "cloud/users/" + Uri.EscapeDataString(user), null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case UserCreateOperation:
                    {
                        var made = await session.OcsAsync(HttpMethod.Post, "cloud/users", form, cancellationToken);
                        yield return Record(("id", Text(made?["id"]) ?? user), ("email", form.Single(pair => pair.Key == "email").Value), ("created", true));
                        break;
                    }

                case UserUpdateOperation:
                    {
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = user };
                        foreach (var (name, key, value) in changes)
                        {
                            await session.OcsAsync(HttpMethod.Put, "cloud/users/" + Uri.EscapeDataString(user), new KeyValuePair<string, string>[] { new("key", key), new("value", value) }, cancellationToken);
                            fields[name] = value;
                        }

                        if (enabled is { } on)
                        {
                            await session.OcsAsync(HttpMethod.Put, $"cloud/users/{Uri.EscapeDataString(user)}/{(on ? "enable" : "disable")}", null, cancellationToken);
                            fields["enabled"] = on;
                        }

                        yield return new ConnectorRecord(fields);
                        break;
                    }

                case UserDeleteOperation:
                    await session.OcsAsync(HttpMethod.Delete, "cloud/users/" + Uri.EscapeDataString(user), null, cancellationToken);
                    yield return Record(("id", user), ("deleted", true));
                    break;
            }
        }

        /// <summary>
        /// A path from the user's root as a step gives it: <c>""</c> for the root (where <paramref name="root"/> allows it), or
        /// its segments joined by <c>/</c> - a leading or trailing slash dropped, no empty, <c>.</c> or <c>..</c> segment and no
        /// control character.
        /// </summary>
        public static string PathOf(string? given, string what, bool root)
        {
            var value = (given ?? string.Empty).Trim().Trim('/');
            if (value.Length == 0)
            {
                return root ? string.Empty : throw new InvalidOperationException($"Give a path - Kvitton/1042.pdf ({what}).");
            }

            var segments = value.Split('/');
            return segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl))
                ? throw new InvalidOperationException($"'{given}' is not a path - Kvitton/2026/1042.pdf ({what}).")
                : string.Join("/", segments);
        }

        /// <summary>The server's own address from what a connection gives: n8n's WebDAV address (<c>.../remote.php/webdav</c>) read as the server's; no user, query or fragment.</summary>
        public static string BaseOf(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("Give the server's address - https://cloud.example.com.");
            }

            var uri = new Uri(Values.BaseUrl(endpoint, string.Empty));
            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Nextcloud address - the server's alone, https://cloud.example.com; the user and app password are the connection's credentials.");
            }

            var path = uri.AbsolutePath;
            foreach (var marker in new[] { "/remote.php", "/index.php", "/ocs/" })
            {
                var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    path = path[..at];
                }
            }

            return uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/');
        }

        private static int ShareTypeOf(string? given)
        {
            var value = given ?? throw new InvalidOperationException("Give how it is shared - user, group, email, team, link or internal (type).");
            foreach (var (name, type) in ShareTypes)
            {
                if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase) || value == type.ToString(CultureInfo.InvariantCulture))
                {
                    return type;
                }
            }

            throw new InvalidOperationException($"'{value}' is not user, group, email, team, link or internal (type).");
        }

        private static int PermissionsOf(string given)
        {
            if (int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return number is >= 1 and <= 31 ? number : throw new InvalidOperationException($"'{given}' is not a number from 1 to 31 (permissions).");
            }

            var bits = 0;
            foreach (var part in given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var known = Permissions.FirstOrDefault(permission => string.Equals(permission.Name, part, StringComparison.OrdinalIgnoreCase));
                bits |= known.Name is null ? throw new InvalidOperationException($"'{part}' is not read, update, create, delete, share or all (permissions).") : known.Bits;
            }

            return bits;
        }

        private static string UserOf(string? given)
        {
            var value = given ?? throw new InvalidOperationException("Give the user's id - their login name (user).");
            return UserId.IsMatch(value) ? value : throw new InvalidOperationException($"'{value}' is not a user's id - letters, digits and _ . @ - ' (user).");
        }

        private static string EmailOf(string value, string what) =>
            OneLine(value, what) is var line && line.IndexOf('@') > 0 && line.IndexOf('@') < line.Length - 1 && !line.Contains(' ', StringComparison.Ordinal)
                ? line
                : throw new InvalidOperationException($"'{value}' is not an address - ada@example.com ({what}).");

        private static bool Bool(IReadOnlyDictionary<string, string> p, string name, bool fallback) => Values.Get(p, name) switch
        {
            null => fallback,
            var value when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            var value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            var value => throw new InvalidOperationException($"'{value}' is not true or false ({name})."),
        };

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

        // Nextcloud's OC-FileId: the id, zero-padded, then the instance's own letters - the id alone, as a list names it.
        private static long? FileId(HttpResponseMessage response) =>
            response.Headers.TryGetValues("OC-FileId", out var values) && values.FirstOrDefault() is { } value
            && long.TryParse(new string(value.TakeWhile(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

        private static string? ETag(HttpResponseMessage response) => response.Headers.ETag?.Tag.Trim('"') ?? (response.Headers.TryGetValues("OC-ETag", out var tags) ? tags.FirstOrDefault()?.Trim('"') : null);

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static ConnectorRecord Fields(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => Clr(pair.Value), StringComparer.Ordinal));

        private static object? Clr(JsonNode? node) => node is null ? null : JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(node));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        /// <summary>One signed-in Nextcloud: its address, its user's WebDAV root and the account OCS named.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly AuthenticationHeaderValue authorization;

            private Session(HttpClient client, AuthenticationHeaderValue authorization, string baseUrl)
            {
                this.client = client;
                this.authorization = authorization;
                Base = baseUrl;
            }

            public string Base { get; }

            public string UserId { get; private set; } = string.Empty;

            public JsonObject Account { get; private set; } = new();

            private string DavRoot => $"{Base}/remote.php/dav/files/{Uri.EscapeDataString(UserId)}/";

            public static async Task<Session> OpenAsync(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var baseUrl = BaseOf(endpoint);
                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Nextcloud connection has no user.");
                var password = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Nextcloud connection has no app password.");
                var session = new Session(egress.CreateHttpClient(timeout), new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password))), baseUrl);
                try
                {
                    // Who signed in: the user's id names their WebDAV root, whatever name they signed in by.
                    session.Account = await session.OcsAsync(HttpMethod.Get, "cloud/user", null, cancellationToken) as JsonObject
                        ?? throw new InvalidOperationException("Nextcloud named no account for the sign-in - check the connection's address.");
                    session.UserId = Text(session.Account["id"]) ?? throw new InvalidOperationException("Nextcloud named no id for the account - check the connection's address.");
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            public void Dispose() => client.Dispose();

            public string DavUrl(string path) => DavRoot + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

            /// <summary>An OCS call (v2, JSON): its <c>data</c>, or Nextcloud's refusal with its status and message.</summary>
            public async Task<JsonNode?> OcsAsync(HttpMethod method, string route, IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
            {
                var url = $"{Base}/ocs/v2.php/{route}?format=json";
                if (method == HttpMethod.Get && form is { Count: > 0 })
                {
                    url += "&" + string.Join("&", form.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
                }

                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = Request(method, url);
                    request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (method != HttpMethod.Get && form is not null)
                    {
                        request.Content = new FormUrlEncodedContent(form);
                    }

                    return request;
                }, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("Nextcloud refused the sign-in (401) - check the user and its app password.");
                }

                JsonObject? ocs;
                try
                {
                    ocs = (JsonNode.Parse(payload) as JsonObject)?["ocs"] as JsonObject;
                }
                catch (JsonException)
                {
                    ocs = null;
                }

                if (ocs is null)
                {
                    throw new InvalidOperationException($"Nextcloud answered {(int)response.StatusCode} with what is not its OCS JSON - check the connection's address.");
                }

                var meta = ocs["meta"] as JsonObject;
                if (!response.IsSuccessStatusCode || Text(meta?["status"]) is not "ok")
                {
                    var message = Text(meta?["message"]);
                    var code = meta?["statuscode"] is JsonValue value && value.TryGetValue<int>(out var statuscode) ? statuscode : (int)response.StatusCode;
                    throw new InvalidOperationException($"Nextcloud answered {code}{(string.IsNullOrWhiteSpace(message) ? "." : ": " + message)}");
                }

                return ocs["data"];
            }

            /// <summary>A WebDAV call on a path: its answer when Nextcloud took it, or its refusal with its status and message.</summary>
            public async Task<HttpResponseMessage> DavAsync(HttpMethod method, string path, Func<HttpContent>? content, IReadOnlyList<(string Name, string Value)>? headers, CancellationToken cancellationToken, string? suffix = null)
            {
                var url = DavUrl(path) + suffix;
                var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = Request(method, url);
                    foreach (var (name, value) in headers ?? Array.Empty<(string, string)>())
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    request.Content = content?.Invoke();
                    return request;
                }, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                using (response)
                {
                    throw Failure(response, await response.Content.ReadAsStringAsync(cancellationToken));
                }
            }

            /// <summary>A PROPFIND on a path: each response's path relative to the user's root (a folder's with its trailing slash) and its fields.</summary>
            public async Task<List<(string Relative, Dictionary<string, object?> Fields)>> PropfindAsync(string path, bool folder, int depth, CancellationToken cancellationToken)
            {
                var headers = new[] { ("Depth", depth.ToString(CultureInfo.InvariantCulture)) };
                using var response = await DavAsync(new HttpMethod("PROPFIND"), path, () => new StringContent(PropfindBody, Encoding.UTF8, "application/xml"), headers, cancellationToken, folder && path.Length > 0 ? "/" : null);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                XDocument document;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    document = XDocument.Load(reader);
                }
                catch (XmlException)
                {
                    throw new InvalidOperationException($"Nextcloud answered {(int)response.StatusCode} with what is not WebDAV - check the connection's address.");
                }

                var root = Uri.UnescapeDataString(new Uri(DavRoot).AbsolutePath);
                var entries = new List<(string, Dictionary<string, object?>)>();
                foreach (var item in document.Root?.Elements(Dav + "response") ?? Enumerable.Empty<XElement>())
                {
                    var href = (string?)item.Element(Dav + "href") ?? string.Empty;
                    var absolute = Uri.UnescapeDataString(Stomme.Shared.Utilities.TryCreateAbsoluteUri(href, out var full) && full.Scheme.StartsWith("http", StringComparison.Ordinal) ? full.AbsolutePath : href);
                    var relative = absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? absolute[root.Length..] : absolute.TrimStart('/');

                    // The properties Nextcloud has: the propstat that says 200 - the others name those it has not.
                    var props = item.Elements(Dav + "propstat").FirstOrDefault(propstat => ((string?)propstat.Element(Dav + "status") ?? string.Empty).Contains(" 200 ", StringComparison.Ordinal))?.Element(Dav + "prop");
                    var isFolder = props?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = relative,
                        ["name"] = NameOf(relative.TrimEnd('/')),
                        ["type"] = isFolder ? "folder" : "file",
                    };
                    if ((string?)props?.Element(Dav + "getlastmodified") is { } modified)
                    {
                        fields["lastModified"] = DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : modified;
                    }

                    if ((string?)props?.Element(Dav + "getcontentlength") is { } length && long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                    {
                        fields["contentLength"] = bytes;
                    }

                    if ((string?)props?.Element(Dav + "getcontenttype") is { Length: > 0 } type)
                    {
                        fields["contentType"] = type;
                    }

                    if ((string?)props?.Element(Dav + "getetag") is { } etag)
                    {
                        fields["eTag"] = etag.Trim('"');
                    }

                    if ((string?)props?.Element(Oc + "fileid") is { } id && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
                    {
                        fields["fileId"] = fileId;
                    }

                    if ((string?)props?.Element(Oc + "size") is { } size && long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var total))
                    {
                        fields["size"] = total;
                    }

                    entries.Add((relative, fields));
                }

                return entries;
            }

            /// <summary>A file's bytes written into the run's folder; its length checked against the folder's cap first.</summary>
            public async Task<ConnectorRecord> DownloadAsync(string path, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                using var response = await DavAsync(HttpMethod.Get, path, null, null, cancellationToken);
                var name = NameOf(path);
                if (response.Content.Headers.ContentLength is { } known && known > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{name}' is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var written = file.EndsWith('/') ? file + Safe(name) : file;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var length = await files.WriteAsync(written, stream, cancellationToken);
                return Record(("path", path), ("name", name), ("file", written), ("size", length), ("contentType", response.Content.Headers.ContentType?.MediaType), ("eTag", ETag(response)),
                    ("lastModified", response.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            }

            /// <summary>An upload: text, or a file of the run's folder - the same bytes from the start for each attempt; refused when something is there and it is not to be replaced.</summary>
            public async Task<ConnectorRecord> UploadAsync(string path, byte[]? text, IConnectorFiles? files, string? file, bool overwrite, CancellationToken cancellationToken)
            {
                var headers = overwrite ? null : new[] { ("If-None-Match", "*") };
                HttpResponseMessage response;
                long length;
                if (text is not null)
                {
                    length = text.LongLength;
                    response = await DavAsync(HttpMethod.Put, path, () => new ByteArrayContent(text) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, headers, cancellationToken);
                }
                else
                {
                    await using var opened = await files!.OpenReadAsync(file!, cancellationToken);
                    length = opened.Length;
                    response = await DavAsync(HttpMethod.Put, path, () =>
                    {
                        opened.Content.Position = 0;
                        return new StreamContent(new LentStream(opened.Content)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream"), ContentLength = opened.Length } };
                    }, headers, cancellationToken);
                }

                using (response)
                {
                    return Record(("path", path), ("name", NameOf(path)), ("size", length), ("replaced", response.StatusCode == HttpStatusCode.NoContent), ("eTag", ETag(response)), ("fileId", FileId(response)));
                }
            }

            private HttpRequestMessage Request(HttpMethod method, string url) => new(method, url) { Headers = { Authorization = authorization } };

            // Nextcloud's WebDAV refusal: its status and Sabre's own message, or the first line of its text.
            private static InvalidOperationException Failure(HttpResponseMessage response, string payload)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new InvalidOperationException("Nextcloud refused the sign-in (401) - check the user and its app password.");
                }

                string? message = null;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    message = (string?)XDocument.Load(reader).Root?.Element(Sabre + "message");
                }
                catch (XmlException)
                {
                }

                message ??= payload.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line.Length > 300 ? line[..300] + "..." : line : null;
                return new InvalidOperationException($"Nextcloud answered {(int)response.StatusCode}{(string.IsNullOrWhiteSpace(message) ? "." : ": " + message)}");
            }

            // A file's own name as a file of the folder: its unsafe characters replaced.
            private static string Safe(string name)
            {
                var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
                var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
                return safe.Length == 0 ? "file" : safe;
            }
        }
    }
}
