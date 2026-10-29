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
using Microsoft.AspNetCore.StaticFiles;

namespace Stomme.Modules.Connectors.GoogleCloudStorage
{
    /// <summary>
    /// Google Cloud Storage (n8n's Google Cloud Storage node: buckets and objects) over its JSON API -
    /// <c>https://storage.googleapis.com/storage/v1</c>, uploads at <c>/upload/storage/v1</c> - and the OAuth2 REFRESH-TOKEN
    /// scheme (n8n's default, its OAuth2 credential), exchanged at Google's token endpoint through the shared
    /// <see cref="OAuth2TokenProvider"/>, as Google Drive's is. A download is written into the pipeline run's folder and an
    /// upload read from it (<see cref="IConnectorFiles"/>). A test lists one bucket of a project when the connection names one,
    /// or signs in.
    /// </summary>
    /// <remarks>
    /// <para><c>buckets</c> (a project's, by a prefix), <c>bucket_get</c>, <c>bucket_create</c> (its location, storage class,
    /// labels and versioning), <c>bucket_delete</c> (an empty one), <c>objects</c> (a bucket's, by a prefix - a folder's - its
    /// subfolders as folders when a delimiter is given, its old versions when asked), <c>get</c> (an object's metadata),
    /// <c>download</c>, <c>upload</c> (a file of the run's folder or text - over several lines from the step's Query box - its
    /// type, metadata and storage class; replacing as n8n's node does, or refused when it exists, <c>ifGenerationMatch=0</c>),
    /// <c>update</c> (its metadata and headers), <c>copy</c> (Google rewriting it, as many calls as it asks) and <c>delete</c>.
    /// Each object answers as Google names its fields - its size a number, its metadata a JSON object.</para>
    /// <para>A bucket's name is held to Google's rules and an object's name escaped whole (its slashes too, as the JSON API asks)
    /// before a token is asked for. Google's refusals said with their status, its reason and its message.</para>
    /// </remarks>
    public sealed class GoogleCloudStorageConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://storage.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string BucketsOperation = "buckets";

        public const string BucketGetOperation = "bucket_get";

        public const string BucketCreateOperation = "bucket_create";

        public const string BucketDeleteOperation = "bucket_delete";

        public const string ObjectsOperation = "objects";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string UpdateOperation = "update";

        public const string CopyOperation = "copy";

        public const string DeleteOperation = "delete";

        public const int DefaultMax = 100;

        /// <summary>The most Google lists in one page.</summary>
        public const int PageSize = 1000;

        private static readonly string[] Known =
        {
            BucketsOperation, BucketGetOperation, BucketCreateOperation, BucketDeleteOperation, ObjectsOperation, GetOperation, DownloadOperation, UploadOperation, UpdateOperation, CopyOperation, DeleteOperation,
        };

        private static readonly Regex BucketName = new("^(?=.{3,222}$)[a-z0-9](?:[a-z0-9_.-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

        private static readonly Regex ProjectId = new("^[a-z][a-z0-9-]{4,28}[a-z0-9]$|^[a-z0-9.:-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly string[] StorageClasses = { "STANDARD", "NEARLINE", "COLDLINE", "ARCHIVE" };

        // The object's fields as Google names them, the transport's own left out.
        private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "kind", "acl", "selfLink", "mediaLink", "owner", "id" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googlecloudstorage";

        public string DisplayName => "Google Cloud Storage";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://cloud.google.com/storage");

        public Uri ApiDocumentationUrl { get; } = new("https://cloud.google.com/storage/docs/json_api");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted the devstorage scope)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "A Google Cloud project's id - kafe-kassa - for listing and making buckets when a step names none, and for a test."),
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField Flag(string name, string label, string description, bool fallback) => Field(name, label, description, type: ConnectorConfigFieldType.Boolean) with { Default = fallback ? "true" : "false" };

        private static ConnectorConfigField Max() => Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField ProjectField = Field("project", "Project", "The project's id - kafe-kassa. Empty: the connection's.");

        private static readonly ConnectorConfigField BucketField = Field("bucket", "Bucket", "Its name - kafe-kvitton.", required: true);

