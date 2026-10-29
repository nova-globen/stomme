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
using System.Security.Cryptography;
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
using Microsoft.AspNetCore.StaticFiles;

namespace Stomme.Modules.Connectors.AwsS3
{
    /// <summary>
    /// Amazon S3 and the stores that speak its API - MinIO, Ceph, Cloudflare R2, Wasabi (n8n's AWS S3 and S3 nodes): a
    /// bucket's objects listed, downloaded, uploaded, copied and deleted, its folders made, listed and deleted, buckets listed,
    /// made and deleted, every call signed with AWS Signature Version 4 by an access key and its secret over the host's egress.
    /// A download is written into the pipeline run's folder and an upload read from it (<see cref="IConnectorFiles"/>), so a file
    /// travels between S3 and the file steps without a host path; text can be uploaded as well. A test lists the buckets, or the
    /// connection's bucket when it names one.
    /// </summary>
    /// <remarks>
    /// <para>Buckets are addressed by host (<c>{bucket}.s3.{region}.amazonaws.com</c>, AWS's way) or by path
    /// (<c>{endpoint}/{bucket}</c>, the setting most S3-compatible stores need). A key is sent encoded segment by segment and is
    /// REFUSED when a segment is <c>.</c> or <c>..</c> - an address normalised on the way would name another object - when it
    /// starts with <c>/</c>, holds a control character or passes S3's 1,024 bytes. An upload is signed with its body's SHA-256,
    /// read from the file first, so the store checks what arrived; a download over the folder's cap is refused before its body
    /// is read, by the length S3 names, and stopped at the cap where none is named.</para>
    /// <para>Listing follows S3's continuation tokens up to <c>max</c>. A success that is not S3's XML - a sign-in page, a proxy's -
    /// is a failure that says to check the address; a copy whose failure S3 reports inside its 200 is a failure. A bucket in
    /// another region is named in the failure. Folder delete lists every key under the folder and deletes them a thousand at a
    /// time, naming the first S3 refuses; an empty folder name is refused, so a bucket is never emptied by a blank.</para>
    /// </remarks>
    public sealed class AwsS3Connector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string BucketsOperation = "buckets";

        public const string CreateBucketOperation = "create_bucket";

        public const string DeleteBucketOperation = "delete_bucket";

        public const string ObjectsOperation = "objects";

        public const string FoldersOperation = "folders";

        public const string CreateFolderOperation = "create_folder";

        public const string DeleteFolderOperation = "delete_folder";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string CopyOperation = "copy";

        public const string DeleteOperation = "delete";

        public const int DefaultMax = 1000;

        private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

        private static readonly Regex Region = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

        private static readonly Regex BucketName = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex MetadataName = new("^[A-Za-z0-9-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly string[] StorageClasses = { "STANDARD", "REDUCED_REDUNDANCY", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE", "GLACIER_IR", "OUTPOSTS", "EXPRESS_ONEZONE" };

        private static readonly string[] Acls = { "private", "public-read", "public-read-write", "authenticated-read", "aws-exec-read", "bucket-owner-read", "bucket-owner-full-control" };

        private static readonly string[] Known = { BucketsOperation, CreateBucketOperation, DeleteBucketOperation, ObjectsOperation, FoldersOperation, CreateFolderOperation, DeleteFolderOperation, DownloadOperation, UploadOperation, CopyOperation, DeleteOperation };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awss3";

