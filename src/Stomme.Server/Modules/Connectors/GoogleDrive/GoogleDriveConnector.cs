#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using Microsoft.AspNetCore.StaticFiles;

namespace Stomme.Modules.Connectors.GoogleDrive
{
    /// <summary>
    /// Google Drive (n8n's Google Drive node: files, folders and shared drives) over the Drive API v3
    /// (<c>https://www.googleapis.com/drive/v3</c>, uploads at its <c>/upload</c> twin) and the OAuth2 REFRESH-TOKEN scheme - a
    /// Google Cloud OAuth client's id and secret and a refresh token granted the drive scope, exchanged at Google's token endpoint
    /// through the shared <see cref="OAuth2TokenProvider"/>, as Google Docs' is. A download is written into the pipeline run's
    /// folder and an upload read from it (<see cref="IConnectorFiles"/>), so a file travels between Drive and the file steps
    /// without a host path. A test names whom the token signs in as.
    /// </summary>
    /// <remarks>
    /// <para><c>search</c> (a name, Drive's own query, a folder, a shared drive, files or folders, types, the trash) page by page up
    /// to <c>max</c>; <c>get</c>; <c>download</c> - a Google Docs, Sheets, Slides or Drawings file exported as n8n's node does, to
    /// its default format for its kind unless another is given, any other file's bytes as they are; a path ending with <c>/</c>
    /// takes the file's own name - <c>upload</c> (a file of the run's folder or text, multipart, into a folder or a shared drive,
    /// converted to Google's format when asked), <c>update</c> (its name, description and properties, its content), <c>copy</c>,
    /// <c>move</c> (its current parents read first), <c>delete</c> (to the trash unless permanently), <c>share</c> (a role for a
    /// user, a group, a domain or anyone - the owner's only with ownership transferred, and no one e-mailed unless asked),
    /// <c>create_folder</c>, and the shared drives: <c>drives</c>, <c>create_drive</c> (one request id per call, so a retry makes
    /// one), <c>get_drive</c>, <c>update_drive</c>, <c>delete_drive</c>.</para>
    /// <para>A file is its id or its address (<c>drive.google.com/file/d/{id}</c>, <c>docs.google.com/document/d/{id}</c>...), a
    /// folder the same or <c>root</c>; every value held to its shape before a token is asked for. A success that is not JSON is a
    /// failure that says to check the address; a failure is thrown with its status, Google's reason and its message.</para>
    /// </remarks>
    public sealed class GoogleDriveConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://www.googleapis.com/drive/v3";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string SearchOperation = "search";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string UpdateOperation = "update";

        public const string CopyOperation = "copy";

        public const string MoveOperation = "move";

        public const string DeleteOperation = "delete";

        public const string ShareOperation = "share";

        public const string CreateFolderOperation = "create_folder";

        public const string DrivesOperation = "drives";

        public const string CreateDriveOperation = "create_drive";

        public const string GetDriveOperation = "get_drive";

        public const string UpdateDriveOperation = "update_drive";

        public const string DeleteDriveOperation = "delete_drive";

        public const int DefaultMax = 1000;

        public const string FolderType = "application/vnd.google-apps.folder";

        private const string FileFields = "id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description";

