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

namespace Stomme.Modules.Connectors.LinkedIn
{
    /// <summary>
    /// LinkedIn (n8n's LinkedIn node: a post made as a member or an organization): a member's access token over LinkedIn's
    /// versioned API (<c>https://api.linkedin.com</c>, the <c>LinkedIn-Version</c> the <c>version</c> setting names), sent as a
    /// bearer. A test names the member.
    /// </summary>
    /// <remarks>
    /// <c>profile</c> - the signed-in member: its id and person URN, name and e-mail address. <c>create_post</c> - as the member
    /// (its default), another person or an organization by its URN or id, its text as written (LinkedIn's reserved characters
    /// escaped so it reads as written, # left for hashtags), a visibility (an organization's always public), an article - its
    /// address, title and description - or a reshare of a post; the post's URN and address said. <c>update_post</c> - its text;
    /// <c>delete_post</c>; <c>posts</c> - an author's, newest first by when they changed or were made, page by page up to
    /// <c>max</c> (1,000). Every URN, address and length held to its shape before anything is sent. A throttled call waits as
    /// LinkedIn says; a failure is thrown with its status, LinkedIn's code and its message.
    /// </remarks>
    public sealed class LinkedInConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.linkedin.com";

        // The version n8n's LinkedIn node sends; LinkedIn keeps a version a year.
        public const string DefaultVersion = "202604";

        public const string ProfileOperation = "profile";

        public const string CreatePostOperation = "create_post";

        public const string UpdatePostOperation = "update_post";

        public const string DeletePostOperation = "delete_post";

        public const string PostsOperation = "posts";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const int MostText = 3000;