        private static readonly ConnectorConfigField ObjectField = Field("object", "Object", "Its name in the bucket - 2026/1042.pdf.", required: true);

        private static readonly ConnectorConfigField MetadataField = Field("metadata", "Metadata", "A JSON object of names and values - {\"kund\": \"ada\"}.", type: ConnectorConfigFieldType.Multiline);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(BucketsOperation, "Get buckets", "A project's buckets - n8n's bucket Get Many.") { Parameters = Schema(ProjectField, Field("prefix", "Starting with", "Only those whose names start so - kafe-."), Max()) },
            new ConnectorOperation(BucketGetOperation, "Get a bucket", "Its location, storage class, labels and dates - n8n's bucket Get.") { Parameters = Schema(BucketField) },
            new ConnectorOperation(BucketCreateOperation, "Create a bucket", "A bucket in a project - n8n's bucket Create.")
            {
                Parameters = Schema(
                    ProjectField,
                    BucketField,
                    Field("location", "Location", "Where it is kept - EU, US, europe-north1. Empty: Google's default."),
                    Field("storage_class", "Storage class", "STANDARD, NEARLINE, COLDLINE or ARCHIVE. Empty: STANDARD."),
                    Field("labels", "Labels", "A JSON object of labels - {\"avdelning\": \"kassa\"}.", type: ConnectorConfigFieldType.Multiline),
                    Flag("versioning", "Keep old versions", "Keep an object's earlier versions when it is replaced or deleted.", fallback: false)),
            },
            new ConnectorOperation(BucketDeleteOperation, "Delete a bucket", "An empty bucket - n8n's bucket Delete.") { Parameters = Schema(BucketField) },
            new ConnectorOperation(ObjectsOperation, "Get objects", "A bucket's objects - n8n's object Get Many.")
            {
                Parameters = Schema(
                    BucketField,
                    Field("prefix", "Starting with", "Only those whose names start so - 2026/ for a folder."),
                    Field("delimiter", "Folders by", "/ to list one folder's objects, its subfolders as folders. Empty: every object under the prefix."),
                    Flag("versions", "Old versions too", "Each object's earlier versions as well.", fallback: false),
                    Max()),
            },
            new ConnectorOperation(GetOperation, "Get an object", "Its size, type, dates, generation and metadata - n8n's object Get.") { Parameters = Schema(BucketField, ObjectField) },
            new ConnectorOperation(DownloadOperation, "Download an object", "An object written into the run's pipeline folder - n8n's object Get, its data.")
            {
                Parameters = Schema(BucketField, ObjectField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation(UploadOperation, "Upload an object", "A file of the run's pipeline folder, or text, stored as an object - n8n's object Create.")
            {
                Parameters = Schema(
                    BucketField,
                    Field("object", "Object", "Its name in the bucket - 2026/1042.pdf; ending with / the file's own name in that folder.", required: true),
                    Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
                    Field("text", "Text", "Text to store, as UTF-8 - over several lines in the step's Query box."),
                    Field("content_type", "Content type", "Its type - application/pdf. Empty: a file's by its name, text's text/plain."),
                    Field("storage_class", "Storage class", "STANDARD, NEARLINE, COLDLINE or ARCHIVE. Empty: the bucket's."),
                    MetadataField,
                    Flag("overwrite", "Replace what is there", "Replace an object of that name, as n8n's node does; refuse otherwise.", fallback: true)),
            },
            new ConnectorOperation(UpdateOperation, "Update an object", "Its metadata and headers - n8n's object Update.")
            {
                Parameters = Schema(
                    BucketField,
                    ObjectField,
                    MetadataField,
                    Field("content_type", "Content type", "Its type."),
                    Field("cache_control", "Cache control", "How long it may be cached - public, max-age=3600."),
                    Field("content_disposition", "Content disposition", "How a browser offers it - attachment; filename=\"1042.pdf\".")),
            },
            new ConnectorOperation(CopyOperation, "Copy an object", "An object copied to another name or bucket, by Google.")
            {
                Parameters = Schema(BucketField, ObjectField, Field("to_bucket", "To bucket", "Where it is copied - empty: the same bucket."), Field("to_object", "To object", "Its new name.", required: true)),
            },
            new ConnectorOperation(DeleteOperation, "Delete an object", "An object - n8n's object Delete.") { Parameters = Schema(BucketField, ObjectField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                if (Values.Get(context.Settings, "project") is not { } project)
                {
                    return ConnectionTestResult.Ok("Google Cloud Storage: Google gave a token; name a project in the connection to list its buckets.");
                }

                var page = await session.JsonAsync(HttpMethod.Get, $"/storage/v1/b?project={Uri.EscapeDataString(ProjectOf(project, "project"))}&maxResults=1", null, cancellationToken);
                return ConnectionTestResult.Ok($"Google Cloud Storage: the project {project} answers{(page?["items"] is JsonArray { Count: > 0 } ? string.Empty : ", no bucket yet")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ObjectsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Cloud Storage has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var overwrite = Bool(p, "overwrite", true);
            var bucket = operation == BucketsOperation ? string.Empty : BucketOf(Values.Get(p, "bucket"), "bucket");
            string objectName = string.Empty, project = string.Empty, query = string.Empty;
            string? target = null, text = null, toBucket = null, toObject = null;
            var body = new JsonObject();
            switch (operation)
            {
                case BucketsOperation or BucketCreateOperation:
                    project = ProjectOf(Values.Get(p, "project") ?? Values.Get(context.Settings, "project") ?? throw new InvalidOperationException("Give the project's id - kafe-kassa (project)."), "project");
                    if (operation == BucketsOperation)
                    {
                        query = Values.Get(p, "prefix") is { } prefix ? "&prefix=" + Uri.EscapeDataString(OneLine(prefix, "prefix")) : string.Empty;
                        break;
                    }

                    body["name"] = bucket;
                    if (Values.Get(p, "location") is { } location)
                    {
                        body["location"] = OneLine(location, "location");
                    }

                    if (Values.Get(p, "storage_class") is { } bucketClass)
                    {
                        body["storageClass"] = ClassOf(bucketClass);
                    }

                    if (Values.Get(p, "labels") is { } labels)
                    {
                        body["labels"] = JsonOf(labels, "labels");
                    }

                    if (Bool(p, "versioning", false))
                    {
                        body["versioning"] = new JsonObject { ["enabled"] = true };
                    }

                    break;
                case ObjectsOperation:
                    if (Values.Get(p, "prefix") is { } under)
                    {
                        query += "&prefix=" + Uri.EscapeDataString(OneLine(under, "prefix"));
                    }

                    if (p.TryGetValue("delimiter", out var delimiter) && !string.IsNullOrEmpty(delimiter))
                    {
                        query += "&delimiter=" + Uri.EscapeDataString(OneLine(delimiter, "delimiter"));
                    }

                    if (Bool(p, "versions", false))
                    {
                        query += "&versions=true";
                    }

                    break;
                case GetOperation or DeleteOperation:
                    objectName = ObjectOf(Values.Get(p, "object"), "object");
                    break;
                case DownloadOperation:
                    objectName = ObjectOf(Values.Get(p, "object"), "object");
                    target = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    break;
                case UploadOperation:
                    {
                        target = Values.Get(p, "file");
                        text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw // the content as given, not trimmed
                            : p.TryGetValue("query", out var box) && !string.IsNullOrEmpty(box) ? box : null;
                        if ((target is null) == (text is null))
                        {
                            throw new InvalidOperationException(target is null ? "Give the file in the site's pipeline folder to store, or text." : "Give the file or text, not both.");
                        }

                        var name = Values.Get(p, "object") ?? throw new InvalidOperationException("Give the object's name - 2026/1042.pdf.");
                        if (name.EndsWith('/'))
                        {
                            name += target is null ? throw new InvalidOperationException("Give the object's name with its own - text has none here.") : Path.GetFileName(target.Replace('\\', '/'));
                        }

                        objectName = ObjectOf(name, "object");
                        body["name"] = objectName;
                        body["contentType"] = Values.Get(p, "content_type") is { } type
                            ? MediaTypeHeaderValue.TryParse(type, out _) ? type : throw new InvalidOperationException($"'{type}' is not a content type - application/pdf (content_type).")
                            : text is not null ? "text/plain; charset=utf-8" : new FileExtensionContentTypeProvider().TryGetContentType(target!, out var byName) ? byName : "application/octet-stream";
                        if (Values.Get(p, "storage_class") is { } objectClass)
                        {
                            body["storageClass"] = ClassOf(objectClass);
                        }

                        if (Values.Get(p, "metadata") is { } metadata)
                        {
                            body["metadata"] = JsonOf(metadata, "metadata");
                        }

                        break;
                    }

                case UpdateOperation:
                    objectName = ObjectOf(Values.Get(p, "object"), "object");
                    if (Values.Get(p, "metadata") is { } changed)
                    {
                        body["metadata"] = JsonOf(changed, "metadata");
                    }

                    foreach (var (name, field) in new[] { ("content_type", "contentType"), ("cache_control", "cacheControl"), ("content_disposition", "contentDisposition") })
                    {
                        if (Values.Get(p, name) is { } value)
                        {
                            body[field] = OneLine(value, name);
                        }
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give what changes - metadata, content_type, cache_control or content_disposition.");
                    }

                    break;
                case CopyOperation:
                    objectName = ObjectOf(Values.Get(p, "object"), "object");
                    toBucket = Values.Get(p, "to_bucket") is { } other ? BucketOf(other, "to_bucket") : bucket;
                    toObject = ObjectOf(Values.Get(p, "to_object"), "to_object");
                    break;
            }

            if ((operation == DownloadOperation || (operation == UploadOperation && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            var at = $"/storage/v1/b/{Uri.EscapeDataString(bucket)}";
            var named = $"{at}/o/{Uri.EscapeDataString(objectName)}";
            switch (operation)
            {
                case BucketsOperation:
                    await foreach (var item in session.PagesAsync($"/storage/v1/b?project={Uri.EscapeDataString(project)}{query}", "items", max, cancellationToken))
                    {
                        yield return Fields(item);
                    }

                    break;

                case ObjectsOperation:
                    await foreach (var item in session.PagesAsync($"{at}/o?{query.TrimStart('&')}", "items", max, cancellationToken, folders: "prefixes"))
                    {
                        yield return item["type"] is null ? Fields(item, ("type", "file")) : Fields(item);
                    }

                    break;

                case BucketGetOperation:
                    yield return Fields(await session.JsonAsync(HttpMethod.Get, at, null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case BucketCreateOperation:
                    yield return Fields(await session.JsonAsync(HttpMethod.Post, $"/storage/v1/b?project={Uri.EscapeDataString(project)}", body, cancellationToken) as JsonObject ?? new JsonObject(), ("created", true));
                    break;

                case BucketDeleteOperation:
                    await session.JsonAsync(HttpMethod.Delete, at, null, cancellationToken);
                    yield return Record(("name", bucket), ("deleted", true));
                    break;

                case GetOperation:
                    yield return Fields(await session.JsonAsync(HttpMethod.Get, named, null, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case DownloadOperation:
                    yield return await session.DownloadAsync(bucket, objectName, named, context.Files!, target!, cancellationToken);
                    break;

                case UploadOperation:
                    yield return Fields(await session.UploadAsync($"/upload/storage/v1/b/{Uri.EscapeDataString(bucket)}/o?uploadType=multipart{(overwrite ? string.Empty : "&ifGenerationMatch=0")}", body, text, context.Files, target, cancellationToken));
                    break;

                case UpdateOperation:
                    yield return Fields(await session.JsonAsync(HttpMethod.Patch, named, body, cancellationToken) as JsonObject ?? new JsonObject());
                    break;

                case CopyOperation:
                    {
                        // A rewrite: Google copies in as many calls as it asks for, each carrying the token the last one gave.
                        var route = $"{named}/rewriteTo/b/{Uri.EscapeDataString(toBucket!)}/o/{Uri.EscapeDataString(toObject!)}";
                        JsonObject? answer = null;
                        for (var call = 0; call < 100; call++)
                        {
                            var token = answer?["rewriteToken"] is JsonValue given && given.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
                            answer = await session.JsonAsync(HttpMethod.Post, token is null ? route : route + "?rewriteToken=" + Uri.EscapeDataString(token), new JsonObject(), cancellationToken) as JsonObject ?? new JsonObject();
                            if (answer["done"] is JsonValue done && done.TryGetValue<bool>(out var finished) && finished)
                            {
                                break;
                            }
                        }

                        yield return Fields(answer?["resource"] as JsonObject ?? throw new InvalidOperationException("Google did not finish the copy in a hundred calls."), ("from", $"{bucket}/{objectName}"));
                        break;
                    }

                case DeleteOperation:
                    await session.JsonAsync(HttpMethod.Delete, named, null, cancellationToken);
                    yield return Record(("bucket", bucket), ("name", objectName), ("deleted", true));
                    break;
            }
        }

        /// <summary>A bucket's name as Google takes it: 3 to 63 lower-case letters, digits, - and _ (dots up to 222), a letter or digit first and last.</summary>
        public static string BucketOf(string? given, string what)
        {
            var value = given ?? throw new InvalidOperationException($"Give the bucket's name - kafe-kvitton ({what}).");
            return BucketName.IsMatch(value) && (value.Contains('.') || value.Length <= 63) && !value.StartsWith("goog", StringComparison.Ordinal)
                ? value
                : throw new InvalidOperationException($"'{value}' is not a bucket's name - 3 to 63 lower-case letters, digits, - and _ ({what}).");
        }

        /// <summary>An object's name as Google takes it: 1 to 1,024 characters, no control character, not . or ..; its slashes name its folders.</summary>
        public static string ObjectOf(string? given, string what)
        {
            var value = given ?? throw new InvalidOperationException($"Give the object's name - 2026/1042.pdf ({what}).");
            return value.Length is > 0 and <= 1024 && !value.Any(char.IsControl) && value is not ("." or "..")
                ? value
                : throw new InvalidOperationException($"'{value}' is not an object's name - 2026/1042.pdf, 1 to 1,024 characters ({what}).");
        }

        private static string ProjectOf(string value, string what) =>
            ProjectId.IsMatch(value) ? value : throw new InvalidOperationException($"'{value}' is not a project's id - kafe-kassa ({what}).");

        private static string ClassOf(string value) =>
            StorageClasses.FirstOrDefault(known => string.Equals(known, value, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{value}' is not STANDARD, NEARLINE, COLDLINE or ARCHIVE (storage_class).");

        private static JsonObject JsonOf(string json, string what)
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject parsed)
                {
                    return new JsonObject(parsed.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)(pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString()))));
                }
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException($"The {what} is not a JSON object - {{\"kund\": \"ada\"}}.");
        }

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

        // A bucket or an object as Google names its fields: a size a number, a nested value (metadata, labels) a JSON object.
        private static ConnectorRecord Fields(JsonObject resource, params (string Name, object? Value)[] added)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, node) in resource)
            {
                if (Dropped.Contains(name))
                {
                    continue;
                }

                fields[name] = name == "size" && node is JsonValue sized && long.TryParse(sized.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                    ? bytes
                    : node is JsonValue value ? JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(value)) : node?.ToJsonString(Wire);
            }

            foreach (var (name, value) in added)
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        /// <summary>One signed-in Google Cloud Storage: its address and its token.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;

            private Session(HttpClient client, string token, string api)
            {
                this.client = client;
                this.token = token;
                this.api = api;
            }

            public static async Task<Session> OpenAsync(GoogleCloudStorageConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Google Cloud Storage's address - its own, {DefaultEndpoint}.");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, api);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A JSON call: its answer, or Google's refusal with its status, reason and message.</summary>
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

            /// <summary>A listing page by page - Google's nextPageToken carried on - each page's items, and its folders when asked, up to <paramref name="max"/>.</summary>
            public async IAsyncEnumerable<JsonObject> PagesAsync(string route, string items, int max, [EnumeratorCancellation] CancellationToken cancellationToken, string? folders = null)
            {
                var read = 0;
                string? next = null;
                var joiner = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                while (read < max)
                {
                    var page = await JsonAsync(HttpMethod.Get, route + joiner + "maxResults=" + Math.Min(max - read, PageSize).ToString(CultureInfo.InvariantCulture) + (next is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(next)), null, cancellationToken) as JsonObject ?? new JsonObject();
                    var entries = (page[items] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone()).ToList();
                    if (folders is not null)
                    {
                        entries.AddRange((page[folders] as JsonArray ?? new JsonArray()).Select(prefix => new JsonObject { ["name"] = prefix?.GetValue<string>(), ["type"] = "folder" }));
                    }

                    foreach (var entry in entries)
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        read++;
                        yield return entry;
                    }

                    next = page["nextPageToken"] is JsonValue given && given.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
                    if (next is null)
                    {
                        yield break;
                    }
                }
            }

            /// <summary>An object's bytes (alt=media) written into the run's folder; its length checked against the folder's cap first.</summary>
            public async Task<ConnectorRecord> DownloadAsync(string bucket, string name, string route, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(HttpMethod.Get, api + route + "?alt=media"), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, await response.Content.ReadAsStringAsync(cancellationToken));
                }

                var own = name[(name.LastIndexOf('/') + 1)..];
                if (response.Content.Headers.ContentLength is { } known && known > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{own}' is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var written = file.EndsWith('/') ? file + Safe(own) : file;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var length = await files.WriteAsync(written, stream, cancellationToken);
                return Record(("bucket", bucket), ("name", name), ("file", written), ("size", length), ("contentType", response.Content.Headers.ContentType?.ToString()));
            }

            /// <summary>A multipart upload: the object's metadata, then its bytes - text, or a file of the run's folder, the same bytes from the start for each attempt.</summary>
            public async Task<JsonObject> UploadAsync(string route, JsonObject metadata, string? text, IConnectorFiles? files, string? file, CancellationToken cancellationToken)
            {
                var head = metadata.ToJsonString(Wire);
                var type = (string)metadata["contentType"]!;
                ConnectorFileContent? opened = file is null ? null : await files!.OpenReadAsync(file, cancellationToken);
                try
                {
                    var bytes = text is null ? null : Encoding.UTF8.GetBytes(text);
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                    {
                        HttpContent media;
                        if (bytes is not null)
                        {
                            media = new ByteArrayContent(bytes);
                        }
                        else
                        {
                            opened!.Content.Position = 0;
                            media = new StreamContent(new LentStream(opened.Content)) { Headers = { ContentLength = opened.Length } };
                        }

                        media.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                        var request = Request(HttpMethod.Post, api + route);
                        request.Content = new MultipartContent("related") { new StringContent(head, Encoding.UTF8, "application/json"), media };
                        return request;
                    }, cancellationToken);
                    return await ReadAsync(response, cancellationToken) as JsonObject ?? new JsonObject();
                }
                finally
                {
                    if (opened is not null)
                    {
                        await opened.DisposeAsync();
                    }
                }
            }

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
                    throw new InvalidOperationException($"Google Cloud Storage answered {(int)response.StatusCode} with something that is not JSON - check the connection's address.");
                }
            }

            // Google's refusal: its status, its first error's reason and its message.
            private static InvalidOperationException Failure(HttpResponseMessage response, string payload)
            {
                string? reason = null, message = null;
                try
                {
                    var error = (JsonNode.Parse(payload) as JsonObject)?["error"] as JsonObject;
                    message = error?["message"] is JsonValue said && said.TryGetValue<string>(out var text) ? text : null;
                    reason = (error?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["reason"] is JsonValue why && why.TryGetValue<string>(out var r) ? r : null;
                }
                catch (JsonException)
                {
                    message = payload.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line.Length > 300 ? line[..300] + "..." : line : null;
                }

                var said2 = string.Join(": ", new[] { reason, message }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return new InvalidOperationException($"Google Cloud Storage answered {(int)response.StatusCode}{(said2.Length == 0 ? "." : " " + said2)}");
            }

            // An object's own name as a file of the folder: its unsafe characters replaced.
            private static string Safe(string name)
            {
                var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
                var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
                return safe.Length == 0 ? "file" : safe;
            }
        }
    }
}
