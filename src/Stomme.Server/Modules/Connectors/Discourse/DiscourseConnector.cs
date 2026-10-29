#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.Discourse
{
    /// <summary>
    /// Discourse (n8n's Discourse node: its posts, categories, groups, group members and users): a forum's API with an
    /// API key and the user it acts as in Api-Key and Api-Username, over the host's egress. A test names who the key acts as.
    /// </summary>
    /// <remarks>
    /// <c>posts</c> (the latest, newest first, page by page by the last one's id up to <c>max</c>), <c>post</c>,
    /// <c>create_post</c> (a topic with its title and category, or a reply in a topic) and <c>update_post</c> (with an edit's
    /// reason); <c>categories</c>, <c>create_category</c> and <c>update_category</c> (a name and colours); <c>groups</c>
    /// (page by page), <c>group</c>, <c>create_group</c> and <c>update_group</c>; <c>add_to_group</c> and
    /// <c>remove_from_group</c> (users by name); <c>users</c> (an admin's list by a flag, an order, either way, with e-mails and
    /// stats, page by page), <c>user</c> (by name or an SSO external id) and <c>create_user</c>. A record is its fields as
    /// Discourse gives them, a list or an object as its JSON. Every id, name, user name, colour, flag and order held to its
    /// shape before anything is sent; a success answer that is not JSON - or Discourse's own success false - a failure that
    /// says so. A throttled call waits as Discourse says; a failure is thrown with its status and Discourse's words.
    /// </remarks>
    public sealed class DiscourseConnector : IConnector, IConnectorExtractor
    {
        public const string PostsOperation = "posts";

        public const string PostOperation = "post";

        public const string CreatePostOperation = "create_post";

        public const string UpdatePostOperation = "update_post";

        public const string CategoriesOperation = "categories";

        public const string CreateCategoryOperation = "create_category";

        public const string UpdateCategoryOperation = "update_category";

        public const string GroupsOperation = "groups";

        public const string GroupOperation = "group";

        public const string CreateGroupOperation = "create_group";

        public const string UpdateGroupOperation = "update_group";

        public const string AddToGroupOperation = "add_to_group";

        public const string RemoveFromGroupOperation = "remove_from_group";

        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string CreateUserOperation = "create_user";

        public const int DefaultMax = 1000;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^[A-Za-z0-9_.-]{1,60}$", RegexOptions.CultureInvariant);

        private static readonly Regex Colour = new("^#?([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            PostsOperation, PostOperation, CreatePostOperation, UpdatePostOperation, CategoriesOperation, CreateCategoryOperation, UpdateCategoryOperation, GroupsOperation,
            GroupOperation, CreateGroupOperation, UpdateGroupOperation, AddToGroupOperation, RemoveFromGroupOperation, UsersOperation, UserOperation, CreateUserOperation,
        };

        private static readonly string[] Flags = { "active", "new", "staff", "suspended", "blocked", "suspect" };

        private static readonly string[] Orders = { "created", "last_emailed", "seen", "username", "email", "trust_level", "days_visited", "posts_read", "topics_viewed", "posts", "read_time" };

        public string ConnectorType => "discourse";

        public string DisplayName => "Discourse";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://forum.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "API username (system for an all-users key)", ["password"] = "API key (Admin > Advanced > API keys)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.discourse.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.discourse.org/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PostsOperation, "Get posts", "The latest posts, newest first - n8n's Discourse Post Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(PostOperation, "Get a post", "n8n's Discourse Post Get.") { Parameters = Schema(Field("post", "Post", "The post's id.", required: true)) },
            new ConnectorOperation(CreatePostOperation, "Create a post", "A topic, or a reply in one - n8n's Discourse Post Create.")
            {
                Parameters = Schema(
                    Field("content", "Content", "What it says - Markdown.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("title", "Title", "A new topic's title."),
                    Field("category", "Category", "A new topic's category id."),
                    Field("topic", "Topic", "The topic's id - a reply in it."),
                    Field("reply_to", "Reply to", "The post's number in the topic it answers.")),
            },
            new ConnectorOperation(UpdatePostOperation, "Update a post", "n8n's Discourse Post Update.")
            {
                Parameters = Schema(Field("post", "Post", "The post's id.", required: true), Field("content", "Content", "What it says now - Markdown.", ConnectorConfigFieldType.Multiline, required: true), Field("edit_reason", "Edit reason", "Why it changed.")),
            },
            new ConnectorOperation(CategoriesOperation, "Get categories", "n8n's Discourse Category Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(CreateCategoryOperation, "Create a category", "n8n's Discourse Category Create.")
            {
                Parameters = Schema(Field("name", "Name", "Its name.", required: true), Field("color", "Colour", "Its colour - 0088CC."), Field("text_color", "Text colour", "Its text's colour - FFFFFF.")),
            },
            new ConnectorOperation(UpdateCategoryOperation, "Update a category", "n8n's Discourse Category Update.")
            {
                Parameters = Schema(Field("category", "Category", "The category's id.", required: true), Field("name", "Name", "Its name.", required: true), Field("color", "Colour", "Its colour - 0088CC."), Field("text_color", "Text colour", "Its text's colour - FFFFFF.")),
            },
            new ConnectorOperation(GroupsOperation, "Get groups", "n8n's Discourse Group Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(GroupOperation, "Get a group", "By its name - n8n's Discourse Group Get.") { Parameters = Schema(Field("name", "Name", "The group's name.", required: true)) },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "n8n's Discourse Group Create.") { Parameters = Schema(Field("name", "Name", "Its name - letters, digits, '.', '-' and '_'.", required: true)) },
            new ConnectorOperation(UpdateGroupOperation, "Update a group", "Its name - n8n's Discourse Group Update.") { Parameters = Schema(Field("group", "Group", "The group's id.", required: true), Field("name", "Name", "Its new name.", required: true)) },
            new ConnectorOperation(AddToGroupOperation, "Add to a group", "n8n's Discourse User Group Add.") { Parameters = Schema(Field("group", "Group", "The group's id.", required: true), Field("usernames", "User names", "Comma-separated.", required: true)) },
            new ConnectorOperation(RemoveFromGroupOperation, "Remove from a group", "n8n's Discourse User Group Remove.") { Parameters = Schema(Field("group", "Group", "The group's id.", required: true), Field("usernames", "User names", "Comma-separated.", required: true)) },
            new ConnectorOperation(UsersOperation, "Get users", "An admin's list - n8n's Discourse User Get Many.")
            {
                Parameters = Schema(
                    Field("flag", "Flag", "active (the default), new, staff, suspended, blocked or suspect."),
                    Field("order", "Order", "created, last_emailed, seen, username, email, trust_level, days_visited, posts_read, topics_viewed, posts or read_time."),
                    Field("ascending", "Ascending", "true or false.", ConnectorConfigFieldType.Boolean),
                    Field("show_emails", "Show e-mails", "true: their e-mail addresses too.", ConnectorConfigFieldType.Boolean),
                    Field("stats", "Stats", "true: their stats too.", ConnectorConfigFieldType.Boolean),
                    MaxField),
            },
            new ConnectorOperation(UserOperation, "Get a user", "By name or SSO external id - n8n's Discourse User Get.") { Parameters = Schema(Field("username", "User name", "Their user name."), Field("external_id", "External id", "Their SSO external id.")) },
            new ConnectorOperation(CreateUserOperation, "Create a user", "n8n's Discourse User Create.")
            {
                Parameters = Schema(
                    Field("name", "Name", "Their name.", required: true),
                    Field("email", "E-mail", "Their e-mail.", required: true),
                    Field("username", "User name", "Their user name.", required: true),
                    Field("password", "Password", "Their password - read it from the record, never written here.", required: true),
                    Field("active", "Active", "true: active at once.", ConnectorConfigFieldType.Boolean),
                    Field("approved", "Approved", "true: approved at once.", ConnectorConfigFieldType.Boolean)),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = (await session.SendAsync(HttpMethod.Get, "/session/current.json", null, cancellationToken))?["current_user"] as JsonObject;
                return ConnectionTestResult.Ok($"Discourse: the key acts as {(Text(me?["username"]) is { } name ? "@" + name : "a user")}{(me?["admin"] is JsonValue admin && admin.TryGetValue<bool>(out var isAdmin) && isAdmin ? ", an admin" : "")}.");
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
                ?? throw new InvalidOperationException($"Discourse has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string name, string what) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).") : throw new InvalidOperationException($"Give the {what}'s id.");
            string? OptionalId(string name) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a number ({name}).") : null;
            string NameOf(string name, string what) => Values.Get(p, name) is { } given ? Name.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {what} - letters, digits, '.', '-' and '_' ({name}).") : throw new InvalidOperationException($"Give the {what}.");
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            string? ColourOf(string name) => Values.Get(p, name) is { } given ? Colour.IsMatch(given) ? given.TrimStart('#').ToUpperInvariant() : throw new InvalidOperationException($"'{given}' is not a colour - 0088CC ({name}).") : null;
            string Content() => Values.Get(p, "content") ?? throw new InvalidOperationException("Give the post's content.");

            JsonObject? body = null;
            string path;
            var method = HttpMethod.Get;
            switch (operation)
            {
                case PostOperation:
                    path = $"/posts/{IdOf("post", "post")}.json";
                    break;
                case CreatePostOperation:
                    {
                        body = new JsonObject { ["raw"] = Content() };
                        var topic = OptionalId("topic");
                        if (Values.Get(p, "title") is { } title)
                        {
                            body["title"] = topic is null ? title : throw new InvalidOperationException("A reply has no title - give a title for a new topic, or a topic to reply in.");
                        }
                        else if (topic is null)
                        {
                            throw new InvalidOperationException("Give a new topic's title, or the topic to reply in.");
                        }

                        if (OptionalId("category") is { } category)
                        {
                            body["category"] = long.Parse(category, CultureInfo.InvariantCulture);
                        }

                        if (topic is not null)
                        {
                            body["topic_id"] = long.Parse(topic, CultureInfo.InvariantCulture);
                        }

                        if (OptionalId("reply_to") is { } replyTo)
                        {
                            body["reply_to_post_number"] = topic is not null ? long.Parse(replyTo, CultureInfo.InvariantCulture) : throw new InvalidOperationException("A reply to a post needs its topic.");
                        }

                        method = HttpMethod.Post;
                        path = "/posts.json";
                        break;
                    }

                case UpdatePostOperation:
                    {
                        var post = IdOf("post", "post");
                        var change = new JsonObject { ["raw"] = Content() };
                        if (Values.Get(p, "edit_reason") is { } reason)
                        {
                            change["edit_reason"] = reason;
                        }

                        body = new JsonObject { ["post"] = change };
                        method = HttpMethod.Put;
                        path = $"/posts/{post}.json";
                        break;
                    }

                case CreateCategoryOperation or UpdateCategoryOperation:
                    {
                        var category = operation == UpdateCategoryOperation ? IdOf("category", "category") : null;
                        body = new JsonObject { ["name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the category's name.") };
                        if (ColourOf("color") is { } colour)
                        {
                            body["color"] = colour;
                        }

                        if (ColourOf("text_color") is { } textColour)
                        {
                            body["text_color"] = textColour;
                        }

                        method = category is null ? HttpMethod.Post : HttpMethod.Put;
                        path = category is null ? "/categories.json" : $"/categories/{category}.json";
                        break;
                    }

                case GroupOperation:
                    path = $"/groups/{NameOf("name", "group's name")}.json";
                    break;
                case CreateGroupOperation:
                    body = new JsonObject { ["group"] = new JsonObject { ["name"] = NameOf("name", "group's name") } };
                    method = HttpMethod.Post;
                    path = "/admin/groups.json";
                    break;
                case UpdateGroupOperation:
                    {
                        var group = IdOf("group", "group");
                        body = new JsonObject { ["group"] = new JsonObject { ["name"] = NameOf("name", "group's name") } };
                        method = HttpMethod.Put;
                        path = $"/groups/{group}.json";
                        break;
                    }

                case AddToGroupOperation or RemoveFromGroupOperation:
                    {
                        var group = IdOf("group", "group");
                        var names = (Values.Get(p, "usernames") ?? throw new InvalidOperationException("Give the user names - comma-separated."))
                            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(name => Name.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a user name - letters, digits, '.', '-' and '_' (usernames)."))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        body = new JsonObject { ["usernames"] = string.Join(",", names) };
                        method = operation == AddToGroupOperation ? HttpMethod.Put : HttpMethod.Delete;
                        path = $"/groups/{group}/members.json";
                        break;
                    }

                case UserOperation:
                    {
                        var username = Values.Get(p, "username");
                        var external = Values.Get(p, "external_id");
                        path = (username, external) switch
                        {
                            ({ } name, null) => $"/u/{(Name.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a user name - letters, digits, '.', '-' and '_' (username)."))}.json",
                            (null, { } id) => $"/u/by-external/{Uri.EscapeDataString(id)}.json",
                            (null, null) => throw new InvalidOperationException("Give their user name or SSO external id."),
                            _ => throw new InvalidOperationException("Give their user name or SSO external id - one of them."),
                        };
                        break;
                    }

                case CreateUserOperation:
                    {
                        var email = Values.Get(p, "email") is { } given ? Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail (email).") : throw new InvalidOperationException("Give their e-mail.");
                        body = new JsonObject
                        {
                            ["name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give their name."),
                            ["email"] = email,
                            ["username"] = NameOf("username", "user name"),
                            ["password"] = p.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password) ? password : throw new InvalidOperationException("Give their password."),
                        };
                        foreach (var name in new[] { "active", "approved" })
                        {
                            if (FlagOf(name) is { } flag)
                            {
                                body[name] = flag;
                            }
                        }

                        method = HttpMethod.Post;
                        path = "/users.json";
                        break;
                    }

                case UsersOperation:
                    {
                        var flag = Values.Get(p, "flag") is { } given ? Flags.Contains(given.ToLowerInvariant()) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not active, new, staff, suspended, blocked or suspect (flag).") : "active";
                        var query = new List<string>();
                        if (Values.Get(p, "order") is { } order)
                        {
                            query.Add("order=" + (Orders.Contains(order.ToLowerInvariant()) ? order.ToLowerInvariant() : throw new InvalidOperationException($"'{order}' is not created, last_emailed, seen, username, email, trust_level, days_visited, posts_read, topics_viewed, posts or read_time (order).")));
                        }

                        foreach (var (name, wire) in new[] { ("ascending", "asc"), ("show_emails", "show_emails"), ("stats", "stats") })
                        {
                            if (FlagOf(name) is { } on)
                            {
                                query.Add(wire + "=" + (on ? "true" : "false"));
                            }
                        }

                        path = $"/admin/users/list/{flag}.json" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
                        break;
                    }

                case CategoriesOperation:
                    path = "/categories.json";
                    break;
                case GroupsOperation:
                    path = "/groups.json";
                    break;
                default:
                    path = "/posts.json";
                    break;
            }

            using var client = session.Client;
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case PostsOperation:
                    {
                        // The latest posts, newest first: each page the posts before the last one read, an empty page or one that
                        // goes no further the end.
                        var read = 0;
                        long? before = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + (before is null ? "" : "?before=" + before.Value.ToString(CultureInfo.InvariantCulture)), null, cancellationToken);
                            var items = (answer?["latest_posts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            var last = items.Count == 0 ? null : items[^1]["id"] is JsonValue id && id.TryGetValue<long>(out var value) ? value : (long?)null;
                            if (last is null || last >= before)
                            {
                                yield break;
                            }

                            before = last;
                        }
                    }

                case CategoriesOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, path, null, cancellationToken))?["category_list"]?["categories"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                    {
                        yield return Record(item);
                    }

                    yield break;
                case GroupsOperation or UsersOperation:
                    {
                        // Page by page from the first (the groups') or from 1 (the users'), an empty page the end.
                        var read = 0;
                        for (var page = operation == GroupsOperation ? 0 : 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + (path.Contains('?') ? "&" : "?") + "page=" + page.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
                            var items = ((operation == GroupsOperation ? answer?["groups"] : answer) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            if (items.Count == 0)
                            {
                                yield break;
                            }

                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }
                        }
                    }

                case AddToGroupOperation or RemoveFromGroupOperation:
                    {
                        var answer = await session.SendAsync(method, path, body, cancellationToken) as JsonObject;
                        yield return Done(("group", long.Parse(path.Split('/')[2], CultureInfo.InvariantCulture)), ("usernames", Text(body!["usernames"])), (operation == AddToGroupOperation ? "added" : "removed", true), ("skipped", (answer?["skipped_usernames"] as JsonArray)?.ToJsonString(Wire)));
                        yield break;
                    }

                default:
                    {
                        var answer = await session.SendAsync(method, path, body, cancellationToken) as JsonObject ?? new JsonObject();
                        var item = operation switch
                        {
                            CreateCategoryOperation or UpdateCategoryOperation => answer["category"] as JsonObject,
                            GroupOperation => answer["group"] as JsonObject,
                            CreateGroupOperation => answer["basic_group"] as JsonObject,
                            UpdatePostOperation => answer["post"] as JsonObject,
                            UserOperation => answer["user"] as JsonObject,
                            _ => answer,
                        };
                        yield return Record(item ?? answer);
                        yield break;
                    }
            }
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the forum's API with the key and the user it acts as in Api-Key and Api-Username; a throttled
        // call waiting as Discourse says; a failure thrown with its status and Discourse's words - its own success false one too.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string username;
            private readonly string key;

            private Session(string baseUrl, string username, string key, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.username = username;
                this.key = key;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the forum's address - https://forum.example.com.");
                }

                return new Session(
                    Values.BaseUrl(endpoint, ""),
                    Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Discourse connection has no API username."),
                    Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Discourse connection has no API key."),
                    egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Add("Api-Key", key);
                    request.Headers.Add("Api-Username", username);
                    request.Headers.Accept.ParseAdd("application/json");
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
                    // A page that is not the forum's - a sign-in page, a proxy's - read as no posts would hide that it never was.
                    throw new InvalidOperationException($"Discourse answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                var said = json is JsonObject error
                    ? error["errors"] is JsonArray errors ? string.Join("; ", errors.Select(Text).OfType<string>()) : Text(error["message"]) ?? Text(error["error"])
                    : null;
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                if (json is JsonObject done && done["success"] is JsonValue success && (success.TryGetValue<bool>(out var ok) ? !ok : Text(success) is "false"))
                {
                    // Discourse answers some refusals - a user it would not create - with HTTP 200 and its success false.
                    throw new InvalidOperationException($"Discourse refused it{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