        private static readonly Regex Version = new("^20[0-9]{2}(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant);

        private static readonly Regex PersonId = new("^[A-Za-z0-9_-]{4,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Numeric = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex PostUrn = new("^urn:li:(share|ugcPost):[0-9]{1,30}$", RegexOptions.CultureInvariant);

        private static readonly Regex Author = new("^urn:li:(person:[A-Za-z0-9_-]{4,64}|organization:[0-9]{1,20})$", RegexOptions.CultureInvariant);

        // The characters LinkedIn's little text format reserves - escaped so the text reads as written; # stays for hashtags.
        private static readonly char[] Reserved = { '\\', '(', ')', '[', ']', '{', '}', '<', '>', '@', '|', '~', '_', '*' };

        // JSON as written: posts and their accents, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { ProfileOperation, CreatePostOperation, UpdatePostOperation, DeletePostOperation, PostsOperation };

        public string ConnectorType => "linkedin";

        public string DisplayName => "LinkedIn";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.linkedin.com");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/linkedin/marketing/community-management/shares/posts-api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("version", "API version", ConnectorConfigFieldType.Text, Required: false,
                Description: "LinkedIn's API version, YYYYMM - LinkedIn keeps each about a year.") { Default = DefaultVersion },
        });

        private static readonly ConnectorConfigField PostField = new("post", "Post", ConnectorConfigFieldType.Text, Required: true, Description: "The post's URN - urn:li:share:6844785523593134080.");

        private static readonly ConnectorConfigField TextField = new("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "As it should read - up to 3,000 characters; #words become hashtags.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ProfileOperation, "Get my profile", "The signed-in member - its id, person URN, name and e-mail address.") { Parameters = Schema() },
            new ConnectorOperation(CreatePostOperation, "Create a post", "Posts as the member, another person or an organization - text, an article or a reshare; n8n's post Create.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("author", "As", ConnectorConfigFieldType.Text, Required: false, Description: "me - the signed-in member - or urn:li:person:..., urn:li:organization:123 or organization:123.") { Default = "me" },
                    TextField,
                    new ConnectorConfigField("visibility", "Visibility", ConnectorConfigFieldType.Text, Required: false, Description: "PUBLIC, CONNECTIONS or LOGGED_IN - an organization's always PUBLIC.") { Default = "PUBLIC" },
                    new ConnectorConfigField("article_url", "Article address", ConnectorConfigFieldType.Text, Required: false, Description: "An article to share - https://example.com/post."),
                    new ConnectorConfigField("article_title", "Article title", ConnectorConfigFieldType.Text, Required: false, Description: "Its title - needed with an article."),
                    new ConnectorConfigField("article_description", "Article description", ConnectorConfigFieldType.Text, Required: false, Description: "A line about it."),
                    new ConnectorConfigField("reshare", "Reshare of", ConnectorConfigFieldType.Text, Required: false, Description: "A post's URN to reshare with the text."),
                    new ConnectorConfigField("reshare_disabled", "No resharing", ConnectorConfigFieldType.Text, Required: false, Description: "true to keep others from resharing it.")),
            },
            new ConnectorOperation(UpdatePostOperation, "Update a post", "Changes a post's text.") { Parameters = Schema(PostField, TextField) },
            new ConnectorOperation(DeletePostOperation, "Delete a post", "Deletes a post.") { Parameters = Schema(PostField) },
            new ConnectorOperation(PostsOperation, "Get posts", "An author's posts, newest first - an organization's needs its page role, a member's LinkedIn's approval.")
            {
                Parameters = Schema(
                    new ConnectorConfigField("author", "Author", ConnectorConfigFieldType.Text, Required: false, Description: "me, or urn:li:person:..., urn:li:organization:123 or organization:123.") { Default = "me" },
                    new ConnectorConfigField("sort_by", "Newest by", ConnectorConfigFieldType.Text, Required: false, Description: "LAST_MODIFIED or CREATED.") { Default = "LAST_MODIFIED" },
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/v2/userinfo", null, null, cancellationToken) as JsonObject ?? new JsonObject();
                return ConnectionTestResult.Ok($"Signed in as {Text(me["name"]) ?? Text(me["email"]) ?? Text(me["sub"]) ?? "the member"}{(Text(me["name"]) is not null && Text(me["email"]) is { } email ? $" ({email})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? CreatePostOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"LinkedIn has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var author = operation is CreatePostOperation or PostsOperation ? AuthorOf(Values.Get(p, "author") ?? "me") : null;
            var post = operation is UpdatePostOperation or DeletePostOperation ? PostOf(Values.Get(p, "post"), "post") : null;
            var text = operation is CreatePostOperation or UpdatePostOperation ? Little(context.Parameters.TryGetValue("text", out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : throw new InvalidOperationException("Give the post's text.")) : null;
            JsonObject? body = null;
            string? sortBy = null;
            switch (operation)
            {
                case CreatePostOperation:
                    {
                        var visibility = Choice(Values.Get(p, "visibility") ?? "PUBLIC", "a visibility", "PUBLIC", "CONNECTIONS", "LOGGED_IN");
                        if (author!.StartsWith("urn:li:organization:", StringComparison.Ordinal) && visibility != "PUBLIC")
                        {
                            throw new InvalidOperationException($"An organization posts publicly - not {visibility}.");
                        }

                        body = new JsonObject
                        {
                            ["author"] = author,
                            ["commentary"] = text,
                            ["visibility"] = visibility,
                            ["distribution"] = new JsonObject { ["feedDistribution"] = "MAIN_FEED", ["targetEntities"] = new JsonArray(), ["thirdPartyDistributionChannels"] = new JsonArray() },
                            ["lifecycleState"] = "PUBLISHED",
                            ["isReshareDisabledByAuthor"] = Values.Get(p, "reshare_disabled") is { } disabled && Flag(disabled, "no resharing"),
                        };
                        var url = Values.Get(p, "article_url");
                        var title = Values.Get(p, "article_title");
                        var description = Values.Get(p, "article_description");
                        if (url is not null || title is not null || description is not null)
                        {
                            var article = new JsonObject
                            {
                                ["source"] = url is not null && Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) ? url
                                    : throw new InvalidOperationException(url is null ? "Give the article's address." : $"'{url}' is not an article's address - https://example.com/post."),
                                ["title"] = title is not null ? OneLine(title, "article's title", 400) : throw new InvalidOperationException("Give the article's title."),
                            };
                            if (description is not null)
                            {
                                article["description"] = OneLine(description, "article's description", 4086);
                            }

                            body["content"] = new JsonObject { ["article"] = article };
                        }

                        if (Values.Get(p, "reshare") is { } parent)
                        {
                            if (body["content"] is not null)
                            {
                                throw new InvalidOperationException("Share an article or reshare a post - not both.");
                            }

                            body["reshareContext"] = new JsonObject { ["parent"] = PostOf(parent, "reshared post") };
                        }

                        break;
                    }

                case UpdatePostOperation:
                    body = new JsonObject { ["patch"] = new JsonObject { ["$set"] = new JsonObject { ["commentary"] = text } } };
                    break;
                case PostsOperation:
                    sortBy = Choice(Values.Get(p, "sort_by") ?? "LAST_MODIFIED", "a sort", "LAST_MODIFIED", "CREATED");
                    break;
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            if (author == "me" || operation == ProfileOperation)
            {
                var me = await session.SendAsync(HttpMethod.Get, "/v2/userinfo", null, null, cancellationToken) as JsonObject ?? new JsonObject();
                if (operation == ProfileOperation)
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = Text(me["sub"]),
                        ["urn"] = Text(me["sub"]) is { } id ? "urn:li:person:" + id : null,
                        ["name"] = Text(me["name"]),
                        ["given_name"] = Text(me["given_name"]),
                        ["family_name"] = Text(me["family_name"]),
                        ["email"] = Text(me["email"]),
                        ["email_verified"] = me["email_verified"] is JsonValue verified && verified.TryGetValue<bool>(out var flag) ? flag : null,
                        ["picture"] = Text(me["picture"]),
                        ["locale"] = me["locale"] is JsonObject locale ? Text(locale["language"]) : Text(me["locale"]),
                    });
                    yield break;
                }

                author = Text(me["sub"]) is { } sub && PersonId.IsMatch(sub) ? "urn:li:person:" + sub : throw new InvalidOperationException("LinkedIn did not name the signed-in member - is the openid scope granted?");
                if (body is not null)
                {
                    body["author"] = author;
                }
            }

            switch (operation)
            {
                case CreatePostOperation:
                    {
                        var (_, made) = await session.SendWithHeadersAsync(HttpMethod.Post, "/rest/posts", body, null, cancellationToken);
                        var id = made ?? throw new InvalidOperationException("LinkedIn made the post but did not name it.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = id,
                            ["author"] = author,
                            ["visibility"] = Text(body!["visibility"]),
                            ["url"] = $"https://www.linkedin.com/feed/update/{id}/",
                        });
                        yield break;
                    }

                case UpdatePostOperation:
                case DeletePostOperation:
                    await session.SendAsync(operation == UpdatePostOperation ? HttpMethod.Post : HttpMethod.Delete, "/rest/posts/" + Uri.EscapeDataString(post!), body, operation == UpdatePostOperation ? "PARTIAL_UPDATE" : "DELETE", cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["post"] = post, ["done"] = true });
                    yield break;
                default:
                    {
                        var read = 0;
                        var start = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var count = Math.Min(PageSize, max - read);
                            var page = await session.SendAsync(HttpMethod.Get, $"/rest/posts?author={Uri.EscapeDataString(author!)}&q=author&count={count.ToString(CultureInfo.InvariantCulture)}&start={start.ToString(CultureInfo.InvariantCulture)}&sortBy={sortBy}", null, "FINDER", cancellationToken);
                            var elements = (page?["elements"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var one in elements)
                            {
                                yield return PostRecord(one);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            start += count;
                            var next = (page?["paging"]?["links"] as JsonArray ?? new JsonArray()).Any(link => Text(link?["rel"]) == "next");
                            if (elements.Count == 0 || !next)
                            {
                                yield break;
                            }
                        }
                    }
            }
        }

        private static ConnectorRecord PostRecord(JsonObject post) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(post["id"]),
            ["author"] = Text(post["author"]),
            ["text"] = Text(post["commentary"]),
            ["visibility"] = Text(post["visibility"]),
            ["state"] = Text(post["lifecycleState"]),
            ["created_at"] = Time(post["createdAt"]),
            ["published_at"] = Time(post["publishedAt"]),
            ["modified_at"] = Time(post["lastModifiedAt"]),
            ["article_url"] = Text(post["content"]?["article"]?["source"]),
            ["article_title"] = Text(post["content"]?["article"]?["title"]),
            ["media"] = Text(post["content"]?["media"]?["id"]),
            ["reshare_of"] = Text(post["reshareContext"]?["parent"]),
            ["url"] = Text(post["id"]) is { } id ? $"https://www.linkedin.com/feed/update/{id}/" : null,
        });

        // LinkedIn's times are milliseconds since 1970; here UTC.
        private static string? Time(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : null;

        // A post's text in LinkedIn's little text format: its reserved characters escaped so it reads as written.
        private static string Little(string given)
        {
            if (given.Length > MostText)
            {
                throw new InvalidOperationException($"The text is longer than {MostText.ToString("N0", CultureInfo.InvariantCulture)} characters.");
            }

            var escaped = new StringBuilder(given.Length + 16);
            foreach (var c in given)
            {
                if (Array.IndexOf(Reserved, c) >= 0)
                {
                    escaped.Append('\\');
                }

                escaped.Append(c);
            }

            return escaped.ToString();
        }

        private static string AuthorOf(string given)
        {
            var who = given.Trim();
            if (who == "me")
            {
                return who;
            }

            var urn = who.StartsWith("organization:", StringComparison.Ordinal) ? "urn:li:" + who : who.StartsWith("person:", StringComparison.Ordinal) ? "urn:li:" + who : who;
            return Author.IsMatch(urn) ? urn : throw new InvalidOperationException($"'{given}' is not an author - me, urn:li:person:..., urn:li:organization:123 or organization:123.");
        }

        private static string PostOf(string? given, string what) =>
            given is null ? throw new InvalidOperationException($"Give the {what} - its URN.")
                : PostUrn.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a post's URN - urn:li:share:6844785523593134080.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what, int most) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.")
                : value.Length <= most ? value.Trim() : throw new InvalidOperationException($"The {what} is longer than {most.ToString("N0", CultureInfo.InvariantCulture)} characters.");

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: LinkedIn's address, its version, the token as a bearer and Rest.li's protocol version; a throttled
        // call waiting as LinkedIn says; a failure thrown with its status, LinkedIn's code and its message.
        private sealed class Session
        {
            private readonly string api;
            private readonly string version;
            private readonly string token;

            private Session(string api, string version, string token, HttpClient client)
            {
                this.api = api;
                this.version = version;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var version = Values.Get(settings, "version") ?? DefaultVersion;
                if (!Version.IsMatch(version))
                {
                    throw new InvalidOperationException($"'{version}' is not an API version - YYYYMM, {DefaultVersion}.");
                }

                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The LinkedIn connection has no access token.");
                return new Session(api, version, token, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, string? restliMethod, CancellationToken cancellationToken) =>
                (await SendWithHeadersAsync(method, path, body, restliMethod, cancellationToken)).Json;

            public async Task<(JsonNode? Json, string? Id)> SendWithHeadersAsync(HttpMethod method, string path, JsonObject? body, string? restliMethod, CancellationToken cancellationToken)
            {
                var content = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (path.StartsWith("/rest/", StringComparison.Ordinal))
                    {
                        request.Headers.Add("LinkedIn-Version", version);
                        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
                    }

                    if (restliMethod is not null)
                    {
                        request.Headers.Add("X-RestLi-Method", restliMethod);
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
                    throw new InvalidOperationException($"LinkedIn answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = Text(json?["code"]);
                    var said = Text(json?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                var id = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.FirstOrDefault() : null;
                return (json, id);
            }
        }
    }
}
