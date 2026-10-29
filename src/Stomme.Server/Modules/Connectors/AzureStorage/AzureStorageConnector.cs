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

namespace Stomme.Modules.Connectors.AzureStorage
{
    /// <summary>
    /// Azure Storage (n8n's Azure Storage node: containers and blobs) over the Blob service's REST API, signed with the
    /// storage account's key (Shared Key, as n8n's default credential) - the account's own address,
    /// <c>https://{account}.blob.core.windows.net</c>, or another cloud's or an emulator's. A download is written into the
    /// pipeline run's folder and an upload read from it (<see cref="IConnectorFiles"/>). A test lists one container.
    /// </summary>
    /// <remarks>
    /// <para><c>containers</c> (by a prefix, their metadata too when asked, page by page up to <c>max</c>), <c>container_get</c>,
    /// <c>container_create</c> (private, or blobs or the whole container public; its metadata), <c>container_delete</c>,
    /// <c>blobs</c> (a container's, by a prefix - a folder's - and what n8n's node includes), <c>get</c> (a blob's properties and
    /// metadata), <c>download</c>, <c>upload</c> (a file of the run's folder, text - over several lines from the step's Query box -
    /// or a URL Azure copies from; its content type, access tier, metadata and tags; replacing as n8n's node does, or refused
    /// when it exists), <c>copy</c> (a blob to another, Azure copying it) and <c>delete</c> (its snapshots too when asked).</para>
    /// <para>Every request is signed as Azure's Shared Key asks - its method, its content headers, its <c>x-ms-</c> headers and
    /// the resource it names, the account's key the HMAC's - and carries <c>x-ms-version</c> 2025-01-05; the key is never sent.
    /// A container's name is held to Azure's rules and a blob's segments are escaped, each, before anything is sent. Azure's
    /// refusals said with their status, its error code and its message.</para>
    /// </remarks>
    public sealed class AzureStorageConnector : IConnector, IConnectorExtractor
    {
        public const string ApiVersion = "2025-01-05";

        public const string ContainersOperation = "containers";

        public const string ContainerGetOperation = "container_get";

        public const string ContainerCreateOperation = "container_create";

        public const string ContainerDeleteOperation = "container_delete";

        public const string BlobsOperation = "blobs";

        public const string GetOperation = "get";

        public const string DownloadOperation = "download";

        public const string UploadOperation = "upload";

        public const string CopyOperation = "copy";

        public const string DeleteOperation = "delete";

        public const int DefaultMax = 50;

        /// <summary>The most Azure lists in one page.</summary>
        public const int PageSize = 5000;

        private static readonly string[] Known =
        {
            ContainersOperation, ContainerGetOperation, ContainerCreateOperation, ContainerDeleteOperation, BlobsOperation, GetOperation, DownloadOperation, UploadOperation, CopyOperation, DeleteOperation,
        };

        private static readonly Regex ContainerName = new("^(?=.{3,63}$)[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9]))*$", RegexOptions.CultureInvariant);

