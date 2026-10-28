#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.WordPress;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - WordPress: the username and application password by Basic; an admin page's or the API's address cut back to the
    /// site, a site in a folder kept; the posts page by page as many as WordPress says it has, up to the most asked for, a page no
    /// bigger than what is left, by status, words, authors, categories, tags (and those left out), times, an order and sticky; each
    /// post a plain record, its title as read and its content as HTML; the pages by a parent and menu order; the users by words and
    /// roles; one got, created (WordPress's draft when no status; HTML as written; ids as numbers), updated with no default,
    /// trashed or deleted for good; what could go wrong refused before anything is sent; WordPress's refusal its message and code;
    /// a test naming the user.
    /// </summary>
    public sealed class WordPressConnectorTests
    {
        private const string Api = "https://blog.example.com/news/wp-json/wp/v2";

        private sealed class WordPressApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body, int? Pages)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                var (status, answer, pages) = Answer(request.Method, request.RequestUri!.AbsoluteUri);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (pages is { } total)
                {
                    response.Headers.Add("X-WP-TotalPages", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return response;
            }
        }

        private static readonly string Basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("editor:abcd efgh ijkl mnop qrst uvwx"));

        private static ConnectorExtractContext Context(WordPressApi wordpress, string operation, Dictionary<string, string> parameters, string endpoint = "https://blog.example.com/news/wp-admin/edit.php") =>
            Contexts.Extract(new StubEgress(wordpress), endpoint, operation, parameters, null, new Dictionary<string, string> { ["username"] = "editor", ["password"] = "abcd efgh ijkl mnop qrst uvwx" });

        private static WordPressConnector Connector() => new();

        private static string Post(int id, string type = "post") =>
            "{\"id\":" + id + ",\"date\":\"2026-10-01T12:00:00\",\"date_gmt\":\"2026-10-01T10:00:00\",\"modified_gmt\":\"2026-10-02T10:00:00\",\"slug\":\"post-" + id + "\",\"status\":\"publish\",\"type\":\"" + type + "\","
            + "\"link\":\"https://blog.example.com/news/post-" + id + "/\",\"title\":{\"rendered\":\"Caf&#233; &amp; tea &#8211; " + id + "\"},\"content\":{\"rendered\":\"<p>Hello <b>world</b></p>\\n\",\"protected\":false},"
            + "\"excerpt\":{\"rendered\":\"<p>Hello</p>\\n\"},\"author\":2,\"featured_media\":0,\"comment_status\":\"open\",\"sticky\":false,\"template\":\"\",\"format\":\"standard\",\"categories\":[3,4],\"tags\":[7],\"parent\":0,\"menu_order\":0}";

        private static string Posts(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(id => Post(id))) + "]";

        [Fact]
        public async Task The_posts_are_read_page_by_page_as_many_as_wordpress_has_up_to_the_most_asked_for_by_basic()
        {
            var wordpress = new WordPressApi { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=2", StringComparison.Ordinal) ? Posts(101, 100) : Posts(1, 100), 5) };
            var records = await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.PostsOperation, new()
            {
                ["status"] = "publish, Draft", ["search"] = "tea & cake", ["author"] = "2, 5", ["categories"] = "3", ["categories_exclude"] = "9", ["tags"] = "7", ["tags_exclude"] = "8",
                ["after"] = "2026-10-01", ["before"] = "2026-10-31T09:00:00", ["order_by"] = "date", ["order"] = "asc", ["context"] = "edit", ["sticky"] = "True", ["max"] = "150",
            }));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.All(wordpress.Requests, request => Assert.Equal(Basic, request.Authorization));
            const string query = "status=publish,draft&search=tea%20%26%20cake&author=2,5&categories=3&categories_exclude=9&tags=7&tags_exclude=8&after=2026-10-01T00%3A00%3A00&before=2026-10-31T09%3A00%3A00&orderby=date&order=asc&context=edit&sticky=true";
            Assert.Equal(new[] { Api + "/posts?" + query + "&per_page=100&page=1", Api + "/posts?" + query + "&per_page=50&page=2" }, wordpress.Requests.Select(request => request.Url)); // an admin page cut back to the site in its folder; a page no bigger than what is left
            var post = records[0].Fields;
            Assert.Equal((1L, "Café & tea – 1", "<p>Hello <b>world</b></p>\n", "<p>Hello</p>\n", "post-1", "publish", "post", "https://blog.example.com/news/post-1/"),
                ((long?)post["id"], (string?)post["title"], (string?)post["content"], (string?)post["excerpt"], (string?)post["slug"], (string?)post["status"], (string?)post["type"], (string?)post["link"]));
            Assert.Equal((2L, "3, 4", "7", "standard", false, "open", "2026-10-01T10:00:00Z", "2026-10-02T10:00:00Z"),
                ((long?)post["author"], (string?)post["categories"], (string?)post["tags"], (string?)post["format"], (bool?)post["sticky"], (string?)post["comment_status"], (string?)post["date"], (string?)post["modified"]));

            var last = new WordPressApi { Answer = (_, url) => (HttpStatusCode.OK, Posts(1, 100), 2) };
            Assert.Equal(200, (await Contexts.DrainAsync(Connector(), Context(last, WordPressConnector.PostsOperation, new(), "https://blog.example.com/news/wp-json/wp/v2/posts"))).Count);
            Assert.Equal(new[] { Api + "/posts?per_page=100&page=1", Api + "/posts?per_page=100&page=2" }, last.Requests.Select(request => request.Url)); // the API's address cut back; no third page past WordPress's last

            var short1 = new WordPressApi { Answer = (_, _) => (HttpStatusCode.OK, Posts(1, 3), null) };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(short1, WordPressConnector.PostsOperation, new(), "http://127.0.0.1:5271"))).Count);
            Assert.Equal("http://127.0.0.1:5271/wp-json/wp/v2/posts?per_page=100&page=1", Assert.Single(short1.Requests).Url); // a short page the last

            var ten = new WordPressApi { Answer = (_, _) => (HttpStatusCode.OK, Posts(1, 100), null) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, WordPressConnector.PostsOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(10, ten.Requests.Count);

            var pages = new WordPressApi { Answer = (_, _) => (HttpStatusCode.OK, "[" + Post(41, "page").Replace("\"parent\":0,\"menu_order\":0", "\"parent\":40,\"menu_order\":2", StringComparison.Ordinal) + "]", 1) };
            var page = Assert.Single(await Contexts.DrainAsync(Connector(), Context(pages, WordPressConnector.PagesOperation, new() { ["parent"] = "40", ["menu_order"] = "2", ["order_by"] = "menu_order", ["author"] = "2" })));
            Assert.Equal(Api + "/pages?author=2&orderby=menu_order&parent=40&menu_order=2&per_page=100&page=1", Assert.Single(pages.Requests).Url);
            Assert.Equal((40L, 2L, "page"), ((long?)page.Fields["parent"], (long?)page.Fields["menu_order"], (string?)page.Fields["type"]));

            var users = new WordPressApi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":2,\"name\":\"Ada Lovelace\",\"username\":\"ada\",\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"slug\":\"ada\",\"url\":\"\",\"description\":\"\",\"link\":\"https://blog.example.com/news/author/ada/\",\"roles\":[\"editor\",\"author\"],\"registered_date\":\"2026-01-02T03:04:05+00:00\"}]", 1) };
            var user = Assert.Single(await Contexts.DrainAsync(Connector(), Context(users, WordPressConnector.UsersOperation, new() { ["search"] = "ada", ["roles"] = "Editor, author", ["order_by"] = "name", ["order"] = "desc", ["context"] = "edit" })));
            Assert.Equal(Api + "/users?search=ada&roles=editor,author&orderby=name&order=desc&context=edit&per_page=100&page=1", Assert.Single(users.Requests).Url);
            Assert.Equal((2L, "Ada Lovelace", "ada", "ada@example.com", "editor, author"), ((long?)user.Fields["id"], (string?)user.Fields["name"], (string?)user.Fields["username"], (string?)user.Fields["email"], (string?)user.Fields["roles"]));
        }

        [Fact]
        public async Task A_post_a_page_and_a_user_are_got_created_updated_and_deleted()
        {
            var wordpress = new WordPressApi
            {
                Answer = (method, url) => (HttpStatusCode.OK, method == HttpMethod.Delete && url.Contains("force=true", StringComparison.Ordinal) ? "{\"deleted\":true,\"previous\":" + Post(9) + "}"
                    : method == HttpMethod.Delete ? Post(9).Replace("\"status\":\"publish\"", "\"status\":\"trash\"", StringComparison.Ordinal)
                    : url.Contains("/users", StringComparison.Ordinal) ? "{\"id\":12,\"name\":\"Grace\",\"username\":\"grace\",\"email\":\"grace@example.com\",\"roles\":[\"author\"]}"
                    : Post(9), null),
            };
            (HttpMethod, string, string?) Last() => (wordpress.Requests[^1].Method, wordpress.Requests[^1].Url, wordpress.Requests[^1].Body);

            Assert.Equal("Café & tea – 9", Assert.Single(await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.GetPostOperation, new() { ["post"] = "9", ["context"] = "edit", ["password"] = "s3cret & word" }))).Fields["title"]);
            Assert.Equal((HttpMethod.Get, Api + "/posts/9?context=edit&password=s3cret%20%26%20word", (string?)null), Last());

            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.CreatePostOperation, new()
            {
                ["title"] = "Café & tea", ["content"] = "<p>Hello <b>world</b></p>\n<p>Again</p>", ["excerpt"] = "Hello", ["slug"] = "cafe-tea", ["status"] = "Future", ["password"] = "s3cret",
                ["author"] = "2", ["comment_status"] = "closed", ["ping_status"] = "open", ["date"] = "2026-10-31T09:00:00", ["template"] = "single-wide.php", ["featured_media"] = "77",
                ["format"] = "Aside", ["sticky"] = "true", ["categories"] = "3, 4", ["tags"] = "7",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/posts", "{\"title\":\"Café & tea\",\"content\":\"<p>Hello <b>world</b></p>\\n<p>Again</p>\",\"excerpt\":\"Hello\",\"slug\":\"cafe-tea\",\"status\":\"future\",\"password\":\"s3cret\",\"author\":2,\"featured_media\":77,\"comment_status\":\"closed\",\"ping_status\":\"open\",\"date\":\"2026-10-31T09:00:00\",\"template\":\"single-wide.php\",\"format\":\"aside\",\"sticky\":true,\"categories\":[3,4],\"tags\":[7]}"), Last());
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.CreatePostOperation, new() { ["title"] = "Draft it" }));
            Assert.Equal("{\"title\":\"Draft it\"}", Last().Item3); // WordPress's draft

            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.UpdatePostOperation, new() { ["post"] = "9", ["status"] = "publish", ["sticky"] = "false" }));
            Assert.Equal((HttpMethod.Post, Api + "/posts/9", "{\"status\":\"publish\",\"sticky\":false}"), Last());

            var trashed = Assert.Single(await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.DeletePostOperation, new() { ["post"] = "9" })));
            Assert.Equal((HttpMethod.Delete, Api + "/posts/9?force=false", (string?)null), Last());
            Assert.Equal(("trash", true), ((string?)trashed.Fields["status"], (bool?)trashed.Fields["trashed"]));
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.DeletePostOperation, new() { ["post"] = "9", ["force"] = "true" })));
            Assert.Equal((HttpMethod.Delete, Api + "/posts/9?force=true", (string?)null), Last());
            Assert.Equal((9L, true), ((long?)deleted.Fields["id"], (bool?)deleted.Fields["deleted"])); // what it was

            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.CreatePageOperation, new() { ["title"] = "About", ["parent"] = "40", ["menu_order"] = "3", ["status"] = "publish" }));
            Assert.Equal((HttpMethod.Post, Api + "/pages", "{\"title\":\"About\",\"status\":\"publish\",\"parent\":40,\"menu_order\":3}"), Last());
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.UpdatePageOperation, new() { ["page"] = "41", ["content"] = "<p>New</p>" }));
            Assert.Equal((HttpMethod.Post, Api + "/pages/41", "{\"content\":\"<p>New</p>\"}"), Last());
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.GetPageOperation, new() { ["page"] = "41" }));
            Assert.Equal((HttpMethod.Get, Api + "/pages/41", (string?)null), Last());
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.DeletePageOperation, new() { ["page"] = "41" }));
            Assert.Equal((HttpMethod.Delete, Api + "/pages/41?force=false", (string?)null), Last());

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.CreateUserOperation, new()
            {
                ["username"] = "grace", ["email"] = "grace@example.com", ["password"] = "correct horse", ["name"] = "Grace", ["first_name"] = "Grace", ["last_name"] = "Hopper",
                ["nickname"] = "Amazing Grace", ["url"] = "https://grace.example.com", ["description"] = "Rear admiral.\nCompiler pioneer.", ["slug"] = "grace", ["roles"] = "Author",
            })));
            Assert.Equal((HttpMethod.Post, Api + "/users?context=edit", "{\"username\":\"grace\",\"email\":\"grace@example.com\",\"password\":\"correct horse\",\"name\":\"Grace\",\"first_name\":\"Grace\",\"last_name\":\"Hopper\",\"nickname\":\"Amazing Grace\",\"url\":\"https://grace.example.com\",\"slug\":\"grace\",\"description\":\"Rear admiral.\\nCompiler pioneer.\",\"roles\":[\"author\"]}"), Last());
            Assert.Equal((12L, "author"), ((long?)made.Fields["id"], (string?)made.Fields["roles"]));
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.UpdateUserOperation, new() { ["user"] = "12", ["roles"] = "editor" }));
            Assert.Equal((HttpMethod.Post, Api + "/users/12?context=edit", "{\"roles\":[\"editor\"]}"), Last());
            await Contexts.DrainAsync(Connector(), Context(wordpress, WordPressConnector.GetUserOperation, new() { ["user"] = "12", ["context"] = "edit" }));
            Assert.Equal((HttpMethod.Get, Api + "/users/12?context=edit", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_wordpresss_refusal_is_its_words()
        {
            var wordpress = new WordPressApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://blog.example.com") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(wordpress, operation, parameters, endpoint)))).Message;

            Assert.Equal("'live' is not a status - publish, future, draft, pending or private.", await Refused(WordPressConnector.PostsOperation, new() { ["status"] = "publish, live" }));
            Assert.Equal("'Ada' is not a WordPress author's id (digits).", await Refused(WordPressConnector.PostsOperation, new() { ["author"] = "Ada" }));
            Assert.Equal("'news' is not a WordPress category's id (digits).", await Refused(WordPressConnector.PostsOperation, new() { ["categories_exclude"] = "news" }));
            Assert.Equal("'#7' is not a WordPress tag's id (digits).", await Refused(WordPressConnector.PostsOperation, new() { ["tags"] = "#7" }));
            Assert.Equal("'2026-10-01T09:00:00Z' is not a time (published after) - 2026-10-31, or 2026-10-31T09:00:00, the site's time (no zone).", await Refused(WordPressConnector.PostsOperation, new() { ["after"] = "2026-10-01T09:00:00Z" }));
            Assert.Equal("'2026-10-01T09:00:00+02:00' is not a time (published before) - 2026-10-31, or 2026-10-31T09:00:00, the site's time (no zone).", await Refused(WordPressConnector.PostsOperation, new() { ["before"] = "2026-10-01T09:00:00+02:00" }));
            Assert.Equal("'soon' is not a time (published after) - 2026-10-31, or 2026-10-31T09:00:00, the site's time (no zone).", await Refused(WordPressConnector.PostsOperation, new() { ["after"] = "soon" }));
            Assert.Equal("'menu_order' is not a field to order by - date, modified, id, title, slug, author or relevance.", await Refused(WordPressConnector.PostsOperation, new() { ["order_by"] = "menu_order" }));
            Assert.Equal("'up' is not a direction - desc or asc.", await Refused(WordPressConnector.PostsOperation, new() { ["order"] = "up" }));
            Assert.Equal("'full' is not a context - view, embed or edit.", await Refused(WordPressConnector.PostsOperation, new() { ["context"] = "full" }));
            Assert.Equal("'yes' is not true or false (sticky).", await Refused(WordPressConnector.PostsOperation, new() { ["sticky"] = "yes" }));
            Assert.Equal("The search has a line break - one line only.", await Refused(WordPressConnector.PostsOperation, new() { ["search"] = "a\nb" }));
            Assert.Equal("'top' is not a whole number (menu order).", await Refused(WordPressConnector.PagesOperation, new() { ["menu_order"] = "top" }));
            Assert.Equal("'home' is not a WordPress parent's id (digits).", await Refused(WordPressConnector.PagesOperation, new() { ["parent"] = "home" }));
            Assert.Equal("'owner' is not a role - subscriber, contributor, author, editor or administrator.", await Refused(WordPressConnector.UsersOperation, new() { ["roles"] = "owner" }));
            Assert.Equal("'registered' is not a field to order by - id, name, registered_date, email, url or slug.", await Refused(WordPressConnector.UsersOperation, new() { ["order_by"] = "registered" }));
            Assert.Equal("'my-post' is not a WordPress post's id (digits).", await Refused(WordPressConnector.GetPostOperation, new() { ["post"] = "my-post" }));
            Assert.Equal("Give the title.", await Refused(WordPressConnector.CreatePostOperation, new() { ["content"] = "x" }));
            Assert.Equal("The title has a line break - one line only.", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "a\nb" }));
            Assert.Equal("'My Post' is not a slug - lower-case letters, digits and -, my-first-post.", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "x", ["slug"] = "My Post" }));
            Assert.Equal("'maybe' is not a comment status - open or closed.", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "x", ["comment_status"] = "maybe" }));
            Assert.Equal("'video2' is not a format - standard, aside, chat, gallery, link, image, quote, status, video or audio.", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "x", ["format"] = "video2" }));
            Assert.Equal("'header.png' is not a WordPress featured image's id (digits).", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "x", ["featured_media"] = "header.png" }));
            Assert.Equal("'tomorrow' is not a time (publishing time) - 2026-10-31, or 2026-10-31T09:00:00, the site's time (no zone).", await Refused(WordPressConnector.CreatePostOperation, new() { ["title"] = "x", ["date"] = "tomorrow" }));
            Assert.Equal("Give something to change on the post.", await Refused(WordPressConnector.UpdatePostOperation, new() { ["post"] = "9" }));
            Assert.Equal("Give something to change on the page.", await Refused(WordPressConnector.UpdatePageOperation, new() { ["page"] = "41" }));
            Assert.Equal("Give the user's email.", await Refused(WordPressConnector.CreateUserOperation, new() { ["username"] = "grace", ["password"] = "x" }));
            Assert.Equal("'grace at example.com' is not an address.", await Refused(WordPressConnector.CreateUserOperation, new() { ["username"] = "grace", ["email"] = "grace at example.com", ["password"] = "x" }));
            Assert.Equal("The first name has a line break - one line only.", await Refused(WordPressConnector.UpdateUserOperation, new() { ["user"] = "12", ["first_name"] = "a\nb" }));
            Assert.Equal("Give something to change on the user.", await Refused(WordPressConnector.UpdateUserOperation, new() { ["user"] = "12" }));
            Assert.StartsWith("WordPress has no operation 'delete_user' - posts, get_post,", await Refused("delete_user", new() { ["user"] = "12" }));
            Assert.Equal("'ftp://blog.example.com' is not a WordPress site's address - https://example.com.", await Refused(WordPressConnector.PostsOperation, new(), "ftp://blog.example.com"));
            Assert.Empty(wordpress.Requests);

            var refusing = new WordPressApi { Answer = (_, _) => (HttpStatusCode.NotFound, "{\"code\":\"rest_post_invalid_id\",\"message\":\"Invalid post ID.\",\"data\":{\"status\":404}}", null) };
            Assert.Equal("HTTP 404: Invalid post ID. (rest_post_invalid_id)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, WordPressConnector.GetPostOperation, new() { ["post"] = "9" })))).Message);
            var html = new WordPressApi { Answer = (_, _) => (HttpStatusCode.Forbidden, "<html>Forbidden</html>", null) };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(html, WordPressConnector.PostsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var wordpress = new WordPressApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":2,\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}", null) };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(wordpress), "https://blog.example.com/news/", credentials: new Dictionary<string, string> { ["username"] = "editor", ["password"] = "abcd efgh ijkl mnop qrst uvwx" }));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users/me?context=edit", Basic), (wordpress.Requests[0].Url, wordpress.Requests[0].Authorization));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new WordPressApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"code\":\"incorrect_password\",\"message\":\"The provided password is an invalid application password.\",\"data\":{\"status\":401}}", null) }), "https://blog.example.com", credentials: new Dictionary<string, string> { ["username"] = "editor", ["password"] = "wrong" }));
            Assert.Equal((false, "HTTP 401: The provided password is an invalid application password. (incorrect_password)"), (refused.Success, refused.Message));
            var none = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new WordPressApi()), "https://blog.example.com", credentials: new Dictionary<string, string> { ["username"] = "editor" }));
            Assert.Equal((false, "The WordPress connection has no application password."), (none.Success, none.Message));
            Assert.Equal((AuthenticationType.Basic, "wordpress", ConnectorCategory.Marketing), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "posts", "get_post", "create_post", "update_post", "delete_post", "pages", "get_page", "create_page", "update_page", "delete_page", "users", "get_user", "create_user", "update_user" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
