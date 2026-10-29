#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Ghost
{
    /// <summary>
    /// Ghost (n8n's Ghost node: a site's posts read, created, updated and deleted): a Ghost site's Admin API
    /// (<c>/ghost/api/admin/</c>), an Admin API key - its id and secret - signing a five-minute token for every call, over the
    /// host's egress. A test names how many posts the key sees.
    /// </summary>
    /// <remarks>
    /// <c>posts</c> and <c>pages</c> (a filter in Ghost's own language, an order, what to include, the formats and fields - page
    /// by page up to <c>max</c>), <c>get_post</c> and <c>get_page</c> (by id or slug), <c>create_post</c> and <c>create_page</c>
    /// (a title, HTML or Lexical, a status, any other field), <c>update_post</c> and <c>update_page</c> (Ghost's last change
    /// read first, as Ghost asks), <c>delete_post</c> and <c>delete_page</c>; <c>tags</c> and <c>members</c>. A post is its
    /// fields as Ghost gives them - a list or an object as its JSON. Every id, slug, filter and list held to its shape before
    /// anything is sent. A throttled call waits as Ghost says; a failure is thrown with its status and Ghost's word.
    /// </remarks>
    public sealed class GhostConnector : IConnector, IConnectorExtractor
    {
        public const string PostsOperation = "posts";

        public const string GetPostOperation = "get_post";

        public const string CreatePostOperation = "create_post";

        public const string UpdatePostOperation = "update_post";

        public const string DeletePostOperation = "delete_post";

        public const string PagesOperation = "pages";

        public const string GetPageOperation = "get_page";

        public const string CreatePageOperation = "create_page";

        public const string UpdatePageOperation = "update_page";

        public const string DeletePageOperation = "delete_page";

        public const string TagsOperation = "tags";

        public const string MembersOperation = "members";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex ObjectId = new("^[0-9a-f]{24}$", RegexOptions.CultureInvariant);

        private static readonly Regex Slug = new("^(?!\\.+$)[^\\s/?#%\\\\]{1,191}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z_.]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Secret = new("^[0-9a-fA-F]{32,128}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { PostsOperation, GetPostOperation, CreatePostOperation, UpdatePostOperation, DeletePostOperation, PagesOperation, GetPageOperation, CreatePageOperation, UpdatePageOperation, DeletePageOperation, TagsOperation, MembersOperation };

        private static readonly string[] Statuses = { "draft", "published", "scheduled" };

        private static readonly string[] Formats = { "html", "lexical", "mobiledoc", "plaintext" };

        public string ConnectorType => "ghost";

        public string DisplayName => "Ghost";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://example.ghost.io";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://ghost.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.ghost.org/admin-api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField MaxField() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField FilterField() => new("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "Ghost's filter - status:published+tag:news.");

        private static ConnectorConfigSchema Browse(string what) => new(new[]
        {
            FilterField(),
            new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "A field and asc or desc - published_at desc."),
            new ConnectorConfigField("include", "Include", ConnectorConfigFieldType.Text, Required: false, Description: $"Comma-separated - tags,authors for {what}."),
            new ConnectorConfigField("formats", "Formats", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - html, lexical, mobiledoc, plaintext."),
            new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - id,title,url; all when left empty."),
            MaxField(),
        });

        private static ConnectorConfigSchema Read(string what) => new(new[]
        {
            new ConnectorConfigField("id", "Id", ConnectorConfigFieldType.Text, Required: false, Description: $"The {what}'s id - or give its slug."),
            new ConnectorConfigField("slug", "Slug", ConnectorConfigFieldType.Text, Required: false, Description: $"The {what}'s slug - welcome."),
            new ConnectorConfigField("include", "Include", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - tags,authors."),
            new ConnectorConfigField("formats", "Formats", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - html, lexical, mobiledoc, plaintext."),
        });

        private static ConnectorConfigSchema Write(string what, bool update) => new(new[]
        {
            new ConnectorConfigField("id", "Id", ConnectorConfigFieldType.Text, Required: update, Description: $"The {what}'s id."),
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: !update, Description: $"The {what}'s title."),
            new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "Its content as HTML - Ghost turns it into its own."),
            new ConnectorConfigField("lexical", "Lexical", ConnectorConfigFieldType.Multiline, Required: false, Description: "Or its content in Ghost's Lexical JSON."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "draft (Ghost's default), published or scheduled."),
            new ConnectorConfigField("fields", "Other fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of any other field - {\"tags\": [{\"name\": \"News\"}], \"published_at\": \"2026-10-14T08:00:00Z\"}."),
        });

        private static ConnectorConfigSchema Remove(string what) => new(new[] { new ConnectorConfigField("id", "Id", ConnectorConfigFieldType.Text, Required: true, Description: $"The {what}'s id.") });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PostsOperation, "Get posts", "The site's posts, drafts too - n8n's Ghost Get Many.") { Parameters = Browse("posts") },
            new ConnectorOperation(GetPostOperation, "Get a post", "A post by its id or slug - n8n's Ghost Get.") { Parameters = Read("post") },
            new ConnectorOperation(CreatePostOperation, "Create a post", "n8n's Ghost Create.") { Parameters = Write("post", update: false) },
            new ConnectorOperation(UpdatePostOperation, "Update a post", "Changes the fields given - n8n's Ghost Update.") { Parameters = Write("post", update: true) },
            new ConnectorOperation(DeletePostOperation, "Delete a post", "n8n's Ghost Delete.") { Parameters = Remove("post") },
            new ConnectorOperation(PagesOperation, "Get pages", "The site's pages, drafts too.") { Parameters = Browse("pages") },
            new ConnectorOperation(GetPageOperation, "Get a page", "A page by its id or slug.") { Parameters = Read("page") },
            new ConnectorOperation(CreatePageOperation, "Create a page", "A page - as a post.") { Parameters = Write("page", update: false) },
            new ConnectorOperation(UpdatePageOperation, "Update a page", "Changes the fields given.") { Parameters = Write("page", update: true) },
            new ConnectorOperation(DeletePageOperation, "Delete a page", "Deletes a page.") { Parameters = Remove("page") },
            new ConnectorOperation(TagsOperation, "Get tags", "The site's tags.") { Parameters = new(new[] { FilterField(), MaxField() }) },
            new ConnectorOperation(MembersOperation, "Get members", "The site's members - its newsletter's readers.") { Parameters = new(new[] { FilterField(), MaxField() }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var page = await session.SendAsync(HttpMethod.Get, "/posts/?limit=1&fields=id", null, cancellationToken);
                var total = page?["meta"]?["pagination"]?["total"] is JsonValue count && count.TryGetValue<long>(out var posts) ? posts : 0;
                return ConnectionTestResult.Ok($"Ghost: {(total == 1 ? "one post" : $"{total} posts")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? PostsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Ghost has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var kind = operation switch
            {
                TagsOperation => "tags",
                MembersOperation => "members",
                _ when operation.EndsWith("page", StringComparison.Ordinal) || operation == PagesOperation => "pages",
                _ => "posts",
            };
            var singular = kind == "pages" ? "page" : "post";

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = new List<string>();
            var browse = operation is PostsOperation or PagesOperation or TagsOperation or MembersOperation;
            if (browse && Values.Get(p, "filter") is { } filter)
            {
                query.Add("filter=" + Uri.EscapeDataString(filter.Length <= 2000 && !filter.Any(char.IsControl) ? filter : throw new InvalidOperationException("The filter is one line of up to 2,000 characters.")));
            }

            if (operation is PostsOperation or PagesOperation && Values.Get(p, "order") is { } order)
            {
                query.Add("order=" + (Regex.IsMatch(order, "^[a-z_.]{1,64}( (asc|desc))?(, ?[a-z_.]{1,64}( (asc|desc))?)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ? order : throw new InvalidOperationException($"'{order}' is not an order - published_at desc.")));
            }

            if (operation is PostsOperation or PagesOperation or GetPostOperation or GetPageOperation)
            {
                Listed(p, "include", query, null);
                Listed(p, "formats", query, Formats);
                if (browse)
                {
                    Listed(p, "fields", query, null);
                }
            }

            string? id = null;
            string? slug = null;
            if (operation is GetPostOperation or GetPageOperation)
            {
                id = Values.Get(p, "id") is { } given ? Id(given, singular) : null;
                slug = id is null ? Values.Get(p, "slug") is { } named ? Slug.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a {singular}'s slug.") : throw new InvalidOperationException($"Give the {singular} - its id or its slug.") : null;
            }
            else if (operation is UpdatePostOperation or UpdatePageOperation or DeletePostOperation or DeletePageOperation)
            {
                id = Values.Get(p, "id") is { } given ? Id(given, singular) : throw new InvalidOperationException($"Give the {singular} - its id.");
            }

            JsonObject? written = null;
            if (operation is CreatePostOperation or CreatePageOperation or UpdatePostOperation or UpdatePageOperation)
            {
                written = Values.Get(p, "fields") is { } fields
                    ? Parse(fields) as JsonObject ?? throw new InvalidOperationException("The other fields are not a JSON object - {\"tags\": [{\"name\": \"News\"}]}.")
                    : new JsonObject();
                if (Values.Get(p, "title") is { } title)
                {
                    written["title"] = title;
                }
                else if (operation is CreatePostOperation or CreatePageOperation && written["title"] is null)
                {
                    throw new InvalidOperationException($"Give the {singular}'s title.");
                }

                if (Values.Get(p, "status") is { } status)
                {
                    written["status"] = Statuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{status}' is not draft, published or scheduled (status).");
                }

                var html = Values.Get(p, "html");
                var lexical = Values.Get(p, "lexical");
                if (html is not null && lexical is not null)
                {
                    throw new InvalidOperationException("Give the content as HTML or as Lexical, not both.");
                }

                if (html is not null)
                {
                    written["html"] = html;
                    query.Add("source=html");
                }
                else if (lexical is not null)
                {
                    written["lexical"] = Parse(lexical) is JsonObject ? lexical : throw new InvalidOperationException("The Lexical content is not a JSON object - Ghost's Lexical document.");
                }

                if (written.Count == 0)
                {
                    throw new InvalidOperationException($"Give what to change - a title, the content, a status or other fields.");
                }
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            var tail = query.Count == 0 ? "" : "?" + string.Join("&", query);
            switch (operation)
            {
                case GetPostOperation:
                case GetPageOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/{kind}/{(id is not null ? id : "slug/" + slug)}/{tail}", null, cancellationToken);
                        foreach (var item in (answer?[kind] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }

                case CreatePostOperation:
                case CreatePageOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, $"/{kind}/{tail}", new JsonObject { [kind] = new JsonArray(written) }, cancellationToken);
                        foreach (var item in (answer?[kind] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }

                case UpdatePostOperation:
                case UpdatePageOperation:
                    {
                        // Ghost refuses an edit that does not carry the last change it knows of: read it first.
                        var current = await session.SendAsync(HttpMethod.Get, $"/{kind}/{id}/?fields=id,updated_at", null, cancellationToken);
                        written!["updated_at"] = (current?[kind] as JsonArray)?.FirstOrDefault()?["updated_at"]?.DeepClone() ?? throw new InvalidOperationException($"Ghost did not say when the {singular} last changed.");
                        var answer = await session.SendAsync(HttpMethod.Put, $"/{kind}/{id}/{tail}", new JsonObject { [kind] = new JsonArray(written) }, cancellationToken);
                        foreach (var item in (answer?[kind] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }

                case DeletePostOperation:
                case DeletePageOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/{kind}/{id}/", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["deleted"] = true });
                    yield break;
                default:
                    {
                        // Ghost pages by page number, every page the same size: the size is fixed at the start.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var number = 1; ; number++)
                        {
                            var page = await session.SendAsync(HttpMethod.Get, $"/{kind}/?" + string.Join("&", query.Append($"limit={size}").Append($"page={number}")), null, cancellationToken);
                            foreach (var item in (page?[kind] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (page?["meta"]?["pagination"]?["next"] is not JsonValue)
                            {
                                yield break;
                            }
                        }
                    }
            }
        }

        private static string Id(string given, string what) =>
            ObjectId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {what}'s id - 24 hexadecimal digits.");

        // A comma-separated list for the query, each item held to its shape - or, when it is a closed list, one of it.
        private static void Listed(IReadOnlyDictionary<string, string> parameters, string key, List<string> query, string[]? allowed)
        {
            if (Values.Get(parameters, key) is not { } given)
            {
                return;
            }

            var items = given.Split(',', StringSplitOptions.TrimEntries);
            if (!items.All(item => allowed is null ? FieldName.IsMatch(item) : allowed.Contains(item, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(allowed is null ? $"'{given}' is not a list of fields ({key})." : $"'{given}' is not a list of {string.Join(", ", allowed)} ({key}).");
            }

            query.Add(key + "=" + string.Join(",", items));
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A post, a page, a tag or a member: its fields as Ghost gives them - a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // One connection's calls: the site's Admin API, a fresh token for every call; a throttled call waiting as Ghost says;
        // a failure thrown with its status and Ghost's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string keyId;
            private readonly byte[] secret;

            private Session(string api, string keyId, byte[] secret, HttpClient client)
            {
                this.api = api;
                this.keyId = keyId;
                this.secret = secret;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                // The site's address - its admin's pasted from the browser too, /ghost/... cut off.
                if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
                {
                    throw new InvalidOperationException($"'{endpoint}' is not a Ghost site's address - https://example.ghost.io.");
                }

                var path = address.AbsolutePath;
                var at = path.IndexOf("/ghost", StringComparison.OrdinalIgnoreCase);
                var site = address.GetLeftPart(UriPartial.Authority) + (at >= 0 ? path[..at] : path).TrimEnd('/');
                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Ghost connection has no Admin API key.");
                var parts = key.Split(':');
                if (parts.Length != 2 || !ObjectId.IsMatch(parts[0]) || !Secret.IsMatch(parts[1]))
                {
                    throw new InvalidOperationException("The Ghost connection's key is not an Admin API key - its id, a colon and its secret, as Ghost's integration shows it.");
                }

                return new Session(site + "/ghost/api/admin", parts[0], Convert.FromHexString(parts[1]), egress.CreateHttpClient(timeout));
            }

            // Ghost's token: its key's id, five minutes long, for the Admin API, signed with the key's secret.
            private string Token()
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var header = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "HS256", ["kid"] = keyId, ["typ"] = "JWT" }.ToJsonString()));
                var payload = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["iat"] = now, ["exp"] = now + 300, ["aud"] = "/admin/" }.ToJsonString()));
                return header + "." + payload + "." + Base64Url(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(header + "." + payload)));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Ghost", Token());
                    request.Headers.TryAddWithoutValidation("Accept-Version", "v5.0");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Ghost's - a sign-in page, a proxy's - read as no posts would hide that it never was.
                    throw new InvalidOperationException($"Ghost answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = (json?["errors"] as JsonArray)?.FirstOrDefault();
                    var said = Text(error?["message"]) is { } message ? Text(error?["context"]) is { Length: > 0 } detail ? message + " " + detail : message : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