        private static readonly Regex FileId = new("^[A-Za-z0-9_-]{10,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex FileAddress = new("^https://(?:drive|docs)\\.google\\.com/(?:[A-Za-z0-9_./-]*?/)?(?:d|folders)/([A-Za-z0-9_-]{10,200})(?:[/?#].*)?$", RegexOptions.CultureInvariant);

        private static readonly Regex OpenAddress = new("^https://drive\\.google\\.com/open\\?id=([A-Za-z0-9_-]{10,200})(?:&.*)?$", RegexOptions.CultureInvariant);

        private static readonly Regex DriveId = new("^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Domain = new("^(?=.{1,253}$)[A-Za-z0-9-]{1,63}(?:\\.[A-Za-z0-9-]{1,63})*\\.[A-Za-z]{2,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex MediaType = new("^[a-z]+/[A-Za-z0-9.+-]+$", RegexOptions.CultureInvariant);

        private static readonly string[] Roles = { "reader", "commenter", "writer", "fileOrganizer", "organizer", "owner" };

        private static readonly string[] Grantees = { "user", "group", "domain", "anyone" };

        private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        private const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

        // A Google Docs editors file's kind -> the parameter naming its export, n8n's default, and what it may be exported as.
        private static readonly Dictionary<string, (string Parameter, string Default, string[] Allowed)> Exports = new(StringComparer.Ordinal)
        {
            ["application/vnd.google-apps.document"] = ("docs_as", "text/html", new[] { "text/html", "text/markdown", Docx, "application/vnd.oasis.opendocument.text", "application/pdf", "application/rtf", "text/plain" }),
            ["application/vnd.google-apps.spreadsheet"] = ("sheets_as", "text/csv", new[] { "text/csv", Xlsx, "application/vnd.oasis.opendocument.spreadsheet", "application/pdf" }),
            ["application/vnd.google-apps.presentation"] = ("slides_as", Pptx, new[] { Pptx, "application/vnd.oasis.opendocument.presentation", "application/pdf" }),
            ["application/vnd.google-apps.drawing"] = ("drawings_as", "image/jpeg", new[] { "image/jpeg", "application/pdf", "image/png", "image/svg+xml" }),
        };

        private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
        {
            ["text/html"] = ".html", ["text/markdown"] = ".md", [Docx] = ".docx", ["application/vnd.oasis.opendocument.text"] = ".odt", ["application/pdf"] = ".pdf",
            ["application/rtf"] = ".rtf", ["text/plain"] = ".txt", ["text/csv"] = ".csv", [Xlsx] = ".xlsx", ["application/vnd.oasis.opendocument.spreadsheet"] = ".ods",
            [Pptx] = ".pptx", ["application/vnd.oasis.opendocument.presentation"] = ".odp", ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/svg+xml"] = ".svg",
        };

        // What Drive converts on upload, by the content's type: into a document, a spreadsheet or a presentation.
        private static readonly Dictionary<string, string> Conversions = new(StringComparer.Ordinal)
        {
            ["text/plain"] = "application/vnd.google-apps.document", ["text/html"] = "application/vnd.google-apps.document", ["application/rtf"] = "application/vnd.google-apps.document",
            ["application/msword"] = "application/vnd.google-apps.document", [Docx] = "application/vnd.google-apps.document", ["application/vnd.oasis.opendocument.text"] = "application/vnd.google-apps.document",
            ["text/csv"] = "application/vnd.google-apps.spreadsheet", ["text/tab-separated-values"] = "application/vnd.google-apps.spreadsheet", ["application/vnd.ms-excel"] = "application/vnd.google-apps.spreadsheet",
            [Xlsx] = "application/vnd.google-apps.spreadsheet", ["application/vnd.oasis.opendocument.spreadsheet"] = "application/vnd.google-apps.spreadsheet",
            ["application/vnd.ms-powerpoint"] = "application/vnd.google-apps.presentation", [Pptx] = "application/vnd.google-apps.presentation", ["application/vnd.oasis.opendocument.presentation"] = "application/vnd.google-apps.presentation",
        };

        // JSON as written: names, accents and text as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SearchOperation, GetOperation, DownloadOperation, UploadOperation, UpdateOperation, CopyOperation, MoveOperation, DeleteOperation, ShareOperation, CreateFolderOperation, DrivesOperation, CreateDriveOperation, GetDriveOperation, UpdateDriveOperation, DeleteDriveOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googledrive";

        public string DisplayName => "Google Drive";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://drive.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/workspace/drive/api/reference/rest/v3");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted the drive scope)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField FileField = new("file_id", "File", ConnectorConfigFieldType.Text, Required: true, Description: "The file's id or address - drive.google.com/file/d/{id}.");

        private static readonly ConnectorConfigField FolderField = new("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "A folder's id or address, or root. Empty: My Drive's root, or the shared drive's.");

        private static readonly ConnectorConfigField DriveField = new("drive", "Shared drive", ConnectorConfigFieldType.Text, Required: false, Description: "A shared drive's id. Empty: My Drive.");

        private static readonly ConnectorConfigField SharedDriveField = new("drive", "Shared drive", ConnectorConfigFieldType.Text, Required: true, Description: "The shared drive's id.");

        private static ConnectorConfigField Max(int fallback = DefaultMax) => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = fallback.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField[] UploadFields =
        {
            new("file", "File", ConnectorConfigFieldType.Text, Required: false, Description: "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
            new("text", "Text", ConnectorConfigFieldType.Text, Required: false, Description: "Text to store, as UTF-8, where no file is given."),
            new("content_type", "Content type", ConnectorConfigFieldType.Text, Required: false, Description: "Its media type - application/pdf. Empty: by its name's extension (text/plain for text)."),
            new("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "The file's description."),
            new("properties", "Properties", ConnectorConfigFieldType.Text, Required: false, Description: "Its properties, a JSON object of text - {\"order\": \"1042\"}."),
            new("app_properties", "App properties", ConnectorConfigFieldType.Text, Required: false, Description: "Its properties private to this app, a JSON object of text."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search files and folders", "Files and folders by name, by Drive's own query, in a folder or a shared drive - n8n's file/folder Search.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("name", "Name contains", ConnectorConfigFieldType.Text, Required: false, Description: "Their name contains this."),
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Text, Required: false, Description: "Drive's own query - modifiedTime > '2026-01-01T00:00:00' and starred = true."),
                    FolderField, DriveField,
                    new ConnectorConfigField("what", "What", ConnectorConfigFieldType.Text, Required: false, Description: "all, files or folders.") { Default = "all" },
                    new ConnectorConfigField("types", "Types", ConnectorConfigFieldType.Text, Required: false, Description: "Media types, comma-separated - application/pdf, application/vnd.google-apps.spreadsheet."),
                    new ConnectorConfigField("trashed", "In the trash too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Files in the trash as well.") { Default = "false" },
                    Max()),
            },
            new ConnectorOperation(GetOperation, "Get a file", "A file's or a folder's name, type, size, parents and address.") { Parameters = Schema(FileField) },
            new ConnectorOperation(DownloadOperation, "Download a file", "A file written into the run's pipeline folder - a Google Docs, Sheets, Slides or Drawings file exported; n8n's file Download.")
            {
                Parameters = Schema(
                    FileField,
                    new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: true, Description: "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder."),
                    new ConnectorConfigField("docs_as", "Google Docs as", ConnectorConfigFieldType.Text, Required: false, Description: "text/html (the default), text/plain, text/markdown, application/pdf, the Word or OpenDocument type, application/rtf."),
                    new ConnectorConfigField("sheets_as", "Google Sheets as", ConnectorConfigFieldType.Text, Required: false, Description: "text/csv (the default), application/pdf, the Excel or OpenDocument type."),
                    new ConnectorConfigField("slides_as", "Google Slides as", ConnectorConfigFieldType.Text, Required: false, Description: "the PowerPoint type (the default), application/pdf, the OpenDocument type."),
                    new ConnectorConfigField("drawings_as", "Google Drawings as", ConnectorConfigFieldType.Text, Required: false, Description: "image/jpeg (the default), image/png, image/svg+xml, application/pdf.")),
            },
            new ConnectorOperation(UploadOperation, "Upload a file", "A file of the run's pipeline folder, or text, stored in a folder or a shared drive - n8n's file Upload and Create From Text.")
            {
                Parameters = Schema(new ConnectorConfigField[]
                {
                    new("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Its name in Drive. Empty: the file's own name."),
                    FolderField, DriveField,
                }.Concat(UploadFields).Append(new ConnectorConfigField("convert", "Convert", ConnectorConfigFieldType.Boolean, Required: false, Description: "Made a Google Docs, Sheets or Slides file, as its type allows.") { Default = "false" }).ToArray()),
            },
            new ConnectorOperation(UpdateOperation, "Update a file", "A file's name, description and properties, and its content from the run's folder or text - n8n's file Update.")
            {
                Parameters = Schema(new[] { FileField, new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "Its new name. Empty: as it is.") }.Concat(UploadFields).ToArray()),
            },
            new ConnectorOperation(CopyOperation, "Copy a file", "A copy, in the same folder or another - n8n's file Copy.")
            {
                Parameters = Schema(
                    FileField,
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: false, Description: "The copy's name. Empty: Copy of its name."),
                    new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "Where the copy goes. Empty: beside the file."),
                    DriveField,
                    new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "The copy's description.")),
            },
            new ConnectorOperation(MoveOperation, "Move a file", "A file or a folder into another folder - n8n's file Move.")
            {
                Parameters = Schema(FileField, new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "Its new folder's id or address, or root."), DriveField),
            },
            new ConnectorOperation(DeleteOperation, "Delete a file", "A file or a folder to the trash, or deleted permanently - n8n's file and folder Delete.")
            {
                Parameters = Schema(FileField, new ConnectorConfigField("permanently", "Permanently", ConnectorConfigFieldType.Boolean, Required: false, Description: "Deleted for good, not to the trash.") { Default = "false" }),
            },
            new ConnectorOperation(ShareOperation, "Share a file", "A role on a file or a folder for a user, a group, a domain or anyone - n8n's file and folder Share.")
            {
                Parameters = Schema(
                    FileField,
                    new ConnectorConfigField("role", "Role", ConnectorConfigFieldType.Text, Required: true, Description: "reader, commenter, writer, fileOrganizer, organizer or owner."),
                    new ConnectorConfigField("type", "To", ConnectorConfigFieldType.Text, Required: true, Description: "user, group, domain or anyone."),
                    new ConnectorConfigField("email", "E-mail", ConnectorConfigFieldType.Text, Required: false, Description: "A user's or a group's address."),
                    new ConnectorConfigField("domain", "Domain", ConnectorConfigFieldType.Text, Required: false, Description: "A domain's name - kafe.example.com."),
                    new ConnectorConfigField("discoverable", "Findable", ConnectorConfigFieldType.Boolean, Required: false, Description: "A domain's or anyone's: found by search.") { Default = "false" },
                    new ConnectorConfigField("notify", "E-mail them", ConnectorConfigFieldType.Boolean, Required: false, Description: "Google e-mails the user or the group.") { Default = "false" },
                    new ConnectorConfigField("message", "Message", ConnectorConfigFieldType.Text, Required: false, Description: "The e-mail's message."),
                    new ConnectorConfigField("transfer_ownership", "Transfer ownership", ConnectorConfigFieldType.Boolean, Required: false, Description: "Required to make someone the owner.") { Default = "false" },
                    new ConnectorConfigField("domain_admin", "As a domain administrator", ConnectorConfigFieldType.Boolean, Required: false, Description: "Act as the domain's administrator.") { Default = "false" }),
            },
            new ConnectorOperation(CreateFolderOperation, "Create a folder", "A folder in a folder or a shared drive - n8n's folder Create.")
            {
                Parameters = Schema(new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "Its name."), FolderField, DriveField),
            },
            new ConnectorOperation(DrivesOperation, "Get shared drives", "The shared drives - n8n's shared drive Get Many.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Text, Required: false, Description: "Drive's query for shared drives - name contains 'Kafé'."),
                    new ConnectorConfigField("domain_admin", "As a domain administrator", ConnectorConfigFieldType.Boolean, Required: false, Description: "Every shared drive of the domain.") { Default = "false" },
                    Max(100)),
            },
            new ConnectorOperation(CreateDriveOperation, "Create a shared drive", "A shared drive - n8n's shared drive Create.")
            {
                Parameters = Schema(new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "Its name.")),
            },
            new ConnectorOperation(GetDriveOperation, "Get a shared drive", "A shared drive - n8n's shared drive Get.") { Parameters = Schema(SharedDriveField) },
            new ConnectorOperation(UpdateDriveOperation, "Rename a shared drive", "A shared drive's name - n8n's shared drive Update.")
            {
                Parameters = Schema(SharedDriveField, new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "Its new name.")),
            },
            new ConnectorOperation(DeleteDriveOperation, "Delete a shared drive", "An empty shared drive deleted - n8n's shared drive Delete.") { Parameters = Schema(SharedDriveField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                using var client = session.Client;
                var about = await session.JsonAsync(HttpMethod.Get, session.Api + "/about?fields=" + Uri.EscapeDataString("user(displayName,emailAddress)"), null, cancellationToken);
                var user = about["user"] as JsonObject;
                return ConnectionTestResult.Ok($"Google Drive: signed in as {Text(user?["emailAddress"]) ?? Text(user?["displayName"]) ?? "an account"}.");
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
                ?? throw new InvalidOperationException($"Google Drive has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : operation == DrivesOperation ? 100 : DefaultMax;
            var fileId = operation is GetOperation or DownloadOperation or UpdateOperation or CopyOperation or MoveOperation or DeleteOperation or ShareOperation ? File(Values.Get(p, "file_id"), "file_id") : null;
            var folder = Values.Get(p, "folder") is { } within ? Folder(within) : null;
            var drive = Values.Get(p, "drive") is { } shared ? DriveId.IsMatch(shared) ? shared : throw new InvalidOperationException($"'{shared}' is not a shared drive's id.") : null;
            if (operation is GetDriveOperation or UpdateDriveOperation or DeleteDriveOperation && drive is null)
            {
                throw new InvalidOperationException("Give the shared drive - its id.");
            }

            var name = Values.Get(p, "name") is { } named ? OneLine(named, "name") : null;
            if (operation is CreateFolderOperation or CreateDriveOperation or UpdateDriveOperation && name is null)
            {
                throw new InvalidOperationException("Give the name.");
            }

            var target = Values.Get(p, "file");
            if (operation == DownloadOperation && target is null)
            {
                throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
            }

            var exports = operation == DownloadOperation ? ExportsOf(p) : null;
            var body = operation is UploadOperation or UpdateOperation ? Body.Of(p, target, name, operation == UploadOperation) : null;
            var query = operation == SearchOperation ? SearchQuery(p, folder) : null;
            var share = operation == ShareOperation ? Share(p) : null;
            if ((operation == DownloadOperation || body?.File is not null) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            using var client = session.Client;
            var fields = "&fields=" + Uri.EscapeDataString(FileFields);
            switch (operation)
            {
                case SearchOperation:
                    {
                        var corpora = drive is null ? "corpora=user" : "corpora=drive&driveId=" + drive;
                        string? token = null;
                        var read = 0;
                        while (read < max)
                        {
                            var url = $"{session.Api}/files?{corpora}&includeItemsFromAllDrives=true&supportsAllDrives=true&q={Uri.EscapeDataString(query!)}&pageSize={Math.Min(1000, max - read)}"
                                + "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + FileFields + ")") + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
                            var page = await session.JsonAsync(HttpMethod.Get, url, null, cancellationToken);
                            foreach (var file in (page["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                if (read == max)
                                {
                                    yield break;
                                }

                                read++;
                                yield return Record(file);
                            }

                            var next = Text(page["nextPageToken"]);
                            if (next is null)
                            {
                                yield break;
                            }

                            token = next != token ? next : throw new InvalidOperationException("Google Drive named the same next page again.");
                        }

                        break;
                    }

                case GetOperation:
                    yield return Record(await session.JsonAsync(HttpMethod.Get, $"{session.Api}/files/{fileId}?supportsAllDrives=true{fields}", null, cancellationToken));
                    break;

                case DownloadOperation:
                    {
                        var files = context.Files!;
                        var meta = await session.JsonAsync(HttpMethod.Get, $"{session.Api}/files/{fileId}?supportsAllDrives=true&fields=" + Uri.EscapeDataString("id,name,mimeType,size"), null, cancellationToken);
                        var type = Text(meta["mimeType"]) ?? "application/octet-stream";
                        var fileName = Text(meta["name"]) ?? fileId!;
                        string url;
                        string? exportedAs = null;
                        if (Exports.TryGetValue(type, out var export))
                        {
                            exportedAs = exports![export.Parameter];
                            url = $"{session.Api}/files/{fileId}/export?mimeType={Uri.EscapeDataString(exportedAs)}";
                        }
                        else if (type == FolderType)
                        {
                            throw new InvalidOperationException($"'{fileName}' is a folder; a folder is not downloaded.");
                        }
                        else if (type.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"'{fileName}' is a Google file ({type}) with no export here - only Docs, Sheets, Slides and Drawings are exported.");
                        }
                        else
                        {
                            if (long.TryParse(Text(meta["size"]), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > files.MaxBytes)
                            {
                                throw new InvalidOperationException($"'{fileName}' is {size} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                            }

                            url = $"{session.Api}/files/{fileId}?alt=media&supportsAllDrives=true";
                        }

                        var path = target!.EndsWith('/') ? target + Named(fileName, exportedAs) : target;
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                        await session.EnsureAsync(response, cancellationToken);
                        if (response.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
                        {
                            throw new InvalidOperationException($"'{fileName}' is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                        }

                        long written;
                        await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            written = await files.WriteAsync(path, content, cancellationToken);
                        }

                        yield return Fields(("id", fileId), ("name", fileName), ("file", path), ("size", written), ("mimeType", exportedAs ?? type), ("exported_from", exportedAs is null ? null : type));
                        break;
                    }

                case UploadOperation:
                    {
                        var metadata = body!.Metadata();
                        if (Parent(folder, drive) is { } parent)
                        {
                            metadata["parents"] = new JsonArray(parent);
                        }

                        yield return Record(await body.SendAsync(session, HttpMethod.Post, $"{session.Upload}/files?uploadType=multipart&supportsAllDrives=true{fields}", metadata, context.Files, cancellationToken));
                        break;
                    }

                case UpdateOperation:
                    {
                        var metadata = body!.Metadata();
                        yield return Record(body.HasContent
                            ? await body.SendAsync(session, HttpMethod.Patch, $"{session.Upload}/files/{fileId}?uploadType=multipart&supportsAllDrives=true{fields}", metadata, context.Files, cancellationToken)
                            : await session.JsonAsync(HttpMethod.Patch, $"{session.Api}/files/{fileId}?supportsAllDrives=true{fields}", metadata, cancellationToken));
                        break;
                    }

                case CopyOperation:
                    {
                        var copy = new JsonObject();
                        if (name is null)
                        {
                            var original = await session.JsonAsync(HttpMethod.Get, $"{session.Api}/files/{fileId}?supportsAllDrives=true&fields=name", null, cancellationToken);
                            name = Text(original["name"]) is { } was ? "Copy of " + was : null;
                        }

                        if (name is not null)
                        {
                            copy["name"] = name;
                        }

                        if (folder is not null || drive is not null)
                        {
                            copy["parents"] = new JsonArray(Parent(folder, drive) ?? "root");
                        }

                        if (Values.Get(p, "description") is { } described)
                        {
                            copy["description"] = described;
                        }

                        yield return Record(await session.JsonAsync(HttpMethod.Post, $"{session.Api}/files/{fileId}/copy?supportsAllDrives=true{fields}", copy, cancellationToken));
                        break;
                    }

                case MoveOperation:
                    {
                        var to = Parent(folder, drive) ?? "root";
                        var current = await session.JsonAsync(HttpMethod.Get, $"{session.Api}/files/{fileId}?supportsAllDrives=true&fields=parents", null, cancellationToken);
                        var from = string.Join(",", (current["parents"] as JsonArray ?? new JsonArray()).Select(Text).Where(parent => parent is not null));
                        yield return Record(await session.JsonAsync(HttpMethod.Patch, $"{session.Api}/files/{fileId}?supportsAllDrives=true&addParents={Uri.EscapeDataString(to)}&removeParents={Uri.EscapeDataString(from)}{fields}", new JsonObject(), cancellationToken));
                        break;
                    }

                case DeleteOperation:
                    {
                        var permanently = Flag(p, "permanently");
                        if (permanently)
                        {
                            await session.JsonAsync(HttpMethod.Delete, $"{session.Api}/files/{fileId}?supportsAllDrives=true", null, cancellationToken);
                        }
                        else
                        {
                            await session.JsonAsync(HttpMethod.Patch, $"{session.Api}/files/{fileId}?supportsAllDrives=true&fields=id", new JsonObject { ["trashed"] = true }, cancellationToken);
                        }

                        yield return Fields(("id", fileId), ("deleted", true), ("permanently", permanently));
                        break;
                    }

                case ShareOperation:
                    {
                        var (permission, options) = share!.Value;
                        var answer = await session.JsonAsync(HttpMethod.Post, $"{session.Api}/files/{fileId}/permissions?supportsAllDrives=true{options}", permission, cancellationToken);
                        answer["fileId"] = fileId;
                        yield return Record(answer);
                        break;
                    }

                case CreateFolderOperation:
                    {
                        var made = new JsonObject { ["name"] = name, ["mimeType"] = FolderType };
                        if (Parent(folder, drive) is { } parent)
                        {
                            made["parents"] = new JsonArray(parent);
                        }

                        yield return Record(await session.JsonAsync(HttpMethod.Post, $"{session.Api}/files?supportsAllDrives=true{fields}", made, cancellationToken));
                        break;
                    }

                case DrivesOperation:
                    {
                        var asAdmin = Flag(p, "domain_admin") ? "&useDomainAdminAccess=true" : "";
                        var filter = Values.Get(p, "query") is { } q ? "&q=" + Uri.EscapeDataString(q) : "";
                        string? token = null;
                        var read = 0;
                        while (read < max)
                        {
                            var page = await session.JsonAsync(HttpMethod.Get, $"{session.Api}/drives?pageSize={Math.Min(100, max - read)}{filter}{asAdmin}" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token)), null, cancellationToken);
                            foreach (var each in (page["drives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                if (read == max)
                                {
                                    yield break;
                                }

                                read++;
                                yield return Record(each);
                            }

                            var next = Text(page["nextPageToken"]);
                            if (next is null)
                            {
                                yield break;
                            }

                            token = next != token ? next : throw new InvalidOperationException("Google Drive named the same next page again.");
                        }

                        break;
                    }

                case CreateDriveOperation:
                    // One request id per call: a retry of the same request makes the one drive, never two.
                    yield return Record(await session.JsonAsync(HttpMethod.Post, $"{session.Api}/drives?requestId={Guid.NewGuid():N}", new JsonObject { ["name"] = name }, cancellationToken));
                    break;

                case GetDriveOperation:
                    yield return Record(await session.JsonAsync(HttpMethod.Get, $"{session.Api}/drives/{drive}", null, cancellationToken));
                    break;

                case UpdateDriveOperation:
                    yield return Record(await session.JsonAsync(HttpMethod.Patch, $"{session.Api}/drives/{drive}", new JsonObject { ["name"] = name }, cancellationToken));
                    break;

                case DeleteDriveOperation:
                    await session.JsonAsync(HttpMethod.Delete, $"{session.Api}/drives/{drive}", null, cancellationToken);
                    yield return Fields(("id", drive), ("deleted", true));
                    break;
            }
        }

        /// <summary>A file's id, from itself or from its address.</summary>
        internal static string File(string? given, string what)
        {
            if (given is null)
            {
                throw new InvalidOperationException($"Give the {what.Replace('_', ' ')} - its id or its address.");
            }

            if (FileAddress.Match(given) is { Success: true } address)
            {
                return address.Groups[1].Value;
            }

            if (OpenAddress.Match(given) is { Success: true } open)
            {
                return open.Groups[1].Value;
            }

            return FileId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a file - its id, or its address (drive.google.com/file/d/{{id}}).");
        }

        private static string Folder(string given) => string.Equals(given, "root", StringComparison.OrdinalIgnoreCase) ? "root" : File(given, "folder");

        // Where something goes: a folder named, else the shared drive's root (its id), else none - My Drive's root.
        private static string? Parent(string? folder, string? drive) => folder is not null && folder != "root" ? folder : drive ?? folder;

        // n8n's search as Drive's own query: a name it contains, the raw query, a folder's children, files or folders, types, the trash.
        private static string SearchQuery(IReadOnlyDictionary<string, string> p, string? folder)
        {
            var clauses = new List<string>();
            if (Values.Get(p, "name") is { } name)
            {
                clauses.Add("name contains '" + Quoted(name) + "'");
            }

            if (Values.Get(p, "query") is { } raw)
            {
                clauses.Add(raw.Contains('\n', StringComparison.Ordinal) ? throw new InvalidOperationException("The query has a line break - one line only.") : "(" + raw + ")");
            }

            if (folder is not null)
            {
                clauses.Add("'" + Quoted(folder) + "' in parents");
            }

            var what = Values.GetOrDefault(p, "what", "all").ToLowerInvariant();
            switch (what)
            {
                case "all":
                    break;
                case "files":
                    clauses.Add($"mimeType != '{FolderType}'");
                    break;
                case "folders":
                    clauses.Add($"mimeType = '{FolderType}'");
                    break;
                default:
                    throw new InvalidOperationException($"'{what}' is not all, files or folders (what).");
            }

            if (Values.Get(p, "types") is { } types && what != "folders")
            {
                var listed = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(type => MediaType.IsMatch(type) ? $"mimeType = '{type}'" : throw new InvalidOperationException($"'{type}' is not a media type - application/pdf (types).")).ToList();
                if (listed.Count > 0)
                {
                    clauses.Add("(" + string.Join(" or ", listed) + ")");
                }
            }

            if (!Flag(p, "trashed"))
            {
                clauses.Add("trashed = false");
            }

            return string.Join(" and ", clauses);
        }

        // A Drive query's string, its backslashes and quotes escaped.
        private static string Quoted(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        // The export each kind takes: as given, or n8n's default for the kind - one it may be exported as.
        private static Dictionary<string, string> ExportsOf(IReadOnlyDictionary<string, string> p)
        {
            var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, (parameter, fallback, allowed)) in Exports)
            {
                var given = Values.Get(p, parameter) ?? fallback;
                chosen[parameter] = allowed.Contains(given, StringComparer.Ordinal) ? given : throw new InvalidOperationException($"'{given}' is not one of {string.Join(", ", allowed)} ({parameter}).");
            }

            return chosen;
        }

        // A downloaded file's own name as a file of the folder: its unsafe characters replaced, the export's extension added.
        private static string Named(string name, string? exportedAs)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
            safe = safe.Length == 0 ? "file" : safe;
            return exportedAs is not null && Extensions.TryGetValue(exportedAs, out var extension) && !safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? safe + extension : safe;
        }

        // A permission and the query that asks for it: the role for whom, the owner's with ownership transferred, no e-mail unless asked.
        private static (JsonObject Permission, string Options)? Share(IReadOnlyDictionary<string, string> p)
        {
            var role = Values.Get(p, "role") is { } r ? Roles.FirstOrDefault(known => string.Equals(known, r, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{r}' is not a role - {string.Join(", ", Roles)}.") : throw new InvalidOperationException("Give the role.");
            var type = Values.Get(p, "type") is { } t ? Grantees.FirstOrDefault(known => string.Equals(known, t, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{t}' is not user, group, domain or anyone (type).") : throw new InvalidOperationException("Give whom it is shared with - user, group, domain or anyone (type).");
            var permission = new JsonObject { ["role"] = role, ["type"] = type };
            switch (type)
            {
                case "user" or "group":
                    var email = Values.Get(p, "email") ?? throw new InvalidOperationException($"Give the {type}'s e-mail address.");
                    permission["emailAddress"] = Email.IsMatch(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail address.");
                    break;
                case "domain":
                    var domain = Values.Get(p, "domain") ?? throw new InvalidOperationException("Give the domain.");
                    permission["domain"] = Domain.IsMatch(domain) ? domain : throw new InvalidOperationException($"'{domain}' is not a domain - kafe.example.com.");
                    break;
            }

            if (type is "domain" or "anyone")
            {
                permission["allowFileDiscovery"] = Flag(p, "discoverable");
            }

            var transfer = Flag(p, "transfer_ownership");
            if (role == "owner" && !transfer)
            {
                throw new InvalidOperationException("Making someone the owner transfers the file - set transfer_ownership to true.");
            }

            var notify = Flag(p, "notify");
            var options = "&sendNotificationEmail=" + (notify ? "true" : "false")
                + (transfer ? "&transferOwnership=true" : "")
                + (Flag(p, "domain_admin") ? "&useDomainAdminAccess=true" : "")
                + (Values.Get(p, "message") is { } message ? notify ? "&emailMessage=" + Uri.EscapeDataString(message) : throw new InvalidOperationException("A message goes with an e-mail - set notify to true.") : "");
            return (permission, options);
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(pair.Value)), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static bool Flag(IReadOnlyDictionary<string, string> parameters, string name) =>
            Values.Get(parameters, name) is not { } given ? false
                : given.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                : given.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidOperationException($"'{given}' is not true or false ({name.Replace('_', ' ')}).");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // An upload's or an update's body: its metadata, and its content - a file of the run's folder or text - typed.
        private sealed class Body
        {
            private readonly string? name;
            private readonly byte[]? text;
            private readonly string? contentType;
            private readonly string? description;
            private readonly JsonObject? properties;
            private readonly JsonObject? appProperties;
            private readonly bool convert;

            private Body(string? file, string? name, byte[]? text, string? contentType, string? description, JsonObject? properties, JsonObject? appProperties, bool convert)
            {
                File = file;
                this.name = name;
                this.text = text;
                this.contentType = contentType;
                this.description = description;
                this.properties = properties;
                this.appProperties = appProperties;
                this.convert = convert;
            }

            public string? File { get; }

            public bool HasContent => File is not null || text is not null;

            public static Body Of(IReadOnlyDictionary<string, string> p, string? file, string? name, bool creating)
            {
                // The text as given - not trimmed: it is the file's content.
                var text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw : null;
                if (file is not null && text is not null)
                {
                    throw new InvalidOperationException("Give the file or text, not both.");
                }

                if (creating && file is null && text is null)
                {
                    throw new InvalidOperationException("Give the file in the site's pipeline folder to store, or text.");
                }

                if (creating && file is null && name is null)
                {
                    throw new InvalidOperationException("Give the name - text has none of its own.");
                }

                var type = Values.Get(p, "content_type");
                if (type is not null && !MediaTypeHeaderValue.TryParse(type, out _))
                {
                    throw new InvalidOperationException($"'{type}' is not a media type - application/pdf (content_type).");
                }

                var convert = creating && Flag(p, "convert");
                var body = new Body(file, name, text is null ? null : Encoding.UTF8.GetBytes(text), type, Values.Get(p, "description"), Properties(p, "properties"), Properties(p, "app_properties"), convert);
                if (convert)
                {
                    _ = body.Converted(); // a type Drive will not convert refused now
                }

                return body;
            }

            private static JsonObject? Properties(IReadOnlyDictionary<string, string> p, string name)
            {
                if (Values.Get(p, name) is not { } json)
                {
                    return null;
                }

                JsonObject given;
                try
                {
                    given = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException($"'{json}' is not a JSON object of text ({name}).");
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException($"'{json}' is not a JSON object of text ({name}).");
                }

                var properties = new JsonObject();
                foreach (var (key, value) in given)
                {
                    properties[key] = value is JsonValue scalar ? scalar.ToString() : throw new InvalidOperationException($"Property '{key}' is not text ({name}).");
                }

                return properties;
            }

            // Its content's type: as given, or by its name's (or the file's) extension, text/plain for text.
            private string Type() =>
                contentType ?? (text is not null ? "text/plain" : new FileExtensionContentTypeProvider().TryGetContentType(name ?? File!, out var byName) ? byName : "application/octet-stream");

            private string Converted() =>
                Conversions.TryGetValue(Type().Split(';')[0].Trim(), out var google) ? google : throw new InvalidOperationException($"Drive makes a Google file of a document, a spreadsheet or a presentation - '{Type()}' is none of them (convert).");

            public JsonObject Metadata()
            {
                var metadata = new JsonObject();
                if ((name ?? (File is null ? null : System.IO.Path.GetFileName(File.Replace('\\', '/')))) is { } called)
                {
                    metadata["name"] = called;
                }

                if (description is not null)
                {
                    metadata["description"] = description;
                }

                if (properties is not null)
                {
                    metadata["properties"] = properties.DeepClone();
                }

                if (appProperties is not null)
                {
                    metadata["appProperties"] = appProperties.DeepClone();
                }

                if (convert)
                {
                    metadata["mimeType"] = Converted();
                }

                return metadata;
            }

            // Multipart: the metadata, then the content - the same bytes from the start for each attempt.
            public async Task<JsonObject> SendAsync(Session session, HttpMethod method, string url, JsonObject metadata, IConnectorFiles? files, CancellationToken cancellationToken)
            {
                var json = metadata.ToJsonString(Wire);
                var type = Type();
                if (text is not null)
                {
                    return await session.JsonAsync(() => Request(method, url, json, new ByteArrayContent(text), type), cancellationToken);
                }

                await using var opened = await files!.OpenReadAsync(File!, cancellationToken);
                return await session.JsonAsync(() =>
                {
                    opened.Content.Position = 0;
                    return Request(method, url, json, new StreamContent(new LentStream(opened.Content)), type);
                }, cancellationToken);
            }

            private static HttpRequestMessage Request(HttpMethod method, string url, string metadata, HttpContent media, string type)
            {
                var boundary = "stomme" + Guid.NewGuid().ToString("N");
                var multipart = new MultipartContent("related", boundary);
                multipart.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/related; boundary=" + boundary);
                multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
                media.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                multipart.Add(media);
                return new HttpRequestMessage(method, url) { Content = multipart };
            }
        }

        // One connection's calls: Drive's address and its upload twin, the token on every request; a failure thrown with its status,
        // Google's reason and its message.
        private sealed class Session
        {
            private readonly string token;

            private Session(string api, string upload, string token, HttpClient client)
            {
                Api = api;
                Upload = upload;
                this.token = token;
                Client = client;
            }

            public string Api { get; }

            public string Upload { get; }

            public HttpClient Client { get; }

            public static async Task<Session> OpenAsync(GoogleDriveConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (!uri.AbsolutePath.EndsWith("/drive/v3", StringComparison.Ordinal) || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Drive's API - its address ends with /drive/v3, https://www.googleapis.com/drive/v3.");
                }

                var upload = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..^"/drive/v3".Length] + "/upload/drive/v3";
                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(api, upload, token, egress.CreateHttpClient(timeout));
            }

            public Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead) =>
                RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = build();
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return request;
                }, cancellationToken, completion);

            public Task<JsonObject> JsonAsync(HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
            {
                var content = body?.ToJsonString(Wire);
                return JsonAsync(() =>
                {
                    var request = new HttpRequestMessage(method, url);
                    if (content is not null)
                    {
                        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
            }

            public async Task<JsonObject> JsonAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
            {
                using var response = await SendAsync(build, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Google Drive answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, json);
                }

                return json as JsonObject ?? new JsonObject();
            }

            /// <summary>A failed answer thrown with its status, Google's reason and its message.</summary>
            public async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
            {
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException)
                {
                }

                throw Failure(response, json);
            }

            private static InvalidOperationException Failure(HttpResponseMessage response, JsonNode? json)
            {
                var error = json?["error"];
                var reason = Text(error?["errors"]?[0]?["reason"]) ?? Text(error?["status"]);
                var said = Text(error?["message"]);
                return new InvalidOperationException($"HTTP {(int)response.StatusCode}{(reason is null ? "" : " " + reason)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }
        }
    }
}