        private static readonly Regex AccountName = new("^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant);

        private static readonly Regex MetadataName = new("^[A-Za-z_][A-Za-z0-9_]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly string[] BlobIncludes = { "copy", "deleted", "metadata", "snapshots", "tags", "uncommittedblobs", "versions", "immutabilitypolicy", "legalhold", "deletedwithversions", "permissions" };

        private static readonly string[] Tiers = { "Hot", "Cool", "Cold", "Archive" };

        public string ConnectorType => "azurestorage";

        public string DisplayName => "Azure Storage";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://kafe.blob.core.windows.net";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://azure.microsoft.com/products/storage/blobs");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/rest/api/storageservices/blob-service-rest-api");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-key"] = "Account key (the storage account > Security + networking > Access keys)",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("account", "Storage account", ConnectorConfigFieldType.Text, Required: false, Description: "The storage account's name - kafe. Empty: read from the address - https://kafe.blob.core.windows.net, or an emulator's http://127.0.0.1:10000/kafe."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField Flag(string name, string label, string description, bool fallback) => Field(name, label, description, type: ConnectorConfigFieldType.Boolean) with { Default = fallback ? "true" : "false" };

        private static ConnectorConfigField Max() => Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField ContainerField = Field("container", "Container", "Its name - kvitton.", required: true);

        private static readonly ConnectorConfigField BlobField = Field("blob", "Blob", "Its name in the container - 2026/1042.pdf.", required: true);

        private static readonly ConnectorConfigField MetadataField = Field("metadata", "Metadata", "A JSON object of names and values - {\"kund\": \"ada\"}.", type: ConnectorConfigFieldType.Multiline);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContainersOperation, "Get containers", "The account's containers - n8n's container Get Many.")
            {
                Parameters = Schema(Field("prefix", "Starting with", "Only those whose names start so - kassa."), Flag("metadata", "Metadata too", "Each container's metadata as well.", fallback: false), Max()),
            },
            new ConnectorOperation(ContainerGetOperation, "Get a container", "Its properties and metadata - n8n's container Get.") { Parameters = Schema(ContainerField) },
            new ConnectorOperation(ContainerCreateOperation, "Create a container", "A container, private or public - n8n's container Create.")
            {
                Parameters = Schema(ContainerField, Field("access", "Public access", "private (the default), blob - each blob readable by its address - or container - its list too."), MetadataField),
            },
            new ConnectorOperation(ContainerDeleteOperation, "Delete a container", "A container and every blob in it - n8n's container Delete.") { Parameters = Schema(ContainerField) },
            new ConnectorOperation(BlobsOperation, "Get blobs", "A container's blobs - n8n's blob Get Many.")
            {
                Parameters = Schema(
                    ContainerField,
                    Field("prefix", "Starting with", "Only those whose names start so - 2026/ for a folder."),
                    Field("include", "Include", "Comma-separated: metadata, tags, snapshots, versions, deleted, copy, uncommittedblobs..."),
                    Max()),
            },
            new ConnectorOperation(GetOperation, "Get a blob's properties", "Its size, type, dates, tier and metadata - n8n's blob Get, without its bytes.") { Parameters = Schema(ContainerField, BlobField) },
            new ConnectorOperation(DownloadOperation, "Download a blob", "A blob written into the run's pipeline folder - n8n's blob Get.")
            {
                Parameters = Schema(ContainerField, BlobField, Field("file", "File", "Where in the site's pipeline folder it is written - downloads/1042.pdf; ending with / its own name in that folder.", required: true)),
            },
            new ConnectorOperation(UploadOperation, "Upload a blob", "A file of the run's pipeline folder, text, or what a URL holds, stored as a blob - n8n's blob Create.")
            {
                Parameters = Schema(
                    ContainerField,
                    Field("blob", "Blob", "Its name in the container - 2026/1042.pdf; ending with / the file's own name in that folder.", required: true),
                    Field("file", "File", "The file in the site's pipeline folder to store - reports/june.pdf. Or give text or a URL."),
                    Field("text", "Text", "Text to store, as UTF-8 - over several lines in the step's Query box."),
                    Field("url", "From a URL", "An address Azure copies the blob from - one it can read."),
                    Field("content_type", "Content type", "Its type - application/pdf. Empty: a file's by its name, text's text/plain."),
                    Field("tier", "Access tier", "Hot, Cool, Cold or Archive. Empty: the account's default."),
                    MetadataField,
                    Field("tags", "Tags", "A JSON object of tags - {\"projekt\": \"kassa\"}.", type: ConnectorConfigFieldType.Multiline),
                    Flag("overwrite", "Replace what is there", "Replace a blob already of that name, as n8n's node does; refuse otherwise.", fallback: true)),
            },
            new ConnectorOperation(CopyOperation, "Copy a blob", "A blob of the account copied to another name or container, by Azure.")
            {
                Parameters = Schema(ContainerField, BlobField, Field("to_container", "To container", "Where it is copied - empty: the same container."), Field("to_blob", "To blob", "Its new name.", required: true)),
            },
            new ConnectorOperation(DeleteOperation, "Delete a blob", "A blob - n8n's blob Delete.")
            {
                Parameters = Schema(ContainerField, BlobField, Flag("snapshots", "Its snapshots too", "Delete the blob's snapshots with it (Azure refuses a blob that has some otherwise).", fallback: false)),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var page = await session.ListAsync(string.Empty, new() { ["comp"] = "list", ["maxresults"] = "1" }, cancellationToken);
                var any = page.Root?.Element("Containers")?.Elements("Container").Any() == true;
                return ConnectionTestResult.Ok($"Azure Storage: the account {session.Account} answers{(any ? string.Empty : ", no container yet")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? ContainersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Azure Storage has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var container = operation == ContainersOperation ? string.Empty : ContainerOf(Values.Get(p, "container"), "container");
            var blob = string.Empty;
            string? target = null, text = null, url = null, toContainer = null, toBlob = null;
            var headers = new List<(string Name, string Value)>();
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var overwrite = Bool(p, "overwrite", true);
            switch (operation)
            {
                case ContainersOperation:
                    query["comp"] = "list";
                    if (Values.Get(p, "prefix") is { } prefix)
                    {
                        query["prefix"] = OneLine(prefix, "prefix");
                    }

                    if (Bool(p, "metadata", false))
                    {
                        query["include"] = "metadata";
                    }

                    break;
                case ContainerCreateOperation:
                    switch ((Values.Get(p, "access") ?? "private").ToLowerInvariant())
                    {
                        case "private":
                            break;
                        case var access and ("blob" or "container"):
                            headers.Add(("x-ms-blob-public-access", access));
                            break;
                        default:
                            throw new InvalidOperationException($"'{Values.Get(p, "access")}' is not private, blob or container (access).");
                    }

                    headers.AddRange(Metadata(Values.Get(p, "metadata")));
                    break;
                case BlobsOperation:
                    query["restype"] = "container";
                    query["comp"] = "list";
                    if (Values.Get(p, "prefix") is { } under)
                    {
                        query["prefix"] = OneLine(under, "prefix");
                    }

                    if (List(p, "include") is { Count: > 0 } include)
                    {
                        var unknown = include.FirstOrDefault(item => !BlobIncludes.Contains(item, StringComparer.OrdinalIgnoreCase));
                        query["include"] = unknown is null
                            ? string.Join(",", include.Select(item => item.ToLowerInvariant()))
                            : throw new InvalidOperationException($"'{unknown}' is not one of {string.Join(", ", BlobIncludes)} (include).");
                    }

                    break;
                case GetOperation or DeleteOperation:
                    blob = BlobOf(Values.Get(p, "blob"), "blob");
                    if (operation == DeleteOperation && Bool(p, "snapshots", false))
                    {
                        headers.Add(("x-ms-delete-snapshots", "include"));
                    }

                    break;
                case DownloadOperation:
                    blob = BlobOf(Values.Get(p, "blob"), "blob");
                    target = Values.Get(p, "file") ?? throw new InvalidOperationException("Give the file - where in the site's pipeline folder it is written.");
                    break;
                case UploadOperation:
                    {
                        target = Values.Get(p, "file");
                        text = p.TryGetValue("text", out var raw) && !string.IsNullOrEmpty(raw) ? raw // the content as given, not trimmed
                            : p.TryGetValue("query", out var box) && !string.IsNullOrEmpty(box) ? box : null;
                        url = Values.Get(p, "url");
                        var given = new[] { target, text, url }.Count(source => source is not null);
                        if (given != 1)
                        {
                            throw new InvalidOperationException(given == 0 ? "Give the file in the site's pipeline folder to store, text or a URL." : "Give one of the file, the text and the URL.");
                        }

                        if (url is not null && (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var from) || from.Scheme is not ("https" or "http")))
                        {
                            throw new InvalidOperationException($"'{url}' is not an http(s) address (url).");
                        }

                        var name = Values.Get(p, "blob") ?? throw new InvalidOperationException("Give the blob's name - 2026/1042.pdf.");
                        if (name.EndsWith('/'))
                        {
                            name += target is null ? throw new InvalidOperationException("Give the blob's name with its own - text and a URL have none here.") : Path.GetFileName(target.Replace('\\', '/'));
                        }

                        blob = BlobOf(name, "blob");
                        headers.Add(("x-ms-blob-type", "BlockBlob"));
                        if (Values.Get(p, "content_type") is { } type)
                        {
                            headers.Add(("x-ms-blob-content-type", MediaTypeHeaderValue.TryParse(type, out _) ? type : throw new InvalidOperationException($"'{type}' is not a content type - application/pdf (content_type).")));
                        }

                        if (Values.Get(p, "tier") is { } tier)
                        {
                            headers.Add(("x-ms-access-tier", Tiers.FirstOrDefault(known => string.Equals(known, tier, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{tier}' is not Hot, Cool, Cold or Archive (tier).")));
                        }

                        headers.AddRange(Metadata(Values.Get(p, "metadata")));
                        if (Values.Get(p, "tags") is { } tags)
                        {
                            headers.Add(("x-ms-tags", string.Join("&", Pairs(tags, "tags").Select(tag => Uri.EscapeDataString(tag.Name) + "=" + Uri.EscapeDataString(tag.Value)))));
                        }

                        if (!overwrite)
                        {
                            headers.Add(("If-None-Match", "*"));
                        }

                        break;
                    }

                case CopyOperation:
                    blob = BlobOf(Values.Get(p, "blob"), "blob");
                    toContainer = Values.Get(p, "to_container") is { } other ? ContainerOf(other, "to_container") : container;
                    toBlob = BlobOf(Values.Get(p, "to_blob"), "to_blob");
                    break;
            }

            if ((operation == DownloadOperation || (operation == UploadOperation && target is not null)) && context.Files is null)
            {
                throw new InvalidOperationException("A download or a file's upload reads and writes the run's pipeline folder - run it from a pipeline.");
            }

            using var session = Session.Open(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case ContainersOperation:
                    await foreach (var item in session.PagesAsync(string.Empty, query, "Containers", "Container", max, cancellationToken))
                    {
                        yield return Listed(item);
                    }

                    break;

                case BlobsOperation:
                    await foreach (var item in session.PagesAsync("/" + container, query, "Blobs", "Blob", max, cancellationToken))
                    {
                        yield return Listed(item, ("container", container));
                    }

                    break;

                case ContainerGetOperation:
                    {
                        using var got = await session.SendAsync(HttpMethod.Get, "/" + container, new() { ["restype"] = "container" }, null, null, cancellationToken);
                        yield return Headers(got, ("name", container));
                        break;
                    }

                case ContainerCreateOperation:
                    {
                        using var made = await session.SendAsync(HttpMethod.Put, "/" + container, new() { ["restype"] = "container" }, headers, null, cancellationToken);
                        yield return Headers(made, ("name", container), ("created", true));
                        break;
                    }

                case ContainerDeleteOperation:
                    {
                        using var deleted = await session.SendAsync(HttpMethod.Delete, "/" + container, new() { ["restype"] = "container" }, null, null, cancellationToken);
                        yield return Record(("name", container), ("deleted", true));
                        break;
                    }

                case GetOperation:
                    {
                        using var got = await session.SendAsync(HttpMethod.Head, $"/{container}/{Escape(blob)}", new(), null, null, cancellationToken);
                        yield return Headers(got, ("container", container), ("name", blob));
                        break;
                    }

                case DownloadOperation:
                    yield return await session.DownloadAsync(container, blob, context.Files!, target!, cancellationToken);
                    break;

                case UploadOperation:
                    {
                        Func<HttpContent>? content = null;
                        ConnectorFileContent? opened = null;
                        try
                        {
                            long length = 0;
                            if (text is not null)
                            {
                                var bytes = Encoding.UTF8.GetBytes(text);
                                length = bytes.LongLength;
                                content = () => new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" } } };
                            }
                            else if (target is not null)
                            {
                                opened = await context.Files!.OpenReadAsync(target, cancellationToken);
                                var file = opened;
                                length = file.Length;
                                var type = new FileExtensionContentTypeProvider().TryGetContentType(file.Name, out var byName) ? byName : "application/octet-stream";
                                content = () =>
                                {
                                    file.Content.Position = 0;
                                    return new StreamContent(new LentStream(file.Content)) { Headers = { ContentType = new MediaTypeHeaderValue(type), ContentLength = file.Length } };
                                };
                            }
                            else
                            {
                                headers.Add(("x-ms-copy-source", url!));
                            }

                            using var stored = await session.SendAsync(HttpMethod.Put, $"/{container}/{Escape(blob)}", new(), headers, content, cancellationToken);
                            yield return Headers(stored, ("container", container), ("name", blob), ("size", url is null ? length : null));
                        }
                        finally
                        {
                            if (opened is not null)
                            {
                                await opened.DisposeAsync();
                            }
                        }

                        break;
                    }

                case CopyOperation:
                    {
                        var source = session.Url($"/{container}/{Escape(blob)}", new());
                        using var copied = await session.SendAsync(HttpMethod.Put, $"/{toContainer}/{Escape(toBlob!)}", new(), new[] { ("x-ms-copy-source", source) }, null, cancellationToken);
                        yield return Headers(copied, ("container", toContainer), ("name", toBlob), ("from", $"{container}/{blob}"));
                        break;
                    }

                case DeleteOperation:
                    {
                        using var deleted = await session.SendAsync(HttpMethod.Delete, $"/{container}/{Escape(blob)}", new(), headers, null, cancellationToken);
                        yield return Record(("container", container), ("name", blob), ("deleted", true));
                        break;
                    }
            }
        }

        /// <summary>A container's name as Azure takes it: 3 to 63 lower-case letters, digits and single hyphens, a letter or digit first and last.</summary>
        public static string ContainerOf(string? given, string what)
        {
            var value = given ?? throw new InvalidOperationException($"Give the container's name - kvitton ({what}).");
            return ContainerName.IsMatch(value) || value == "$root" ? value : throw new InvalidOperationException($"'{value}' is not a container's name - 3 to 63 lower-case letters, digits and single hyphens ({what}).");
        }

        /// <summary>A blob's name as Azure takes it: 1 to 1,024 characters, no control character; its slashes name its folders.</summary>
        public static string BlobOf(string? given, string what)
        {
            var value = given ?? throw new InvalidOperationException($"Give the blob's name - 2026/1042.pdf ({what}).");
            return value.Length is > 0 and <= 1024 && !value.Any(char.IsControl) && !value.StartsWith('/') && !value.EndsWith('/')
                ? value
                : throw new InvalidOperationException($"'{value}' is not a blob's name - 2026/1042.pdf, 1 to 1,024 characters ({what}).");
        }

        // A blob's name on the wire: each segment escaped, its slashes kept.
        private static string Escape(string blob) => string.Join("/", blob.Split('/').Select(Uri.EscapeDataString));

        private static IEnumerable<(string Name, string Value)> Metadata(string? json) =>
            json is null ? Enumerable.Empty<(string, string)>() : Pairs(json, "metadata").Select(pair => MetadataName.IsMatch(pair.Name) && !pair.Value.Any(c => c > 127 || char.IsControl(c))
                ? ("x-ms-meta-" + pair.Name, pair.Value)
                : throw new InvalidOperationException($"'{pair.Name}' = '{pair.Value}' is not metadata Azure takes - a name of letters, digits and _, a value of ASCII (metadata)."));

        private static List<(string Name, string Value)> Pairs(string json, string what)
        {
            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                parsed = null;
            }

            return (parsed ?? throw new InvalidOperationException($"The {what} is not a JSON object - {{\"kund\": \"ada\"}}."))
                .Select(pair => (pair.Key, pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString() ?? string.Empty))
                .ToList();
        }

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

        // A listed container or blob: its name, its properties by camel-cased name, its metadata (and tags) as JSON objects.
        private static ConnectorRecord Listed(XElement item, params (string Name, object? Value)[] added)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in added)
            {
                fields[name] = value;
            }

            fields["name"] = (string?)item.Element("Name");
            foreach (var property in item.Element("Properties")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                fields[Camel(property.Name.LocalName)] = Value(property.Name.LocalName, property.Value);
            }

            if (item.Element("Metadata") is { } metadata)
            {
                fields["metadata"] = new JsonObject(metadata.Elements().Select(entry => KeyValuePair.Create(entry.Name.LocalName, (JsonNode?)entry.Value))).ToJsonString();
            }

            if (item.Element("Tags")?.Element("TagSet") is { } set)
            {
                fields["tags"] = new JsonObject(set.Elements("Tag").Select(tag => KeyValuePair.Create((string?)tag.Element("Key") ?? string.Empty, (JsonNode?)(string?)tag.Element("Value")))).ToJsonString();
            }

            return new ConnectorRecord(fields);
        }

        // A response's headers as a record: Azure's properties without their x-ms- prefix, camel-cased; its metadata as a JSON object.
        private static ConnectorRecord Headers(HttpResponseMessage response, params (string Name, object? Value)[] added)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in added.Where(field => field.Value is not null))
            {
                fields[name] = value;
            }

            var metadata = new JsonObject();
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                var value = string.Join(",", values);
                if (name.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                {
                    metadata[name["x-ms-meta-".Length..]] = value;
                }
                else if (name is "ETag" or "Last-Modified" or "Content-Length" or "Content-Type" or "Content-MD5" or "Content-Encoding" or "Content-Language" or "Cache-Control"
                    || (name.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase) && name is not ("x-ms-request-id" or "x-ms-version" or "x-ms-client-request-id")))
                {
                    var bare = name.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase) ? name["x-ms-".Length..] : name;
                    fields.TryAdd(Camel(bare), Value(bare, value));
                }
            }