        public string DisplayName => "Amazon S3";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://s3.eu-north-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/s3/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/AmazonS3/latest/API/Welcome.html");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access-key"] = "Access key ID (IAM > Users > the user > Security credentials; a store's own key for MinIO, R2 or Ceph)",
            ["secret-key"] = "Secret access key (shown once, when the key is made)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The bucket's region - eu-north-1 (us-east-1 for most S3-compatible stores, auto for R2). The address is S3's in it when left empty."),
            new ConnectorConfigField("path_style", "Address buckets by path", ConnectorConfigFieldType.Boolean, Required: false, Description: "Address a bucket as {address}/{bucket} rather than as {bucket}.{address} - on for MinIO, Ceph and most S3-compatible stores, and for a bucket whose name holds a dot.") { Default = "false" },
            new ConnectorConfigField("bucket", "Bucket", ConnectorConfigFieldType.Text, Required: false, Description: "The bucket an operation uses when it names none - and the one a test reads."),
        });

        private static readonly ConnectorConfigField BucketField = new("bucket", "Bucket", ConnectorConfigFieldType.Text, Required: false, Description: "Its name. Empty: the connection's bucket.");

        private static readonly ConnectorConfigField KeyField = new("key", "Key", ConnectorConfigFieldType.Text, Required: true, Description: "The object's key - invoices/2026/1042.pdf.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ObjectsOperation, "Get objects", "A bucket's objects - under a prefix, after a key, folders as their own records with a delimiter; n8n's file Get Many and bucket Search.")
            {
                Parameters = Schema(
                    BucketField,
                    new ConnectorConfigField("prefix", "Prefix", ConnectorConfigFieldType.Text, Required: false, Description: "Only keys starting so - invoices/2026/."),
                    new ConnectorConfigField("delimiter", "Delimiter", ConnectorConfigFieldType.Text, Required: false, Description: "/ to read one folder level: its objects, and its folders as records of their own."),
                    new ConnectorConfigField("start_after", "Start after", ConnectorConfigFieldType.Text, Required: false, Description: "Only keys after this one."),
                    Max()),
            },
            new ConnectorOperation(DownloadOperation, "Download an object", "An object written into the run's pipeline folder, for the file steps to read - n8n's file Download.")
            {
                Parameters = Schema(BucketField, KeyField, new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: true, Description: "Where in the site's pipeline folder it is written - downloads/1042.pdf.")),
            },
            new ConnectorOperation(UploadOperation, "Upload an object", "A file of the run's pipeline folder, or text, stored as an object - replacing one with its key; n8n's file Upload.")
            {
                Parameters = Schema(
                    BucketField, KeyField,
                    new ConnectorConfigField("file", "File", ConnectorConfigFieldType.Text, Required: false, Description: "The file in the site's pipeline folder to store - reports/june.pdf. Or give text."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Text, Required: false, Description: "Text to store, as UTF-8, where no file is given."),
                    new ConnectorConfigField("content_type", "Content type", ConnectorConfigFieldType.Text, Required: false, Description: "Its media type - application/pdf. Empty: by the key's extension (text/plain for text)."),
                    new ConnectorConfigField("storage_class", "Storage class", ConnectorConfigFieldType.Text, Required: false, Description: "STANDARD, STANDARD_IA, INTELLIGENT_TIERING, GLACIER_IR... Empty: the bucket's default."),
                    new ConnectorConfigField("acl", "Access", ConnectorConfigFieldType.Text, Required: false, Description: "A canned ACL - private, public-read, bucket-owner-full-control... Empty: the bucket's default."),
                    new ConnectorConfigField("metadata", "Metadata", ConnectorConfigFieldType.Text, Required: false, Description: "Its metadata, a JSON object of text - {\"order\": \"1042\"}.")),
            },
            new ConnectorOperation(CopyOperation, "Copy an object", "An object copied to another key, in this bucket or from another - n8n's file Copy.")
            {
                Parameters = Schema(BucketField, new ConnectorConfigField("key", "Key", ConnectorConfigFieldType.Text, Required: true, Description: "The copy's key."),
                    new ConnectorConfigField("source", "Source", ConnectorConfigFieldType.Text, Required: true, Description: "The object copied, as bucket/key - kafe-docs/invoices/1042.pdf.")),
            },
            new ConnectorOperation(DeleteOperation, "Delete an object", "An object deleted by its key - n8n's file Delete.") { Parameters = Schema(BucketField, KeyField) },
            new ConnectorOperation(FoldersOperation, "Get folders", "The folders one level under a folder - n8n's folder Get Many.")
            {
                Parameters = Schema(BucketField, new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "The folder they are in - invoices/2026. Empty: the bucket's top."), Max()),
            },
            new ConnectorOperation(CreateFolderOperation, "Create a folder", "An empty folder - n8n's folder Create.")
            {
                Parameters = Schema(BucketField, new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: true, Description: "Its path - invoices/2026.")),
            },
            new ConnectorOperation(DeleteFolderOperation, "Delete a folder", "A folder and every object under it - n8n's folder Delete.")
            {
                Parameters = Schema(BucketField, new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: true, Description: "Its path - invoices/2026; never empty.")),
            },
            new ConnectorOperation(BucketsOperation, "Get buckets", "The buckets the key sees - n8n's bucket Get Many.") { Parameters = Schema(Max()) },
            new ConnectorOperation(CreateBucketOperation, "Create a bucket", "A bucket in the connection's region - n8n's bucket Create.")
            {
                Parameters = Schema(new ConnectorConfigField("bucket", "Bucket", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - lower-case letters, digits, dots and hyphens, 3 to 63.")),
            },
            new ConnectorOperation(DeleteBucketOperation, "Delete a bucket", "An empty bucket deleted - n8n's bucket Delete.")
            {
                Parameters = Schema(new ConnectorConfigField("bucket", "Bucket", ConnectorConfigFieldType.Text, Required: true, Description: "Its name.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                if (Values.Get(context.Settings, "bucket") is { } named)
                {
                    var bucket = Bucket(named, session.PathStyle);
                    var page = await session.XmlAsync(HttpMethod.Get, session.Url(bucket, null, "list-type=2&max-keys=1"), "ListBucketResult", cancellationToken);
                    return ConnectionTestResult.Ok($"S3 in {session.Region}: bucket '{bucket}' answers{(Child(page, "KeyCount") == "0" ? ", empty" : "")}.");
                }

                var found = await session.XmlAsync(HttpMethod.Get, session.Url(null, null, null), "ListAllMyBucketsResult", cancellationToken);
                var count = Elements(found, "Buckets", "Bucket").Count();
                return ConnectionTestResult.Ok($"S3 in {session.Region}: {(count == 1 ? "one bucket" : $"{count} buckets")} seen.");
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
                ?? throw new InvalidOperationException($"Amazon S3 has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed or sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var pathStyle = Values.GetBool(context.Settings, "path_style", false);
            string? bucket = null;
            if (operation != BucketsOperation)
            {
                var named = operation is CreateBucketOperation or DeleteBucketOperation ? Values.Get(p, "bucket") : Values.Get(p, "bucket") ?? Values.Get(context.Settings, "bucket");
                bucket = Bucket(named ?? throw new InvalidOperationException("Give the bucket - its name, or the connection's."), pathStyle);
            }

            string? key = operation is DownloadOperation or UploadOperation or CopyOperation or DeleteOperation ? Key(Values.Get(p, "key"), "key") : null;
            string? folder = operation switch
            {
                CreateFolderOperation or DeleteFolderOperation => Folder(Values.Get(p, "folder") ?? throw new InvalidOperationException("Give the folder - its path, never empty.")),
                FoldersOperation => Values.Get(p, "folder") is { } within ? Folder(within) : "",
                _ => null,
            };
            var prefix = Values.Get(p, "prefix") is { } given ? Prefix(given, "prefix") : null;
            var delimiter = Values.Get(p, "delimiter") is { } split ? split.Length <= 32 && !split.Any(char.IsControl) ? split : throw new InvalidOperationException($"'{split}' is not a delimiter - a few characters, /.") : null;
            var startAfter = Values.Get(p, "start_after") is { } after ? Prefix(after, "start_after") : null;
            var file = Values.Get(p, "file");
            (string Bucket, string Key) source = ("", "");
            if (operation == CopyOperation)
            {
                var from = Values.Get(p, "source") ?? throw new InvalidOperationException("Give the source - bucket/key.");
                var slash = from.TrimStart('/').IndexOf('/');
                var trimmed = from.TrimStart('/');
                source = slash > 0 ? (Bucket(trimmed[..slash], pathStyle: true), Key(trimmed[(slash + 1)..], "source")) : throw new InvalidOperationException($"'{from}' is not bucket/key (source).");
            }

            Upload? upload = null;
            if (operation == UploadOperation)
            {
                upload = Upload.Of(p, key!, file);
            }

            if (operation == DownloadOperation && file is null)
            {
                throw new InvalidOperationException("Give the file - where in the site's pipeline folder the object is written.");
            }

            if ((operation == DownloadOperation || (operation == UploadOperation && file is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            switch (operation)
            {
                case BucketsOperation:
                    {
                        var found = await session.XmlAsync(HttpMethod.Get, session.Url(null, null, null), "ListAllMyBucketsResult", cancellationToken);
                        foreach (var each in Elements(found, "Buckets", "Bucket").Take(max))
                        {
                            yield return Record(("name", Child(each, "Name")), ("created", Child(each, "CreationDate")));
                        }

                        break;
                    }

                case ObjectsOperation:
                case FoldersOperation:
                    {
                        var listing = operation == FoldersOperation ? new Listing(folder, "/", null) : new Listing(prefix, delimiter, startAfter);
                        await foreach (var entry in session.ListAsync(bucket!, listing, max, cancellationToken))
                        {
                            if (operation == FoldersOperation)
                            {
                                if (entry.Folder is { } path)
                                {
                                    yield return Record(("folder", path), ("name", path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..]));
                                }

                                continue;
                            }

                            yield return entry.Folder is { } common
                                ? Record(("prefix", common), ("folder", true))
                                : Record(("key", entry.Key), ("size", entry.Size), ("last_modified", entry.Modified), ("etag", entry.ETag), ("storage_class", entry.StorageClass));
                        }

                        break;
                    }

                case DownloadOperation:
                    {
                        var files = context.Files!;
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, session.Url(bucket, key, null)), Array.Empty<byte>(), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                        await session.EnsureAsync(response, cancellationToken);
                        if (response.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
                        {
                            throw new InvalidOperationException($"'{key}' is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                        }

                        long written;
                        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            written = await files.WriteAsync(file!, body, cancellationToken);
                        }

                        yield return Record(("bucket", bucket), ("key", key), ("file", file), ("size", written), ("content_type", response.Content.Headers.ContentType?.ToString()),
                            ("etag", Unquoted(response.Headers.ETag?.Tag)), ("last_modified", response.Content.Headers.LastModified?.ToString("o", CultureInfo.InvariantCulture)), ("version_id", Header(response, "x-amz-version-id")));
                        break;
                    }

                case UploadOperation:
                    {
                        var stored = await upload!.SendAsync(session, bucket!, context.Files, cancellationToken);
                        yield return Record(("bucket", bucket), ("key", key), ("size", stored.Size), ("etag", stored.ETag), ("version_id", stored.VersionId), ("file", file));
                        break;
                    }

                case CopyOperation:
                    {
                        var copySource = source.Bucket + "/" + EncodedKey(source.Key);
                        var copied = await session.XmlAsync(HttpMethod.Put, session.Url(bucket, key, null), "CopyObjectResult", cancellationToken, request => request.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource));
                        yield return Record(("bucket", bucket), ("key", key), ("source", source.Bucket + "/" + source.Key), ("etag", Unquoted(Child(copied, "ETag"))), ("last_modified", Child(copied, "LastModified")));
                        break;
                    }

                case DeleteOperation:
                    {
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, session.Url(bucket, key, null)), Array.Empty<byte>(), cancellationToken);
                        await session.EnsureAsync(response, cancellationToken);
                        yield return Record(("bucket", bucket), ("key", key), ("deleted", true), ("version_id", Header(response, "x-amz-version-id")));
                        break;
                    }

                case CreateFolderOperation:
                    {
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, session.Url(bucket, folder, null)) { Content = new ByteArrayContent(Array.Empty<byte>()) }, Array.Empty<byte>(), cancellationToken);
                        await session.EnsureAsync(response, cancellationToken);
                        yield return Record(("bucket", bucket), ("folder", folder), ("created", true));
                        break;
                    }

                case DeleteFolderOperation:
                    {
                        var deleted = await session.DeleteUnderAsync(bucket!, folder!, cancellationToken);
                        yield return Record(("bucket", bucket), ("folder", folder), ("deleted", deleted));
                        break;
                    }

                case CreateBucketOperation:
                    {
                        // us-east-1 is S3's default and takes no location; every other region is named.
                        var body = session.Region == "us-east-1" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(new XElement(XName.Get("CreateBucketConfiguration", Namespace), new XElement(XName.Get("LocationConstraint", Namespace), session.Region)).ToString(SaveOptions.DisableFormatting));
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, session.Url(bucket, null, null)) { Content = new ByteArrayContent(body) }, body, cancellationToken);
                        await session.EnsureAsync(response, cancellationToken);
                        yield return Record(("bucket", bucket), ("created", true), ("region", session.Region), ("location", response.Headers.Location?.ToString()));
                        break;
                    }

                case DeleteBucketOperation:
                    {
                        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, session.Url(bucket, null, null)), Array.Empty<byte>(), cancellationToken);
                        await session.EnsureAsync(response, cancellationToken);
                        yield return Record(("bucket", bucket), ("deleted", true));
                        break;
                    }
            }
        }

        /// <summary>A bucket's name held to S3's rules; with a dot only when addressed by path (a dotted host fails TLS).</summary>
        internal static string Bucket(string name, bool pathStyle)
        {
            if (!BucketName.IsMatch(name) || name.Contains("..", StringComparison.Ordinal) || name.Contains(".-", StringComparison.Ordinal) || name.Contains("-.", StringComparison.Ordinal) || IPAddress.TryParse(name, out _))
            {
                throw new InvalidOperationException($"'{name}' is not a bucket's name - lower-case letters, digits, dots and hyphens, 3 to 63, starting and ending with a letter or a digit.");
            }

            if (!pathStyle && name.Contains('.'))
            {
                throw new InvalidOperationException($"The bucket '{name}' has a dot in its name - address buckets by path in the connection (its certificate does not cover a dotted name).");
            }

            return name;
        }

        /// <summary>An object's key held to what travels safely: no leading slash, no '.' or '..' segment, no control character, at most 1,024 bytes.</summary>
        internal static string Key(string? key, string what)
        {
            if (key is null)
            {
                throw new InvalidOperationException($"Give the {what} - an object's key.");
            }

            if (key.StartsWith('/'))
            {
                throw new InvalidOperationException($"'{key}' starts with a slash; a key does not ({what}).");
            }

            return Prefix(key, what);
        }

        // A key or the start of one: no control character, no '.' or '..' segment (an address normalised on the way would name
        // another object), at most S3's 1,024 bytes.
        private static string Prefix(string value, string what)
        {
            if (value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"'{value}' holds a control character ({what}).");
            }

            if (value.Split('/').Any(segment => segment is "." or ".."))
            {
                throw new InvalidOperationException($"'{value}' has a '.' or '..' segment, which an address on the way would read as another key ({what}).");
            }

            return Encoding.UTF8.GetByteCount(value) <= 1024 ? value : throw new InvalidOperationException($"'{value[..40]}...' is over S3's 1,024 bytes ({what}).");
        }

        // A folder's path as S3 keeps one: a key ending with '/', never empty.
        private static string Folder(string value)
        {
            var trimmed = value.Trim('/');
            return trimmed.Length > 0 ? Key(trimmed, "folder") + "/" : throw new InvalidOperationException("Give the folder - its path, never empty.");
        }

        internal static string EncodedKey(string key) => string.Join("/", key.Split('/').Select(AwsSigV4.Encode));

        private static ConnectorRecord Record(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static string? Unquoted(string? etag) => etag?.Trim('"');

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        // S3's XML has no document type: one is refused (an XmlException, read as "not XML"), never processed or resolved.
        private static XElement Parse(string text)
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader);
        }

        private static string? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

        private static IEnumerable<XElement> Elements(XElement root, params string[] path)
        {
            IEnumerable<XElement> current = new[] { root };
            foreach (var name in path)
            {
                current = current.SelectMany(element => element.Elements().Where(child => child.Name.LocalName == name));
            }

            return current;
        }

        // An upload's body: a file of the run's folder or text, with its type, class, access and metadata as headers.
        private sealed class Upload
        {
            private readonly string key;
            private readonly string? file;
            private readonly byte[]? text;
            private readonly string? contentType;
            private readonly string? storageClass;
            private readonly string? acl;
            private readonly IReadOnlyList<(string Name, string Value)> metadata;

            private Upload(string key, string? file, byte[]? text, string? contentType, string? storageClass, string? acl, IReadOnlyList<(string, string)> metadata)
            {
                this.key = key;
                this.file = file;
                this.text = text;
                this.contentType = contentType;
                this.storageClass = storageClass;
                this.acl = acl;
                this.metadata = metadata;
            }

            public static Upload Of(IReadOnlyDictionary<string, string> p, string key, string? file)
            {
                // The text as given - not trimmed: it is the object's content.
                var text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw : null;
                if (file is null && text is null)
                {
                    throw new InvalidOperationException("Give the file in the site's pipeline folder to store, or text.");
                }

                if (file is not null && text is not null)
                {
                    throw new InvalidOperationException("Give the file or text, not both.");
                }

                var type = Values.Get(p, "content_type");
                if (type is not null && !MediaTypeHeaderValue.TryParse(type, out _))
                {
                    throw new InvalidOperationException($"'{type}' is not a media type - application/pdf (content_type).");
                }

                var storage = Values.Get(p, "storage_class") is { } named
                    ? StorageClasses.FirstOrDefault(known => string.Equals(known, named, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{named}' is not a storage class - {string.Join(", ", StorageClasses)}.")
                    : null;
                var access = Values.Get(p, "acl") is { } canned
                    ? Acls.FirstOrDefault(known => string.Equals(known, canned, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{canned}' is not a canned ACL - {string.Join(", ", Acls)}.")
                    : null;
                var meta = new List<(string, string)>();
                if (Values.Get(p, "metadata") is { } json)
                {
                    JsonObject given;
                    try
                    {
                        given = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException($"'{json}' is not a JSON object of text (metadata).");
                    }
                    catch (JsonException)
                    {
                        throw new InvalidOperationException($"'{json}' is not a JSON object of text (metadata).");
                    }

                    foreach (var (name, value) in given)
                    {
                        var said = value is JsonValue scalar ? scalar.ToString() : throw new InvalidOperationException($"Metadata '{name}' is not text.");
                        if (!MetadataName.IsMatch(name) || said.Any(c => c is < ' ' or > '~'))
                        {
                            throw new InvalidOperationException($"Metadata '{name}' - a name of letters, digits and hyphens, a value of plain ASCII text.");
                        }

                        meta.Add((name.ToLowerInvariant(), said));
                    }
                }

                return new Upload(key, file, text is null ? null : Encoding.UTF8.GetBytes(text), type, storage, access, meta);
            }

            public async Task<(long Size, string? ETag, string? VersionId)> SendAsync(Session session, string bucket, IConnectorFiles? files, CancellationToken cancellationToken)
            {
                var type = contentType
                    ?? (text is not null ? "text/plain; charset=utf-8" : new FileExtensionContentTypeProvider().TryGetContentType(key, out var byKey) ? byKey : "application/octet-stream");
                if (text is not null)
                {
                    using var sent = await session.SendAsync(() => Request(new ByteArrayContent(text), text.LongLength, type, session, bucket), text, cancellationToken);
                    await session.EnsureAsync(sent, cancellationToken);
                    return (text.LongLength, Unquoted(sent.Headers.ETag?.Tag), Header(sent, "x-amz-version-id"));
                }

                await using var opened = await files!.OpenReadAsync(file!, cancellationToken);
                // Hashed first, so S3 checks what arrives against what was read; the same bytes then sent from the start, per attempt.
                var hash = AwsSigV4.Hex(await SHA256.HashDataAsync(opened.Content, cancellationToken));
                using var response = await session.SendAsync(() =>
                {
                    opened.Content.Position = 0;
                    return Request(new StreamContent(new LentStream(opened.Content)), opened.Length, type, session, bucket);
                }, hash, cancellationToken);
                await session.EnsureAsync(response, cancellationToken);
                return (opened.Length, Unquoted(response.Headers.ETag?.Tag), Header(response, "x-amz-version-id"));
            }

            private HttpRequestMessage Request(HttpContent content, long length, string type, Session session, string bucket)
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                content.Headers.ContentLength = length;
                var request = new HttpRequestMessage(HttpMethod.Put, session.Url(bucket, key, null)) { Content = content };
                if (storageClass is not null)
                {
                    request.Headers.TryAddWithoutValidation("x-amz-storage-class", storageClass);
                }

                if (acl is not null)
                {
                    request.Headers.TryAddWithoutValidation("x-amz-acl", acl);
                }

                foreach (var (name, value) in metadata)
                {
                    request.Headers.TryAddWithoutValidation("x-amz-meta-" + name, value);
                }

                return request;
            }
        }

        private sealed record Listing(string? Prefix, string? Delimiter, string? StartAfter);

        private sealed record Entry(string? Key, long? Size, string? Modified, string? ETag, string? StorageClass, string? Folder);

        // One connection's calls: the store's address, buckets by host or by path, every request signed with its payload's hash;
        // a failure thrown with its status, S3's error code and its message.
        private sealed class Session
        {
            private readonly Uri api;
            private readonly string accessKey;
            private readonly string secretKey;
            private readonly TimeProvider time;

            private Session(Uri api, string region, bool pathStyle, string accessKey, string secretKey, TimeProvider time, HttpClient client)
            {
                this.api = api;
                Region = region;
                PathStyle = pathStyle;
                this.accessKey = accessKey;
                this.secretKey = secretKey;
                this.time = time;
                Client = client;
            }

            public string Region { get; }

            public bool PathStyle { get; }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
            {
                var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the region - eu-north-1 (us-east-1 for most S3-compatible stores).");
                if (!AwsS3Connector.Region.IsMatch(region))
                {
                    throw new InvalidOperationException($"'{region}' is not a region - eu-north-1.");
                }

                var api = new Uri(Values.BaseUrl(endpoint, $"https://s3.{region}.amazonaws.com"));
                if (api.AbsolutePath != "/" || api.Query.Length > 0 || api.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not a store's address - its host alone, https://s3.eu-north-1.amazonaws.com.");
                }

                var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Amazon S3 connection has no access key.");
                var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Amazon S3 connection has no secret key.");
                return new Session(api, region, Values.GetBool(settings, "path_style", false), accessKey, secretKey, time, egress.CreateHttpClient(timeout));
            }

            /// <summary>The address of the store, a bucket or an object - the bucket by host or by path, the key encoded segment by segment.</summary>
            public Uri Url(string? bucket, string? key, string? query)
            {
                var root = api.GetLeftPart(UriPartial.Authority);
                if (bucket is not null && !PathStyle)
                {
                    root = $"{api.Scheme}://{bucket}.{api.Authority}";
                }

                var path = "/" + (bucket is not null && PathStyle ? bucket + (key is null ? "" : "/") : "") + (key is null ? "" : EncodedKey(key));
                return new Uri(root + path + (string.IsNullOrEmpty(query) ? "" : "?" + query));
            }

            /// <summary>A signed request through the retry policy - built afresh per attempt - its payload hashed from <paramref name="payload"/>.</summary>
            public Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, byte[] payload, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead) =>
                SendAsync(build, AwsSigV4.Hex(SHA256.HashData(payload)), cancellationToken, completion);

            public Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, string payloadHash, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead) =>
                RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = build();
                    AwsSigV4.Sign(request, payloadHash, Region, "s3", accessKey, secretKey, null, time.GetUtcNow(), signPayloadHeader: true);
                    return request;
                }, cancellationToken, completion);

            /// <summary>A failed answer thrown with its status, S3's code and message - a bucket in another region named.</summary>
            public async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
            {
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var text = response.Content is null ? "" : await response.Content.ReadAsStringAsync(cancellationToken);
                XElement? error = null;
                try
                {
                    error = string.IsNullOrWhiteSpace(text) ? null : Parse(text);
                }
                catch (XmlException)
                {
                }

                var code = Child(error, "Code");
                var said = Child(error, "Message");
                if (Header(response, "x-amz-bucket-region") is { } elsewhere && !string.Equals(elsewhere, Region, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: the bucket is in {elsewhere}, not {Region} - set the connection's region to {elsewhere}.");
                }

                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            /// <summary>A request whose answer is S3's XML with <paramref name="root"/> at its top; anything else a failure that says so.</summary>
            public async Task<XElement> XmlAsync(HttpMethod method, Uri url, string root, CancellationToken cancellationToken, Action<HttpRequestMessage>? headers = null, byte[]? body = null)
            {
                var payload = body ?? Array.Empty<byte>();
                using var response = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(method, url);
                    if (body is not null || method == HttpMethod.Put || method == HttpMethod.Post)
                    {
                        request.Content = new ByteArrayContent(payload);
                    }

                    headers?.Invoke(request);
                    return request;
                }, payload, cancellationToken);
                await EnsureAsync(response, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                XElement answer;
                try
                {
                    answer = Parse(text);
                }
                catch (XmlException)
                {
                    throw new InvalidOperationException($"Amazon S3 answered HTTP {(int)response.StatusCode} with what is not XML - check the address: a sign-in page or a proxy answers so.");
                }

                // A copy's failure can arrive inside a 200; any other top is not S3's answer to this request.
                if (answer.Name.LocalName == "Error")
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {Child(answer, "Code")}: {Child(answer, "Message")}");
                }

                return answer.Name.LocalName == root ? answer : throw new InvalidOperationException($"Amazon S3 answered HTTP {(int)response.StatusCode} with what is not its answer ({answer.Name.LocalName}, not {root}) - check the address.");
            }

            /// <summary>A bucket's keys and folders page by page (ListObjectsV2), up to <paramref name="max"/>.</summary>
            public async IAsyncEnumerable<Entry> ListAsync(string bucket, Listing listing, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                string? token = null;
                var read = 0;
                while (read < max)
                {
                    var query = new List<string> { "list-type=2", "max-keys=" + Math.Min(1000, max - read).ToString(CultureInfo.InvariantCulture) };
                    if (listing.Prefix is not null)
                    {
                        query.Add("prefix=" + AwsSigV4.Encode(listing.Prefix));
                    }

                    if (listing.Delimiter is not null)
                    {
                        query.Add("delimiter=" + AwsSigV4.Encode(listing.Delimiter));
                    }

                    if (token is not null)
                    {
                        query.Add("continuation-token=" + AwsSigV4.Encode(token));
                    }
                    else if (listing.StartAfter is not null)
                    {
                        query.Add("start-after=" + AwsSigV4.Encode(listing.StartAfter));
                    }

                    var page = await XmlAsync(HttpMethod.Get, Url(bucket, null, string.Join("&", query)), "ListBucketResult", cancellationToken);
                    foreach (var content in page.Elements().Where(child => child.Name.LocalName is "Contents" or "CommonPrefixes"))
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        read++;
                        yield return content.Name.LocalName == "CommonPrefixes"
                            ? new Entry(null, null, null, null, null, Child(content, "Prefix"))
                            : new Entry(Child(content, "Key"), long.TryParse(Child(content, "Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null,
                                Child(content, "LastModified"), Unquoted(Child(content, "ETag")), Child(content, "StorageClass"), null);
                    }

                    if (!string.Equals(Child(page, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        yield break;
                    }

                    token = Child(page, "NextContinuationToken") ?? throw new InvalidOperationException("Amazon S3 said there was more but named no next page.");
                }
            }

            /// <summary>Every key under <paramref name="folder"/> deleted, a thousand a request (DeleteObjects); how many.</summary>
            public async Task<int> DeleteUnderAsync(string bucket, string folder, CancellationToken cancellationToken)
            {
                var deleted = 0;
                string? lastFirst = null;
                while (true)
                {
                    // Read afresh each round: what was deleted is gone from the listing, so the first page is always what is left.
                    var keys = new List<string>();
                    await foreach (var entry in ListAsync(bucket, new Listing(folder, null, null), 1000, cancellationToken))
                    {
                        keys.Add(entry.Key!);
                    }

                    if (keys.Count == 0)
                    {
                        return deleted;
                    }

                    // A store that says it deleted a key and still lists it would loop here for ever.
                    if (keys[0] == lastFirst)
                    {
                        throw new InvalidOperationException($"Amazon S3 still lists '{lastFirst}' after deleting it; {deleted} deleted before it.");
                    }

                    lastFirst = keys[0];

                    var body = Encoding.UTF8.GetBytes(new XElement(XName.Get("Delete", Namespace),
                        new XElement(XName.Get("Quiet", Namespace), "true"),
                        keys.Select(key => new XElement(XName.Get("Object", Namespace), new XElement(XName.Get("Key", Namespace), key)))).ToString(SaveOptions.DisableFormatting));
                    var md5 = Convert.ToBase64String(MD5.HashData(body)); // DeleteObjects requires it
                    var result = await XmlAsync(HttpMethod.Post, Url(bucket, null, "delete"), "DeleteResult", cancellationToken, request => request.Content!.Headers.TryAddWithoutValidation("Content-MD5", md5), body);
                    if (result.Elements().FirstOrDefault(child => child.Name.LocalName == "Error") is { } refused)
                    {
                        throw new InvalidOperationException($"Amazon S3 refused to delete '{Child(refused, "Key")}' ({Child(refused, "Code")}: {Child(refused, "Message")}); {deleted} deleted before it.");
                    }

                    deleted += keys.Count;
                }
            }
        }
    }
}
