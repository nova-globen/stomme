#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace Stomme.Modules.Connectors.WordPress
{
    /// <summary>
    /// WordPress (n8n's WordPress node, its posts, pages and users: found, got, created, updated - and posts and pages
    /// deleted): a site's REST API (<c>{site}/wp-json/wp/v2</c>), a user's name and an application password by Basic. A test
    /// names the user.
    /// </summary>
    /// <remarks>
    /// The site is its address - an admin page's or the API's cut back to it (example.com/blog/wp-admin/edit.php is the site
    /// example.com/blog). A post, a page, a user, an author, a category, a tag, a parent and a featured image are named by their
    /// id (digits). <c>posts</c>: by status, words, authors, categories and tags (and those left out), a time before or after, a
    /// sticky flag, in an order, page by page (100 a page, a page no bigger than what is left, as many pages as WordPress says it
    /// has) up to <c>max</c> (1,000); one record each: its id, title (as read), content and excerpt (HTML), slug, status, link,
    /// author, categories, tags, format, sticky, featured image, comments' status and times (UTC). <c>pages</c> the same for
    /// pages, by a parent and in menu order. <c>get_post</c>, <c>get_page</c> one. <c>create_post</c> and <c>create_page</c>: a
    /// title, content (HTML, as written), an excerpt, a slug, a status (WordPress's draft when none), a password, an author,
    /// comments and pings open or closed, a format, sticky, categories and tags, a publishing time, a template, a featured image -
    /// a page's parent and menu order; <c>update_post</c> and <c>update_page</c> change those given, with no default (something,
    /// or refused); <c>delete_post</c> and <c>delete_page</c> move one to the trash, or delete it for good when <c>force</c>.
    /// <c>users</c>: by words and roles, in an order; <c>get_user</c>; <c>create_user</c> a username, an address and a password,
    /// names, a site, a description and roles; <c>update_user</c>. A throttled call waits as the site says; a failure is thrown
    /// with its status, WordPress's message and its code (<c>HTTP 404: Invalid post ID. (rest_post_invalid_id)</c>).
    /// </remarks>
    public sealed class WordPressConnector : IConnector, IConnectorExtractor
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

        public const string UsersOperation = "users";

        public const string GetUserOperation = "get_user";

        public const string CreateUserOperation = "create_user";

        public const string UpdateUserOperation = "update_user";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Slug = new("^[a-z0-9_%-]{1,200}$", RegexOptions.CultureInvariant);

        // JSON as written: a post's HTML, ampersands and accents as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Statuses = { "publish", "future", "draft", "pending", "private" };

        private static readonly string[] Formats = { "standard", "aside", "chat", "gallery", "link", "image", "quote", "status", "video", "audio" };

        private static readonly string[] Known =
        {
            PostsOperation, GetPostOperation, CreatePostOperation, UpdatePostOperation, DeletePostOperation, PagesOperation, GetPageOperation, CreatePageOperation,
            UpdatePageOperation, DeletePageOperation, UsersOperation, GetUserOperation, CreateUserOperation, UpdateUserOperation,
        };

        public string ConnectorType => "wordpress";

        public string DisplayName => "WordPress";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The pair as WordPress names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Username",
            ["password"] = "Application password (Users > Profile > Application Passwords)",
        };

        public string EndpointExample => "https://example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://wordpress.org");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.wordpress.org/rest-api/reference/");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField IdField(string name, string what) => new(name, what, ConnectorConfigFieldType.Text, Required: true, Description: $"The {what.ToLowerInvariant()}'s id (digits).");

        private static ConnectorConfigField[] ListFields(bool posts) => new[]
        {
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "publish, future, draft, pending or private - several comma-separated. Empty: published."),
            new ConnectorConfigField("search", "Words", ConnectorConfigFieldType.Text, Required: false, Description: "Those holding these words."),
            new ConnectorConfigField("author", "Authors", ConnectorConfigFieldType.Text, Required: false, Description: "Users' ids, comma-separated."),
            new ConnectorConfigField("after", "Published after", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00 - the site's time."),
            new ConnectorConfigField("before", "Published before", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31, or a time - the site's time."),
            new ConnectorConfigField("order_by", "Order by", ConnectorConfigFieldType.Text, Required: false, Description: posts ? "date, modified, id, title, slug, author, relevance." : "date, modified, id, title, slug, author, parent, menu_order, relevance."),
            new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "desc or asc."),
            new ConnectorConfigField("context", "Context", ConnectorConfigFieldType.Text, Required: false, Description: "view, embed or edit (edit: as stored, for an editor)."),
        }.Concat(posts
            ? new[]
            {
                new ConnectorConfigField("categories", "Categories", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
                new ConnectorConfigField("categories_exclude", "Not in categories", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
                new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
                new ConnectorConfigField("tags_exclude", "Not tagged", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
                new ConnectorConfigField("sticky", "Sticky", ConnectorConfigFieldType.Boolean, Required: false, Description: "Only sticky posts (true) or none of them (false)."),
            }
            : new[]
            {
                new ConnectorConfigField("parent", "Parent page", ConnectorConfigFieldType.Text, Required: false, Description: "Its child pages - the parent's id."),
                new ConnectorConfigField("menu_order", "Menu order", ConnectorConfigFieldType.Integer, Required: false, Description: "Only pages at this place in the menu."),
            }).Append(MaxField).ToArray();

        private static ConnectorConfigField[] WriteFields(bool posts, bool create) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Multiline, Required: false, Description: "HTML, as written."),
            new ConnectorConfigField("excerpt", "Excerpt", ConnectorConfigFieldType.Multiline, Required: false, Description: "A summary."),
            new ConnectorConfigField("slug", "Slug", ConnectorConfigFieldType.Text, Required: false, Description: "Its address's last part - my-first-post."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: create ? "publish, future, draft, pending or private. Empty: a draft." : "publish, future, draft, pending or private."),
            new ConnectorConfigField("password", "Password", ConnectorConfigFieldType.Text, Required: false, Description: "Readers need it to read it."),
            new ConnectorConfigField("author", "Author", ConnectorConfigFieldType.Text, Required: false, Description: "A user's id."),
            new ConnectorConfigField("comment_status", "Comments", ConnectorConfigFieldType.Text, Required: false, Description: "open or closed."),
            new ConnectorConfigField("ping_status", "Pings", ConnectorConfigFieldType.Text, Required: false, Description: "open or closed."),
            new ConnectorConfigField("date", "Published at", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31T09:00:00 - the site's time; a later time with status future schedules it."),
            new ConnectorConfigField("template", "Template", ConnectorConfigFieldType.Text, Required: false, Description: "A theme's template file, or empty for the default."),
            new ConnectorConfigField("featured_media", "Featured image", ConnectorConfigFieldType.Text, Required: false, Description: "A media item's id."),
        }.Concat(posts
            ? new[]
            {
                new ConnectorConfigField("format", "Format", ConnectorConfigFieldType.Text, Required: false, Description: "standard, aside, chat, gallery, link, image, quote, status, video or audio."),
                new ConnectorConfigField("sticky", "Sticky", ConnectorConfigFieldType.Boolean, Required: false, Description: "Kept at the top of the blog."),
                new ConnectorConfigField("categories", "Categories", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
                new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Ids, comma-separated."),
            }
            : new[]
            {
                new ConnectorConfigField("parent", "Parent page", ConnectorConfigFieldType.Text, Required: false, Description: "The parent page's id."),
                new ConnectorConfigField("menu_order", "Menu order", ConnectorConfigFieldType.Integer, Required: false, Description: "Its place among its siblings."),
            }).ToArray();

        private static ConnectorConfigField[] UserFields(bool create) => new[]
        {
            new ConnectorConfigField("username", "Username", ConnectorConfigFieldType.Text, Required: create, Description: create ? "One word to sign in with." : "Cannot be changed once made."),
            new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: create, Description: "An address."),
            new ConnectorConfigField("password", "Password", ConnectorConfigFieldType.Text, Required: create, Description: "The user's password."),
            new ConnectorConfigField("name", "Display name", ConnectorConfigFieldType.Text, Required: false, Description: "As shown on the site."),
            new ConnectorConfigField("first_name", "First name", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("last_name", "Last name", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("nickname", "Nickname", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("url", "Website", ConnectorConfigFieldType.Text, Required: false, Description: "An address."),
            new ConnectorConfigField("description", "Biography", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text."),
            new ConnectorConfigField("slug", "Slug", ConnectorConfigFieldType.Text, Required: false, Description: "Its author page's last part."),
            new ConnectorConfigField("roles", "Roles", ConnectorConfigFieldType.Text, Required: false, Description: "subscriber, contributor, author, editor or administrator - comma-separated."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(PostsOperation, "Get posts", "The posts - by status, words, authors, categories, tags and time - one record each: its title, content, status and link - n8n's post Get Many.")
            {
                Parameters = new ConnectorConfigSchema(ListFields(posts: true)),
            },
            new ConnectorOperation(GetPostOperation, "Get a post", "One post - n8n's post Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("post", "Post"), new ConnectorConfigField("context", "Context", ConnectorConfigFieldType.Text, Required: false, Description: "view, embed or edit."), new ConnectorConfigField("password", "Password", ConnectorConfigFieldType.Text, Required: false, Description: "A protected post's password.") }),
            },
            new ConnectorOperation(CreatePostOperation, "Create a post", "Writes a post - a draft unless a status says otherwise - n8n's post Create.")
            {
                Parameters = new ConnectorConfigSchema(WriteFields(posts: true, create: true)),
            },
            new ConnectorOperation(UpdatePostOperation, "Update a post", "Changes a post's title, content, status, categories and the rest - n8n's post Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("post", "Post") }.Concat(WriteFields(posts: true, create: false)).ToArray()),
            },
            new ConnectorOperation(DeletePostOperation, "Delete a post", "Moves a post to the trash - or deletes it for good with force.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("post", "Post"), new ConnectorConfigField("force", "For good", ConnectorConfigFieldType.Boolean, Required: false, Description: "Delete it, not trash it.") { Default = "false" } }),
            },
            new ConnectorOperation(PagesOperation, "Get pages", "The pages - by status, words, authors, a parent and time - one record each - n8n's page Get Many.")
            {
                Parameters = new ConnectorConfigSchema(ListFields(posts: false)),
            },
            new ConnectorOperation(GetPageOperation, "Get a page", "One page - n8n's page Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("page", "Page"), new ConnectorConfigField("context", "Context", ConnectorConfigFieldType.Text, Required: false, Description: "view, embed or edit."), new ConnectorConfigField("password", "Password", ConnectorConfigFieldType.Text, Required: false, Description: "A protected page's password.") }),
            },
            new ConnectorOperation(CreatePageOperation, "Create a page", "Writes a page - a draft unless a status says otherwise - n8n's page Create.")
            {
                Parameters = new ConnectorConfigSchema(WriteFields(posts: false, create: true)),
            },
            new ConnectorOperation(UpdatePageOperation, "Update a page", "Changes a page - n8n's page Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("page", "Page") }.Concat(WriteFields(posts: false, create: false)).ToArray()),
            },
            new ConnectorOperation(DeletePageOperation, "Delete a page", "Moves a page to the trash - or deletes it for good with force.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("page", "Page"), new ConnectorConfigField("force", "For good", ConnectorConfigFieldType.Boolean, Required: false, Description: "Delete it, not trash it.") { Default = "false" } }),
            },
            new ConnectorOperation(UsersOperation, "Get users", "The site's users - by words and roles - n8n's user Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("search", "Words", ConnectorConfigFieldType.Text, Required: false, Description: "Those whose name, address or site holds these words."),
                    new ConnectorConfigField("roles", "Roles", ConnectorConfigFieldType.Text, Required: false, Description: "subscriber, contributor, author, editor or administrator - comma-separated."),
                    new ConnectorConfigField("order_by", "Order by", ConnectorConfigFieldType.Text, Required: false, Description: "id, name, registered_date, email, url or slug."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
                    new ConnectorConfigField("context", "Context", ConnectorConfigFieldType.Text, Required: false, Description: "view, embed or edit (edit: with addresses and roles)."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetUserOperation, "Get a user", "One user - n8n's user Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("user", "User"), new ConnectorConfigField("context", "Context", ConnectorConfigFieldType.Text, Required: false, Description: "view, embed or edit.") }),
            },
            new ConnectorOperation(CreateUserOperation, "Create a user", "Adds a user - n8n's user Create.")
            {
                Parameters = new ConnectorConfigSchema(UserFields(create: true)),
            },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "Changes a user - n8n's user Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField("user", "User") }.Concat(UserFields(create: false)).ToArray()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (me, _) = await SendAsync(client, Basic(context.Credentials), HttpMethod.Get, api + "/users/me?context=edit", null, cancellationToken);
                return Text(me?["name"]) is { } name
                    ? ConnectionTestResult.Ok($"Signed in as {name}{(Text(me?["email"]) is { } email ? $" ({email})" : "")}.")
                    : ConnectionTestResult.Fail("WordPress answered without a user - check the username and application password.");
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
            var api = Api(context.Endpoint);
            var basic = Basic(context.Credentials);
            var operation = context.OperationName ?? PostsOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"WordPress has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            var posts = operation.Contains("post", StringComparison.OrdinalIgnoreCase);
            var users = operation.Contains("user", StringComparison.OrdinalIgnoreCase);
            var path = posts ? "/posts" : users ? "/users" : "/pages";
            var one = posts ? "post" : users ? "user" : "page";
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, PostsOperation) || Is(operation, PagesOperation) || Is(operation, UsersOperation))
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var query = users ? UserQuery(parameters) : ListQuery(parameters, posts);
                var read = 0;
                for (var page = 1; ; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var (found, pages) = await SendAsync(client, basic, HttpMethod.Get, $"{api}{path}?{string.Join("&", query.Append("per_page=" + size.ToString(CultureInfo.InvariantCulture)).Append("page=" + page.ToString(CultureInfo.InvariantCulture)))}", null, cancellationToken);
                    var items = (found as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        read++;
                        yield return users ? UserRecord(item) : ContentRecord(item);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    if (items.Count < size || pages is { } total && page >= total)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, CreatePostOperation) || Is(operation, CreatePageOperation) || Is(operation, CreateUserOperation))
            {
                var body = users ? UserBody(parameters, create: true) : ContentBody(parameters, posts, create: true);
                var (made, _) = await SendAsync(client, basic, HttpMethod.Post, $"{api}{path}{(users ? "?context=edit" : "")}", body, cancellationToken);
                yield return users ? UserRecord(made as JsonObject ?? new JsonObject()) : ContentRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            var id = Id(Values.Get(parameters, one), one);
            if (Is(operation, GetPostOperation) || Is(operation, GetPageOperation) || Is(operation, GetUserOperation))
            {
                var query = new List<string>();
                if (Values.Get(parameters, "context") is { } contextName)
                {
                    query.Add("context=" + Choice(contextName, "context", "view", "embed", "edit"));
                }

                if (!users && Values.Get(parameters, "password") is { } password)
                {
                    query.Add("password=" + Uri.EscapeDataString(password));
                }

                var (found, _) = await SendAsync(client, basic, HttpMethod.Get, $"{api}{path}/{id}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}", null, cancellationToken);
                yield return users ? UserRecord(found as JsonObject ?? new JsonObject()) : ContentRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdatePostOperation) || Is(operation, UpdatePageOperation) || Is(operation, UpdateUserOperation))
            {
                var body = users ? UserBody(parameters, create: false) : ContentBody(parameters, posts, create: false);
                if (body.Count == 0)
                {
                    throw new InvalidOperationException($"Give something to change on the {one}.");
                }

                var (changed, _) = await SendAsync(client, basic, HttpMethod.Post, $"{api}{path}/{id}{(users ? "?context=edit" : "")}", body, cancellationToken);
                yield return users ? UserRecord(changed as JsonObject ?? new JsonObject()) : ContentRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            // A post or a page deleted: to the trash, or for good with force (WordPress answers the post itself, or what it was).
            var force = Values.GetBool(parameters, "force", false);
            var (gone, _) = await SendAsync(client, basic, HttpMethod.Delete, $"{api}{path}/{id}?force={(force ? "true" : "false")}", null, cancellationToken);
            var fields = ContentFields((force ? gone?["previous"] : gone) as JsonObject ?? new JsonObject { ["id"] = long.Parse(id, CultureInfo.InvariantCulture) });
            fields[force ? "deleted" : "trashed"] = true;
            yield return new ConnectorRecord(fields);
        }

        // A list's filters as WordPress's query: statuses, words, authors, times, an order, a context - and a post's categories,
        // tags and sticky flag, a page's parent and menu order.
        private static List<string> ListQuery(IReadOnlyDictionary<string, string> parameters, bool posts)
        {
            var query = new List<string>();
            if (Values.Get(parameters, "status") is { } status)
            {
                query.Add("status=" + string.Join(",", Words(status).Select(word => Choice(word, "status", Statuses))));
            }

            if (Values.Get(parameters, "search") is { } search)
            {
                query.Add("search=" + Uri.EscapeDataString(OneLine(search, "search")));
            }

            foreach (var name in posts ? new[] { "author", "categories", "categories_exclude", "tags", "tags_exclude" } : new[] { "author" })
            {
                if (Values.Get(parameters, name) is { } ids)
                {
                    query.Add(name + "=" + string.Join(",", Words(ids).Select(id => Id(id, Singular(name)))));
                }
            }

            foreach (var name in new[] { "after", "before" })
            {
                if (Values.Get(parameters, name) is { } time)
                {
                    query.Add(name + "=" + Uri.EscapeDataString(SiteTime(time, "published " + name)));
                }
            }

            if (Values.Get(parameters, "order_by") is { } orderBy)
            {
                query.Add("orderby=" + (posts ? Choice(orderBy, "field to order by", "date", "modified", "id", "title", "slug", "author", "relevance") : Choice(orderBy, "field to order by", "date", "modified", "id", "title", "slug", "author", "parent", "menu_order", "relevance")));
            }

            if (Values.Get(parameters, "order") is { } order)
            {
                query.Add("order=" + Choice(order, "direction", "desc", "asc"));
            }

            if (Values.Get(parameters, "context") is { } contextName)
            {
                query.Add("context=" + Choice(contextName, "context", "view", "embed", "edit"));
            }

            if (posts && Values.Get(parameters, "sticky") is { } sticky)
            {
                query.Add("sticky=" + Flag(sticky, "sticky"));
            }

            if (!posts && Values.Get(parameters, "parent") is { } parent)
            {
                query.Add("parent=" + Id(parent, "parent"));
            }

            if (!posts && Values.Get(parameters, "menu_order") is { } menuOrder)
            {
                query.Add("menu_order=" + Whole(menuOrder, "menu order"));
            }

            return query;
        }

        private static List<string> UserQuery(IReadOnlyDictionary<string, string> parameters)
        {
            var query = new List<string>();
            if (Values.Get(parameters, "search") is { } search)
            {
                query.Add("search=" + Uri.EscapeDataString(OneLine(search, "search")));
            }

            if (Values.Get(parameters, "roles") is { } roles)
            {
                query.Add("roles=" + string.Join(",", Words(roles).Select(Role)));
            }

            if (Values.Get(parameters, "order_by") is { } orderBy)
            {
                query.Add("orderby=" + Choice(orderBy, "field to order by", "id", "name", "registered_date", "email", "url", "slug"));
            }

            if (Values.Get(parameters, "order") is { } order)
            {
                query.Add("order=" + Choice(order, "direction", "asc", "desc"));
            }

            if (Values.Get(parameters, "context") is { } contextName)
            {
                query.Add("context=" + Choice(contextName, "context", "view", "embed", "edit"));
            }

            return query;
        }

        // A post's or a page's fields as WordPress takes them: a title of one line, HTML as written, a status, comments and pings,
        // ids as numbers, a publishing time in the site's time.
        private static JsonObject ContentBody(IReadOnlyDictionary<string, string> parameters, bool posts, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "title") is { } title)
            {
                body["title"] = OneLine(title, "title");
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the title.");
            }

            foreach (var name in new[] { "content", "excerpt" })
            {
                if (parameters.TryGetValue(name, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    body[name] = text;
                }
            }

            if (Values.Get(parameters, "slug") is { } slug)
            {
                body["slug"] = Slug.IsMatch(slug) ? slug : throw new InvalidOperationException($"'{slug}' is not a slug - lower-case letters, digits and -, my-first-post.");
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                body["status"] = Choice(status, "status", Statuses);
            }

            if (Values.Get(parameters, "password") is { } password)
            {
                body["password"] = password;
            }

            foreach (var name in new[] { "author", "featured_media" }.Concat(posts ? Array.Empty<string>() : new[] { "parent" }))
            {
                if (Values.Get(parameters, name) is { } id)
                {
                    body[name] = long.Parse(Id(id, Singular(name)), CultureInfo.InvariantCulture);
                }
            }

            foreach (var name in new[] { "comment_status", "ping_status" })
            {
                if (Values.Get(parameters, name) is { } open)
                {
                    body[name] = Choice(open, name.Replace('_', ' '), "open", "closed");
                }
            }

            if (Values.Get(parameters, "date") is { } date)
            {
                body["date"] = SiteTime(date, "publishing time");
            }

            if (Values.Get(parameters, "template") is { } template)
            {
                body["template"] = OneLine(template, "template");
            }

            if (posts)
            {
                if (Values.Get(parameters, "format") is { } format)
                {
                    body["format"] = Choice(format, "format", Formats);
                }

                if (Values.Get(parameters, "sticky") is { } sticky)
                {
                    body["sticky"] = Flag(sticky, "sticky") == "true";
                }

                foreach (var name in new[] { "categories", "tags" })
                {
                    if (Values.Get(parameters, name) is { } ids)
                    {
                        body[name] = new JsonArray(Words(ids).Select(id => (JsonNode)JsonValue.Create(long.Parse(Id(id, Singular(name)), CultureInfo.InvariantCulture))!).ToArray());
                    }
                }
            }
            else if (Values.Get(parameters, "menu_order") is { } menuOrder)
            {
                body["menu_order"] = int.Parse(Whole(menuOrder, "menu order"), CultureInfo.InvariantCulture);
            }

            return body;
        }

        private static JsonObject UserBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            foreach (var name in new[] { "username", "email", "password" })
            {
                if (Values.Get(parameters, name) is { } value)
                {
                    body[name] = name == "email" ? (value.Contains('@', StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace) ? value : throw new InvalidOperationException($"'{value}' is not an address."))
                        : name == "username" ? OneLine(value, "username")
                        : value;
                }
                else if (create)
                {
                    throw new InvalidOperationException($"Give the user's {name}.");
                }
            }

            foreach (var name in new[] { "name", "first_name", "last_name", "nickname", "url", "slug" })
            {
                if (Values.Get(parameters, name) is { } value)
                {
                    body[name] = OneLine(value, name.Replace('_', ' '));
                }
            }

            if (parameters.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
            {
                body["description"] = description;
            }

            if (Values.Get(parameters, "roles") is { } roles)
            {
                body["roles"] = new JsonArray(Words(roles).Select(role => (JsonNode)JsonValue.Create(Role(role))!).ToArray());
            }

            return body;
        }

        private static ConnectorRecord ContentRecord(JsonObject item) => new(ContentFields(item));

        private static Dictionary<string, object?> ContentFields(JsonObject item) => new(StringComparer.Ordinal)
        {
            ["id"] = Number(item["id"]),
            ["title"] = Rendered(item["title"]) is { } title ? WebUtility.HtmlDecode(title) : null,
            ["content"] = Rendered(item["content"]),
            ["excerpt"] = Rendered(item["excerpt"]),
            ["slug"] = Text(item["slug"]),
            ["status"] = Text(item["status"]),
            ["type"] = Text(item["type"]),
            ["link"] = Text(item["link"]),
            ["author"] = Number(item["author"]),
            ["categories"] = Ids(item["categories"]),
            ["tags"] = Ids(item["tags"]),
            ["parent"] = Number(item["parent"]),
            ["menu_order"] = Number(item["menu_order"]),
            ["format"] = Text(item["format"]),
            ["sticky"] = item["sticky"] is JsonValue sticky && sticky.TryGetValue<bool>(out var top) ? top : null,
            ["featured_media"] = Number(item["featured_media"]),
            ["comment_status"] = Text(item["comment_status"]),
            ["template"] = Text(item["template"]),
            ["date"] = Utc(item["date_gmt"]),
            ["modified"] = Utc(item["modified_gmt"]),
        };

        private static ConnectorRecord UserRecord(JsonObject user) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(user["id"]),
            ["name"] = Text(user["name"]),
            ["username"] = Text(user["username"]),
            ["email"] = Text(user["email"]),
            ["first_name"] = Text(user["first_name"]),
            ["last_name"] = Text(user["last_name"]),
            ["nickname"] = Text(user["nickname"]),
            ["slug"] = Text(user["slug"]),
            ["url"] = Text(user["url"]),
            ["description"] = Text(user["description"]),
            ["link"] = Text(user["link"]),
            ["roles"] = string.Join(", ", (user["roles"] as JsonArray ?? new JsonArray()).Select(Text).Where(role => !string.IsNullOrEmpty(role))),
            ["registered_date"] = Text(user["registered_date"]),
        });

        // The site's API: its address - an admin page's or the API's cut back to the site - and /wp-json/wp/v2.
        private static string Api(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a WordPress site's address - https://example.com.");
            }

            var path = address.AbsolutePath;
            foreach (var marker in new[] { "/wp-json", "/wp-admin", "/wp-login.php" })
            {
                var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    path = path[..at];
                }
            }

            return address.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/') + "/wp-json/wp/v2";
        }

        private static string Basic(IReadOnlyDictionary<string, string> credentials)
        {
            var username = credentials.TryGetValue("username", out var user) && !string.IsNullOrWhiteSpace(user) ? user.Trim() : throw new InvalidOperationException("The WordPress connection has no username.");
            var password = credentials.TryGetValue("password", out var secret) && !string.IsNullOrWhiteSpace(secret) ? secret.Trim() : throw new InvalidOperationException("The WordPress connection has no application password.");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        }

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a WordPress {what}'s id (digits).");

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string Role(string given) => Choice(given, "role", "subscriber", "contributor", "author", "editor", "administrator");

        private static string Flag(string given, string what) =>
            given.Trim().ToLowerInvariant() is "true" or "false" ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not true or false ({what}).");

        private static string Whole(string given, string what) =>
            int.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not a whole number ({what}).");

        // A time as the site keeps it: a day or a time, with no zone (WordPress reads it in the site's time zone).
        private static string SiteTime(string given, string what) =>
            DateTime.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) && !given.Contains('Z', StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(given, "[+-][0-9]{2}:?[0-9]{2}$")
                ? time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-31, or 2026-10-31T09:00:00, the site's time (no zone).");

        // What a parameter names one of, for a refusal's words.
        private static string Singular(string name) => name switch
        {
            "categories" or "categories_exclude" => "category",
            "tags" or "tags_exclude" => "tag",
            "featured_media" => "featured image",
            _ => name,
        };

        private static IEnumerable<string> Words(string given) => given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Rendered(JsonNode? node) => node is JsonObject holder ? Text(holder["rendered"]) ?? Text(holder["raw"]) : Text(node);

        private static string Ids(JsonNode? node) => string.Join(", ", (node as JsonArray ?? new JsonArray()).Select(Number).Where(id => id is not null));

        private static string? Utc(JsonNode? node) => Text(node) is { } time && DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            ? moment.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;

        // WordPress's answer and the pages it says it has; a throttled call waits as the site says; a failure thrown with its status,
        // WordPress's message and its code.
        private static async Task<(JsonNode? Data, int? Pages)> SendAsync(HttpClient client, string basic, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                throw new InvalidOperationException($"WordPress answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Text(json?["message"]) is { } message ? message + (Text(json?["code"]) is { } code ? $" ({code})" : "") : null;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            var pages = response.Headers.TryGetValues("X-WP-TotalPages", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : (int?)null;
            return (json, pages);
        }
    }
}
