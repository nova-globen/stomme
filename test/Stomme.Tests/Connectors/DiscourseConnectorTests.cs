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
using Stomme.Modules.Connectors.Discourse;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Discourse (n8n's Discourse node: its posts, categories, groups, group members and users): every call the forum's
    /// API with the key and the user it acts as in Api-Key and Api-Username; the latest posts read page by page by the last
    /// one's id, no more than asked; a topic and a reply posted, a post got and edited with a reason; categories read, created
    /// and changed with their colours; groups read page by page, got, created and renamed, users added and removed; users
    /// listed by a flag and an order page by page, got by name or external id, created; what could go wrong refused before
    /// anything is sent; Discourse's words on a failure - its own success false too; a test naming who the key acts as.
    /// </summary>
    public sealed class DiscourseConnectorTests
    {
        private const string Forum = "https://forum.kafe.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "system", ["password"] = "dk_key_1" };

        private sealed class Discourse : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? User, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Api-Key", out var keys) ? keys.Single() : null, request.Headers.TryGetValues("Api-Username", out var users) ? users.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Discourse discourse, string operation, Dictionary<string, string> parameters, string endpoint = Forum, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new DiscourseConnector(), Contexts.Extract(new StubEgress(discourse), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Posts(int newest, int count) =>
            "{\"latest_posts\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => "{\"id\":" + (newest - i) + ",\"username\":\"ada\",\"raw\":\"Fika " + (newest - i) + "\",\"topic_id\":7,\"post_number\":" + (i + 1) + ",\"hidden\":false}")) + "]}";

        [Fact]
        public async Task Posts_are_read_page_by_page_posted_got_and_edited()
        {
            var discourse = new Discourse
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.EndsWith("before=151", StringComparison.Ordinal) => (HttpStatusCode.OK, Posts(150, 50)),
                    ("GET", var u) when u.EndsWith("before=201", StringComparison.Ordinal) => (HttpStatusCode.OK, Posts(200, 50)),
                    ("GET", var u) when u.EndsWith("before=101", StringComparison.Ordinal) => (HttpStatusCode.OK, Posts(100, 0)),
                    ("GET", var u) when u.EndsWith("/posts.json", StringComparison.Ordinal) => (HttpStatusCode.OK, Posts(250, 50)),
                    ("GET", _) => (HttpStatusCode.OK, "{\"id\":201,\"raw\":\"Fika?\",\"cooked\":\"<p>Fika?</p>\",\"actions_summary\":[]}"),
                    ("PUT", _) => (HttpStatusCode.OK, "{\"post\":{\"id\":201,\"raw\":\"Fika at 3\",\"version\":2}}"),
                    _ => (HttpStatusCode.OK, "{\"id\":202,\"topic_id\":7,\"post_number\":2,\"raw\":\"Kafé!\"}"),
                },
            };
            (string, string, string?) Last() => (discourse.Requests[^1].Method.Method, discourse.Requests[^1].Url, discourse.Requests[^1].Body);

            var read = await Run(discourse, DiscourseConnector.PostsOperation, new() { ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Forum}/posts.json", $"{Forum}/posts.json?before=201", $"{Forum}/posts.json?before=151" }, discourse.Requests.Select(request => request.Url)); // by the last one's id
            Assert.All(discourse.Requests, request => Assert.Equal(("dk_key_1", "system"), (request.Key, request.User)));
            Assert.Equal((250L, "ada", "Fika 250", false), ((long?)read[0].Fields["id"], (string?)read[0].Fields["username"], (string?)read[0].Fields["raw"], (bool?)read[0].Fields["hidden"]));
            discourse.Requests.Clear();
            Assert.Equal(150, (await Run(discourse, DiscourseConnector.PostsOperation, new())).Count); // an empty page the end
            Assert.Equal(4, discourse.Requests.Count);
            var stuck = new Discourse { Answer = (_, _) => (HttpStatusCode.OK, Posts(50, 10)) };
            Assert.Equal(20, (await Run(stuck, DiscourseConnector.PostsOperation, new())).Count); // a page that goes no further the end
            Assert.Equal(2, stuck.Requests.Count);
            var endless = new Discourse { Answer = (_, url) => (HttpStatusCode.OK, Posts(url.Contains("before=", StringComparison.Ordinal) ? int.Parse(url[(url.IndexOf("before=", StringComparison.Ordinal) + 7)..]) - 1 : 100000, 50)) };
            Assert.Equal(1000, (await Run(endless, DiscourseConnector.PostsOperation, new())).Count); // 1,000 at most when no max is given

            Assert.Equal("<p>Fika?</p>", (string?)Assert.Single(await Run(discourse, DiscourseConnector.PostOperation, new() { ["post"] = "201" }, endpoint: Forum + "/")).Fields["cooked"]);
            Assert.Equal(("GET", $"{Forum}/posts/201.json", (string?)null), Last());
            Assert.Equal(202L, (long?)Assert.Single(await Run(discourse, DiscourseConnector.CreatePostOperation, new() { ["title"] = "Fika & bröd at 3", ["content"] = "Who's in? **Kafé**", ["category"] = "4" })).Fields["id"]);
            Assert.Equal(("POST", $"{Forum}/posts.json", (string?)"{\"raw\":\"Who's in? **Kafé**\",\"title\":\"Fika & bröd at 3\",\"category\":4}"), Last());
            await Run(discourse, DiscourseConnector.CreatePostOperation, new() { ["content"] = "I am!", ["topic"] = "7", ["reply_to"] = "1" });
            Assert.Equal("{\"raw\":\"I am!\",\"topic_id\":7,\"reply_to_post_number\":1}", Last().Item3);
            await Run(discourse, null!, new() { ["content"] = "Me too", ["topic"] = "7" }); // no operation: a post
            Assert.Equal(("POST", "{\"raw\":\"Me too\",\"topic_id\":7}"), (Last().Item1, Last().Item3));
            Assert.Equal(2L, (long?)Assert.Single(await Run(discourse, DiscourseConnector.UpdatePostOperation, new() { ["post"] = "201", ["content"] = "Fika at 3", ["edit_reason"] = "Tid" })).Fields["version"]);
            Assert.Equal(("PUT", $"{Forum}/posts/201.json", (string?)"{\"post\":{\"raw\":\"Fika at 3\",\"edit_reason\":\"Tid\"}}"), Last());
            await Run(discourse, DiscourseConnector.UpdatePostOperation, new() { ["post"] = "201", ["content"] = "Fika" });
            Assert.Equal("{\"post\":{\"raw\":\"Fika\"}}", Last().Item3);
        }

        [Fact]
        public async Task Categories_groups_and_users_are_kept()
        {
            static string Users(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"username\":\"u" + i + "\",\"trust_level\":1}")) + "]";
            static string Groups(int first, int count) => "{\"groups\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"name\":\"g" + i + "\"}")) + "],\"total_rows_groups\":45}";
            var discourse = new Discourse
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/categories.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"category_list\":{\"categories\":[{\"id\":4,\"name\":\"Fika\",\"color\":\"0088CC\",\"subcategory_ids\":[9]},{\"id\":5,\"name\":\"Bröd\"}]}}"),
                    ("GET", var u) when u.Contains("/groups.json?page=0", StringComparison.Ordinal) => (HttpStatusCode.OK, Groups(1, 36)),
                    ("GET", var u) when u.Contains("/groups.json?page=1", StringComparison.Ordinal) => (HttpStatusCode.OK, Groups(37, 9)),
                    ("GET", var u) when u.Contains("/groups.json", StringComparison.Ordinal) => (HttpStatusCode.OK, Groups(0, 0)),
                    ("GET", var u) when u.Contains("/groups/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"group\":{\"id\":41,\"name\":\"fika\",\"user_count\":3}}"),
                    ("GET", var u) when u.Contains("page=1", StringComparison.Ordinal) && u.Contains("/admin/users/", StringComparison.Ordinal) => (HttpStatusCode.OK, Users(1, 100)),
                    ("GET", var u) when u.Contains("page=2", StringComparison.Ordinal) && u.Contains("/admin/users/", StringComparison.Ordinal) => (HttpStatusCode.OK, Users(101, 30)),
                    ("GET", var u) when u.Contains("/admin/users/", StringComparison.Ordinal) => (HttpStatusCode.OK, "[]"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"user\":{\"id\":3,\"username\":\"ada\",\"name\":\"Ada\"}}"),
                    ("POST", var u) when u.EndsWith("/categories.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"category\":{\"id\":6,\"name\":\"Kafé\",\"color\":\"0088CC\"}}"),
                    ("PUT", var u) when u.Contains("/categories/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"success\":\"OK\",\"category\":{\"id\":6,\"name\":\"Kafé & bröd\"}}"),
                    ("POST", var u) when u.EndsWith("/admin/groups.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"basic_group\":{\"id\":41,\"name\":\"fika\"}}"),
                    ("PUT", var u) when u.EndsWith("/members.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"success\":\"OK\",\"usernames\":[\"ada\"],\"skipped_usernames\":[\"bo\"]}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"success\":\"OK\",\"usernames\":[\"ada\",\"bo\"]}"),
                    ("PUT", _) => (HttpStatusCode.OK, "{\"success\":\"OK\"}"),
                    _ => (HttpStatusCode.OK, "{\"success\":true,\"active\":true,\"message\":\"Your account is activated and ready to use.\",\"user_id\":12}"),
                },
            };
            (string, string, string?) Last() => (discourse.Requests[^1].Method.Method, discourse.Requests[^1].Url, discourse.Requests[^1].Body);

            var categories = await Run(discourse, DiscourseConnector.CategoriesOperation, new() { ["max"] = "1" });
            Assert.Equal(("Fika", "[9]"), ((string?)Assert.Single(categories).Fields["name"], (string?)categories[0].Fields["subcategory_ids"])); // no more than asked
            Assert.Equal(("GET", $"{Forum}/categories.json", (string?)null), Last());
            Assert.Equal(6L, (long?)Assert.Single(await Run(discourse, DiscourseConnector.CreateCategoryOperation, new() { ["name"] = "Kafé", ["color"] = "#0088cc", ["text_color"] = "fff" })).Fields["id"]);
            Assert.Equal(("POST", $"{Forum}/categories.json", (string?)"{\"name\":\"Kafé\",\"color\":\"0088CC\",\"text_color\":\"FFF\"}"), Last());
            Assert.Equal("Kafé & bröd", (string?)Assert.Single(await Run(discourse, DiscourseConnector.UpdateCategoryOperation, new() { ["category"] = "6", ["name"] = "Kafé & bröd" })).Fields["name"]);
            Assert.Equal(("PUT", $"{Forum}/categories/6.json", (string?)"{\"name\":\"Kafé & bröd\"}"), Last());

            discourse.Requests.Clear();
            Assert.Equal(45, (await Run(discourse, DiscourseConnector.GroupsOperation, new())).Count); // an empty page the end
            Assert.Equal(new[] { $"{Forum}/groups.json?page=0", $"{Forum}/groups.json?page=1", $"{Forum}/groups.json?page=2" }, discourse.Requests.Select(request => request.Url));
            Assert.Equal(3L, (long?)Assert.Single(await Run(discourse, DiscourseConnector.GroupOperation, new() { ["name"] = "fika" })).Fields["user_count"]);
            Assert.Equal(("GET", $"{Forum}/groups/fika.json", (string?)null), Last());
            Assert.Equal(41L, (long?)Assert.Single(await Run(discourse, DiscourseConnector.CreateGroupOperation, new() { ["name"] = "fika" })).Fields["id"]);
            Assert.Equal(("POST", $"{Forum}/admin/groups.json", (string?)"{\"group\":{\"name\":\"fika\"}}"), Last());
            Assert.Equal("OK", (string?)Assert.Single(await Run(discourse, DiscourseConnector.UpdateGroupOperation, new() { ["group"] = "41", ["name"] = "fika-lag" })).Fields["success"]);
            Assert.Equal(("PUT", $"{Forum}/groups/41.json", (string?)"{\"group\":{\"name\":\"fika-lag\"}}"), Last());
            var added = Assert.Single(await Run(discourse, DiscourseConnector.AddToGroupOperation, new() { ["group"] = "41", ["usernames"] = "ada, bo, Ada" })).Fields;
            Assert.Equal(("PUT", $"{Forum}/groups/41/members.json", (string?)"{\"usernames\":\"ada,bo\"}"), Last()); // each once
            Assert.Equal((41L, "ada,bo", true, "[\"bo\"]"), ((long?)added["group"], (string?)added["usernames"], (bool?)added["added"], (string?)added["skipped"]));
            var removed = Assert.Single(await Run(discourse, DiscourseConnector.RemoveFromGroupOperation, new() { ["group"] = "41", ["usernames"] = "ada" })).Fields;
            Assert.Equal(("DELETE", $"{Forum}/groups/41/members.json", (string?)"{\"usernames\":\"ada\"}"), Last());
            Assert.Equal((true, (string?)null), ((bool?)removed["removed"], (string?)removed["skipped"]));

            discourse.Requests.Clear();
            Assert.Equal(120, (await Run(discourse, DiscourseConnector.UsersOperation, new() { ["flag"] = "Staff", ["order"] = "Trust_Level", ["ascending"] = "false", ["show_emails"] = "true", ["stats"] = "false", ["max"] = "120" })).Count);
            Assert.Equal(new[] { $"{Forum}/admin/users/list/staff.json?order=trust_level&asc=false&show_emails=true&stats=false&page=1", $"{Forum}/admin/users/list/staff.json?order=trust_level&asc=false&show_emails=true&stats=false&page=2" }, discourse.Requests.Select(request => request.Url));
            discourse.Requests.Clear();
            Assert.Equal(130, (await Run(discourse, DiscourseConnector.UsersOperation, new())).Count); // active by default, an empty page the end
            Assert.Equal($"{Forum}/admin/users/list/active.json?page=3", discourse.Requests[^1].Url);
            Assert.Equal("Ada", (string?)Assert.Single(await Run(discourse, DiscourseConnector.UserOperation, new() { ["username"] = "ada" })).Fields["name"]);
            Assert.Equal($"{Forum}/u/ada.json", Last().Item2);
            await Run(discourse, DiscourseConnector.UserOperation, new() { ["external_id"] = "sso/ada 1" });
            Assert.Equal($"{Forum}/u/by-external/sso%2Fada%201.json", Last().Item2);
            var created = Assert.Single(await Run(discourse, DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada Kafé", ["email"] = "ada@kafe.example.test", ["username"] = "ada", ["password"] = " s3cret fika ", ["active"] = "true", ["approved"] = "False" })).Fields;
            Assert.Equal(("POST", $"{Forum}/users.json", (string?)"{\"name\":\"Ada Kafé\",\"email\":\"ada@kafe.example.test\",\"username\":\"ada\",\"password\":\" s3cret fika \",\"active\":true,\"approved\":false}"), Last()); // the password as given
            Assert.Equal((12L, true), ((long?)created["user_id"], (bool?)created["active"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_discourses_words_on_a_failure()
        {
            var discourse = new Discourse();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Forum, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(discourse, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Discourse has no operation 'search' - posts, post, create_post,", await Refused("search", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(DiscourseConnector.PostsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the post's id.", await Refused(DiscourseConnector.PostOperation, new()));
            Assert.Equal("'2a' is not an id (post).", await Refused(DiscourseConnector.UpdatePostOperation, new() { ["post"] = "2a", ["content"] = "Fika" }));
            Assert.Equal("Give the post's content.", await Refused(DiscourseConnector.UpdatePostOperation, new() { ["post"] = "201" }));
            Assert.Equal("Give the post's content.", await Refused(DiscourseConnector.CreatePostOperation, new() { ["title"] = "Fika" }));
            Assert.Equal("Give a new topic's title, or the topic to reply in.", await Refused(DiscourseConnector.CreatePostOperation, new() { ["content"] = "Fika" }));
            Assert.Equal("A reply has no title - give a title for a new topic, or a topic to reply in.", await Refused(DiscourseConnector.CreatePostOperation, new() { ["content"] = "Fika", ["title"] = "Fika", ["topic"] = "7" }));
            Assert.Equal("A reply to a post needs its topic.", await Refused(DiscourseConnector.CreatePostOperation, new() { ["content"] = "Fika", ["title"] = "Fika", ["reply_to"] = "1" }));
            Assert.Equal("'fika' is not a number (category).", await Refused(DiscourseConnector.CreatePostOperation, new() { ["content"] = "Fika", ["title"] = "Fika", ["category"] = "fika" }));
            Assert.Equal("'7.5' is not a number (topic).", await Refused(DiscourseConnector.CreatePostOperation, new() { ["content"] = "Fika", ["topic"] = "7.5" }));
            Assert.Equal("Give the category's name.", await Refused(DiscourseConnector.CreateCategoryOperation, new()));
            Assert.Equal("Give the category's id.", await Refused(DiscourseConnector.UpdateCategoryOperation, new() { ["name"] = "Fika" }));
            Assert.Equal("'blue' is not a colour - 0088CC (color).", await Refused(DiscourseConnector.CreateCategoryOperation, new() { ["name"] = "Fika", ["color"] = "blue" }));
            Assert.Equal("'#12345' is not a colour - 0088CC (text_color).", await Refused(DiscourseConnector.UpdateCategoryOperation, new() { ["category"] = "6", ["name"] = "Fika", ["text_color"] = "#12345" }));
            Assert.Equal("Give the group's name.", await Refused(DiscourseConnector.GroupOperation, new()));
            Assert.Equal("'fika lag' is not a group's name - letters, digits, '.', '-' and '_' (name).", await Refused(DiscourseConnector.CreateGroupOperation, new() { ["name"] = "fika lag" }));
            Assert.Equal("'fika/../x' is not a group's name - letters, digits, '.', '-' and '_' (name).", await Refused(DiscourseConnector.GroupOperation, new() { ["name"] = "fika/../x" }));
            Assert.Equal("Give the group's id.", await Refused(DiscourseConnector.UpdateGroupOperation, new() { ["name"] = "fika" }));
            Assert.Equal("Give the user names - comma-separated.", await Refused(DiscourseConnector.AddToGroupOperation, new() { ["group"] = "41" }));
            Assert.Equal("'bo kafe' is not a user name - letters, digits, '.', '-' and '_' (usernames).", await Refused(DiscourseConnector.RemoveFromGroupOperation, new() { ["group"] = "41", ["usernames"] = "ada,bo kafe" }));
            Assert.Equal("'banned' is not active, new, staff, suspended, blocked or suspect (flag).", await Refused(DiscourseConnector.UsersOperation, new() { ["flag"] = "banned" }));
            Assert.Equal("'age' is not created, last_emailed, seen, username, email, trust_level, days_visited, posts_read, topics_viewed, posts or read_time (order).", await Refused(DiscourseConnector.UsersOperation, new() { ["order"] = "age" }));
            Assert.Equal("'yes' is not true or false (stats).", await Refused(DiscourseConnector.UsersOperation, new() { ["stats"] = "yes" }));
            Assert.Equal("Give their user name or SSO external id.", await Refused(DiscourseConnector.UserOperation, new()));
            Assert.Equal("Give their user name or SSO external id - one of them.", await Refused(DiscourseConnector.UserOperation, new() { ["username"] = "ada", ["external_id"] = "1" }));
            Assert.Equal("'../admin' is not a user name - letters, digits, '.', '-' and '_' (username).", await Refused(DiscourseConnector.UserOperation, new() { ["username"] = "../admin" }));
            Assert.Equal("Give their e-mail.", await Refused(DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada", ["username"] = "ada", ["password"] = "x" }));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada", ["email"] = "ada@kafe", ["username"] = "ada", ["password"] = "x" }));
            Assert.Equal("Give their name.", await Refused(DiscourseConnector.CreateUserOperation, new() { ["email"] = "ada@kafe.example.test", ["username"] = "ada", ["password"] = "x" }));
            Assert.Equal("Give the user name.", await Refused(DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada", ["email"] = "ada@kafe.example.test", ["password"] = "x" }));
            Assert.Equal("Give their password.", await Refused(DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada", ["email"] = "ada@kafe.example.test", ["username"] = "ada", ["password"] = "" }));
            Assert.Equal("'maybe' is not true or false (approved).", await Refused(DiscourseConnector.CreateUserOperation, new() { ["name"] = "Ada", ["email"] = "ada@kafe.example.test", ["username"] = "ada", ["password"] = "x", ["approved"] = "maybe" }));
            Assert.Equal("Give the forum's address - https://forum.example.com.", await Refused(DiscourseConnector.PostsOperation, new(), endpoint: ""));
            Assert.Equal("The Discourse connection has no API username.", await Refused(DiscourseConnector.PostsOperation, new(), credentials: new() { ["password"] = "k" }));
            Assert.Equal("The Discourse connection has no API key.", await Refused(DiscourseConnector.PostsOperation, new(), credentials: new() { ["username"] = "system" }));
            Assert.Empty(discourse.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Discourse { Answer = (_, _) => (status, answer) }, DiscourseConnector.CreateGroupOperation, new() { ["name"] = "fika" }))).Message;
            Assert.Equal("HTTP 422: Name has already been taken; Name is too short", await Failed(HttpStatusCode.UnprocessableEntity, "{\"errors\":[\"Name has already been taken\",\"Name is too short\"],\"error_type\":\"invalid_parameters\"}"));
            Assert.Equal("HTTP 403: You are not permitted to view the requested resource.", await Failed(HttpStatusCode.Forbidden, "{\"errors\":[\"You are not permitted to view the requested resource.\"],\"error_type\":\"invalid_access\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errors\":[\"" + new string('e', 400) + "\"]}")); // a long one cut
            Assert.Equal("Discourse refused it: Username must be unique", await Failed(HttpStatusCode.OK, "{\"success\":false,\"message\":\"Username must be unique\",\"errors\":{}}"));
            Assert.Equal("Discourse refused it.", await Failed(HttpStatusCode.OK, "{\"success\":\"false\"}"));
            Assert.Equal("Discourse answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_who_the_key_acts_as()
        {
            Task<ConnectionTestResult> Test(Discourse discourse) =>
                new DiscourseConnector().TestConnectionAsync(Contexts.Test(new StubEgress(discourse), Forum, null, Credentials));
            var current = new Discourse { Answer = (_, _) => (HttpStatusCode.OK, "{\"current_user\":{\"id\":-1,\"username\":\"system\",\"admin\":true}}") };
            var tested = await Test(current);
            Assert.Equal((true, "Discourse: the key acts as @system, an admin."), (tested.Success, tested.Message));
            Assert.Equal(($"{Forum}/session/current.json", "GET", "dk_key_1", "system"), (current.Requests[0].Url, current.Requests[0].Method.Method, current.Requests[0].Key, current.Requests[0].User));
            Assert.Equal("Discourse: the key acts as @ada.", (await Test(new Discourse { Answer = (_, _) => (HttpStatusCode.OK, "{\"current_user\":{\"username\":\"ada\",\"admin\":false}}") })).Message);
            Assert.Equal("Discourse: the key acts as a user.", (await Test(new Discourse())).Message);
            var refused = await Test(new Discourse { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"errors\":[\"You are not permitted to view the requested resource. The API username or key is invalid.\"],\"error_type\":\"invalid_access\"}") });
            Assert.Equal((false, "HTTP 403: You are not permitted to view the requested resource. The API username or key is invalid."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "discourse"), (new DiscourseConnector().AuthType, new DiscourseConnector().ConnectorType));
            Assert.Equal(16, new DiscourseConnector().Operations.Count);
            Assert.Equal("API key (Admin > Advanced > API keys)", new DiscourseConnector().CredentialLabels!["password"]); // in Discourse's words
        }
    }
}