            if (metadata.Count > 0)
            {
                fields["metadata"] = metadata.ToJsonString();
            }

            return new ConnectorRecord(fields);
        }

        // Last-Modified -> lastModified, x-ms-blob-type's blob-type -> blobType, Content-MD5 -> contentMd5, a listing's BlobType ->
        // blobType: a header's and a listing's names alike; ETag and Etag both etag.
        private static string Camel(string name)
        {
            if (name.Equals("ETag", StringComparison.OrdinalIgnoreCase))
            {
                return "etag";
            }

            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 1
                ? char.ToLowerInvariant(name[0]) + name[1..]
                : string.Concat(parts.Select((part, index) => index == 0 ? part.ToLowerInvariant() : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        }

        // A date as ISO 8601, a length as a number, an ETag without its quotes, anything else as Azure wrote it.
        private static object? Value(string name, string value)
        {
            if ((name.EndsWith("Time", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Modified", StringComparison.OrdinalIgnoreCase) || name.EndsWith("time", StringComparison.Ordinal))
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                return when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                return length;
            }

            return name.Equals("ETag", StringComparison.OrdinalIgnoreCase) || name.Equals("Etag", StringComparison.Ordinal) ? value.Trim('"') : value;
        }

        /// <summary>One account: its Blob service's address, its name and its key - which signs each request and never travels.</summary>
        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly byte[] key;

            private Session(HttpClient client, string service, string account, byte[] key)
            {
                this.client = client;
                Service = service;
                Account = account;
                this.key = key;
            }

            public string Service { get; }

            public string Account { get; }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var named = Values.Get(settings, "account");
                if (named is not null && !AccountName.IsMatch(named))
                {
                    throw new InvalidOperationException($"'{named}' is not a storage account's name - 3 to 24 lower-case letters and digits (account).");
                }

                string service;
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    service = named is null ? throw new InvalidOperationException("Give the account's address - https://kafe.blob.core.windows.net - or its name.") : $"https://{named}.blob.core.windows.net";
                }
                else
                {
                    service = Values.BaseUrl(endpoint, string.Empty);
                    var uri = new Uri(service);
                    if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    {
                        throw new InvalidOperationException($"'{endpoint}' is not a Blob service's address - its own, https://kafe.blob.core.windows.net; the key is the connection's credential, never a SAS in the address.");
                    }
                }

                // The account: the setting's, the first label of a host that is an account's own (kafe.blob.core.windows.net), or the
                // one segment of an emulator's path-style address (http://127.0.0.1:10000/kafe - Azurite's, Azure Stack's).
                var address = new Uri(service);
                var path = address.AbsolutePath.Trim('/');
                var account = named
                    ?? (address.Host.Contains(".blob.", StringComparison.OrdinalIgnoreCase) ? address.Host[..address.Host.IndexOf('.')].ToLowerInvariant() : null)
                    ?? (AccountName.IsMatch(path) ? path : null)
                    ?? throw new InvalidOperationException($"Give the storage account's name - '{endpoint}' does not name it (account).");
                var secret = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Azure Storage connection has no account key.");
                byte[] key;
                try
                {
                    key = Convert.FromBase64String(secret);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("The account key is not one Azure gives - its base64 text, from the account's Access keys.");
                }

                return new Session(egress.CreateHttpClient(timeout), service, account, key);
            }

            public void Dispose()
            {
                client.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }

            public string Url(string path, SortedDictionary<string, string> query) =>
                Service + path + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))));

            /// <summary>A signed request: Azure's answer when it took it, or its refusal with its status, code and message.</summary>
            public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, SortedDictionary<string, string> query, IReadOnlyList<(string Name, string Value)>? headers, Func<HttpContent>? content, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
            {
                var url = Url(path, query);
                var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
                    request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
                    request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
                    foreach (var (name, value) in headers ?? Array.Empty<(string, string)>())
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    if (request.Content is null && (method == HttpMethod.Put || method == HttpMethod.Post))
                    {
                        request.Content = new ByteArrayContent(Array.Empty<byte>());
                    }

                    request.Headers.Authorization = new AuthenticationHeaderValue("SharedKey", Account + ":" + Sign(request, query));
                    return request;
                }, cancellationToken, completion);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                using (response)
                {
                    throw Failure(response, method == HttpMethod.Head ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken));
                }
            }

            /// <summary>A listing page by page - its marker carried on while Azure names a next one - each page's items, up to <paramref name="max"/>.</summary>
            public async IAsyncEnumerable<XElement> PagesAsync(string path, SortedDictionary<string, string> query, string list, string item, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var read = 0;
                while (read < max)
                {
                    query["maxresults"] = Math.Min(max - read, PageSize).ToString(CultureInfo.InvariantCulture);
                    var page = await ListAsync(path, query, cancellationToken);
                    foreach (var entry in page.Root?.Element(list)?.Elements(item) ?? Enumerable.Empty<XElement>())
                    {
                        if (read == max)
                        {
                            yield break;
                        }

                        read++;
                        yield return entry;
                    }

                    if ((string?)page.Root?.Element("NextMarker") is not { Length: > 0 } next)
                    {
                        yield break;
                    }

                    query["marker"] = next;
                }
            }

            public async Task<XDocument> ListAsync(string path, SortedDictionary<string, string> query, CancellationToken cancellationToken)
            {
                using var response = await SendAsync(HttpMethod.Get, path, query, null, null, cancellationToken);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    return XDocument.Load(reader);
                }
                catch (XmlException)
                {
                    throw new InvalidOperationException($"Azure Storage answered {(int)response.StatusCode} with what is not its XML - check the connection's address.");
                }
            }

            /// <summary>A blob's bytes written into the run's folder; its length checked against the folder's cap first.</summary>
            public async Task<ConnectorRecord> DownloadAsync(string container, string blob, IConnectorFiles files, string file, CancellationToken cancellationToken)
            {
                using var response = await SendAsync(HttpMethod.Get, $"/{container}/{Escape(blob)}", new(), null, null, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                var name = blob[(blob.LastIndexOf('/') + 1)..];
                if (response.Content.Headers.ContentLength is { } known && known > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{name}' is {known} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                var written = file.EndsWith('/') ? file + Safe(name) : file;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var length = await files.WriteAsync(written, stream, cancellationToken);
                return Headers(response, ("container", container), ("name", blob), ("file", written), ("size", length));
            }

            // Azure's Shared Key: the method, the standard headers in their order, the x-ms- headers sorted, the resource named.
            private string Sign(HttpRequestMessage request, SortedDictionary<string, string> query)
            {
                string Header(string name) => request.Content is not null && request.Content.Headers.TryGetValues(name, out var content) ? string.Join(",", content) : request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;
                var length = request.Content?.Headers.ContentLength is > 0 and var bytes ? bytes.ToString(CultureInfo.InvariantCulture) : string.Empty;
                var canonical = new StringBuilder()
                    .Append(request.Method.Method.ToUpperInvariant()).Append('\n')
                    .Append(Header("Content-Encoding")).Append('\n')
                    .Append(Header("Content-Language")).Append('\n')
                    .Append(length).Append('\n')
                    .Append(Header("Content-MD5")).Append('\n')
                    .Append(request.Content?.Headers.ContentType?.ToString() ?? string.Empty).Append('\n')
                    .Append(string.Empty).Append('\n') // Date: x-ms-date stands for it
                    .Append(Header("If-Modified-Since")).Append('\n')
                    .Append(Header("If-Match")).Append('\n')
                    .Append(Header("If-None-Match")).Append('\n')
                    .Append(Header("If-Unmodified-Since")).Append('\n')
                    .Append(Header("Range")).Append('\n');
                foreach (var (name, values) in request.Headers.Where(header => header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase)).OrderBy(header => header.Key.ToLowerInvariant(), StringComparer.Ordinal))
                {
                    canonical.Append(name.ToLowerInvariant()).Append(':').Append(string.Join(",", values).Trim()).Append('\n');
                }

                canonical.Append('/').Append(Account).Append(request.RequestUri!.AbsolutePath);
                foreach (var (name, value) in query.OrderBy(pair => pair.Key.ToLowerInvariant(), StringComparer.Ordinal))
                {
                    canonical.Append('\n').Append(name.ToLowerInvariant()).Append(':').Append(value);
                }

                return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical.ToString())));
            }

            // Azure's refusal: its status, its error code (a header, and the body's) and the first line of its message.
            private static InvalidOperationException Failure(HttpResponseMessage response, string payload)
            {
                var code = response.Headers.TryGetValues("x-ms-error-code", out var codes) ? codes.FirstOrDefault() : null;
                string? message = null;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var error = XDocument.Load(reader).Root;
                    code ??= (string?)error?.Element("Code");
                    message = ((string?)error?.Element("Message"))?.Split('\n', 2)[0].Trim();
                }
                catch (XmlException)
                {
                }

                var said = string.Join(": ", new[] { code, message }.Where(part => !string.IsNullOrWhiteSpace(part)));
                return new InvalidOperationException($"Azure Storage answered {(int)response.StatusCode}{(said.Length == 0 ? "." : " " + said)}");
            }

            // A blob's own name as a file of the folder: its unsafe characters replaced.
            private static string Safe(string name)
            {
                var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
                var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
                return safe.Length == 0 ? "file" : safe;
            }
        }
    }
}
