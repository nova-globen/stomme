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
using Stomme.Modules.Connectors.Reddit;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Reddit (n8n's Reddit node: posts, comments, the profile, subreddits and users): the refresh token exchanged at
    /// Reddit's token endpoint with the client as HTTP Basic and the user agent, the access token a bearer and the user agent on
    /// every call; a subreddit's posts and a search page by page by Reddit's after up to the most asked for, a page no bigger
    /// than what is left, Reddit's text unescaped; a post got, submitted as text or a link, its comments depth first without the
    /// "more" stubs, a comment made and edited, a post deleted; the account, a user and their activity, a subreddit, its rules,
    /// subreddits found or popular; what could go wrong refused before anything is sent - not even a token asked for; Reddit's
    /// refusals in its words; a test naming the account.
    /// </summary>
    public sealed class RedditConnectorTests
    {
        private const string Api = "https://oauth.reddit.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Reddit : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Agent, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("User-Agent", out var agent) ? string.Join(" ", agent) : null, body));
                if (url.StartsWith("https://www.reddit.example.test/api/v1/access_token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-1","expires_in":86400,"token_type":"bearer","scope":"*"}""", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Agent, string? Body)> Calls =>
                Requests.Where(request => !request.Url.StartsWith("https://www.reddit.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static readonly Dictionary<string, string> Settings = new() { ["token_url"] = "https://www.reddit.example.test/api/v1/access_token", ["user_agent"] = "web:fika-bot:1.0 (by /u/ada)" };

        private static ConnectorExtractContext Context(Reddit reddit, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(reddit), Api, operation, parameters, settings: Settings, credentials: Credentials);

        private static RedditConnector Connector() => new(new OAuth2TokenProvider());

        private static string PostJson(int n) =>
            "{\"kind\":\"t3\",\"data\":{\"id\":\"p" + n + "\",\"name\":\"t3_p" + n + "\",\"subreddit\":\"fika\",\"title\":\"Fika & bullar " + n + "\",\"author\":\"ada\",\"selftext\":\"Kaffe <3\",\"url\":\"https://example.com/" + n + "\","
            + "\"permalink\":\"/r/fika/comments/p" + n + "/fika/\",\"score\":42,\"upvote_ratio\":0.97,\"num_comments\":7,\"created_utc\":1760259600.0,\"over_18\":false,\"spoiler\":true,\"link_flair_text\":\"Recipe\",\"domain\":\"self.fika\"}}";

        private static string Listing(Func<int, string> one, int from, int count, string? after) =>
            "{\"kind\":\"Listing\",\"data\":{\"after\":" + (after is null ? "null" : "\"" + after + "\"") + ",\"children\":[" + string.Join(",", Enumerable.Range(from, count).Select(one)) + "]}}";

        [Fact]
        public async Task A_subreddits_posts_and_a_search_are_read_page_by_page_by_the_exchanged_token()
        {
            var reddit = new Reddit { Answer = (_, url, _) => (HttpStatusCode.OK, url.Contains("after=t3_p100", StringComparison.Ordinal) ? Listing(PostJson, 101, 20, null) : Listing(PostJson, 1, 100, "t3_p100")) };
            var posts = await Contexts.DrainAsync(Connector(), Context(reddit, RedditConnector.PostsOperation, new() { ["subreddit"] = "r/fika" }));

            Assert.Equal(120, posts.Count);
            var token = Assert.Single(reddit.Requests, request => request.Url.Contains("access_token", StringComparison.Ordinal));
            Assert.Equal(("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:secret-1")), "web:fika-bot:1.0 (by /u/ada)", "grant_type=refresh_token&refresh_token=refresh-1"), (token.Authorization, token.Agent, token.Body)); // the client as Basic, never in the form
            Assert.All(reddit.Calls, call => Assert.Equal(("Bearer access-1", "web:fika-bot:1.0 (by /u/ada)"), (call.Authorization, call.Agent)));
            Assert.Equal(new[] { $"{Api}/r/fika/hot?limit=100&raw_json=1", $"{Api}/r/fika/hot?limit=100&after=t3_p100&raw_json=1" }, reddit.Calls.Select(call => call.Url));
            var first = posts[0].Fields;
            Assert.Equal(("post", "p1", "t3_p1", "fika", "Fika & bullar 1", "ada", "Kaffe <3", "https://example.com/1", "https://www.reddit.com/r/fika/comments/p1/fika/"),
                ((string?)first["kind"], (string?)first["id"], (string?)first["name"], (string?)first["subreddit"], (string?)first["title"], (string?)first["author"], (string?)first["text"], (string?)first["url"], (string?)first["permalink"]));
            Assert.Equal((42L, 0.97, 7L, "2025-10-12T09:00:00Z", false, true, "Recipe", "self.fika"), ((long?)first["score"], (double?)first["upvote_ratio"], (long?)first["comments"], (string?)first["created"], (bool?)first["nsfw"], (bool?)first["spoiler"], (string?)first["flair"], (string?)first["domain"]));

            var few = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, Listing(PostJson, 1, 100, "n+x/t")) };
            Assert.Equal(130, (await Contexts.DrainAsync(Connector(), Context(few, RedditConnector.PostsOperation, new() { ["subreddit"] = "fika", ["sort"] = "TOP", ["time"] = "week", ["max"] = "130" }))).Count);
            Assert.Equal(new[] { $"{Api}/r/fika/top?t=week&limit=100&raw_json=1", $"{Api}/r/fika/top?t=week&limit=30&after=n%2Bx%2Ft&raw_json=1" }, few.Calls.Select(call => call.Url)); // a page no bigger than what is left

            var empty = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, Listing(PostJson, 1, 0, "stale")) };
            Assert.Empty(await Contexts.DrainAsync(Connector(), Context(empty, RedditConnector.PostsOperation, new() { ["subreddit"] = "fika" })));
            Assert.Single(empty.Calls); // an empty page the end, whatever its after

            var search = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, Listing(PostJson, 1, 3, null)) };
            await Contexts.DrainAsync(Connector(), Context(search, RedditConnector.SearchOperation, new() { ["query"] = "fika & kanelbulle", ["sort"] = "new", ["time"] = "month" }));
            Assert.Equal($"{Api}/search?q=fika%20%26%20kanelbulle&type=link&sort=new&t=month&limit=100&raw_json=1", search.Calls[^1].Url); // all of Reddit
            await Contexts.DrainAsync(Connector(), Context(search, RedditConnector.SearchOperation, new() { ["query"] = "fika", ["subreddit"] = "/r/sweden" }));
            Assert.Equal($"{Api}/r/sweden/search?q=fika&restrict_sr=true&type=link&sort=relevance&limit=100&raw_json=1", search.Calls[^1].Url);
        }

        [Fact]
        public async Task A_post_is_got_submitted_commented_edited_and_deleted_and_its_comments_read_depth_first()
        {
            const string Comment = "{\"id\":\"c1\",\"name\":\"t1_c1\",\"link_id\":\"t3_p1\",\"parent_id\":\"t3_p1\",\"subreddit\":\"fika\",\"author\":\"lin\",\"body\":\"Yes & no\",\"score\":3,\"depth\":0,\"created_utc\":1760259660,\"permalink\":\"/r/fika/comments/p1/fika/c1/\"";
            var tree = "[" + Listing(PostJson, 1, 1, null) + ",{\"kind\":\"Listing\",\"data\":{\"after\":null,\"children\":["
                + "{\"kind\":\"t1\",\"data\":" + Comment + ",\"replies\":{\"kind\":\"Listing\",\"data\":{\"children\":[{\"kind\":\"t1\",\"data\":{\"id\":\"c2\",\"name\":\"t1_c2\",\"parent_id\":\"t1_c1\",\"body\":\"Fika!\",\"depth\":1,\"replies\":\"\"}},{\"kind\":\"more\",\"data\":{\"count\":4,\"children\":[\"c5\"]}}]}}}},"
                + "{\"kind\":\"t1\",\"data\":{\"id\":\"c3\",\"name\":\"t1_c3\",\"parent_id\":\"t3_p1\",\"body\":\"Third\",\"depth\":0,\"replies\":\"\"}},{\"kind\":\"more\",\"data\":{\"count\":9}}]}}]";
            var reddit = new Reddit
            {
                Answer = (method, url, _) =>
                    url.Contains("/by_id/", StringComparison.Ordinal) ? (HttpStatusCode.OK, Listing(PostJson, 1, 1, null))
                    : url.Contains("/comments/", StringComparison.Ordinal) ? (HttpStatusCode.OK, tree)
                    : url.EndsWith("/api/submit", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"json":{"errors":[],"data":{"url":"https://www.reddit.com/r/fika/comments/p9/fika/","drafts_count":0,"id":"p9","name":"t3_p9"}}}""")
                    : url.EndsWith("/api/comment", StringComparison.Ordinal) || url.EndsWith("/api/editusertext", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"json\":{\"errors\":[],\"data\":{\"things\":[{\"kind\":\"t1\",\"data\":" + Comment + "}}]}}}")
                    : (HttpStatusCode.OK, "{}"),
            };
            (string, string, string?) Last() => (reddit.Calls[^1].Method.Method, reddit.Calls[^1].Url, reddit.Calls[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(reddit, operation, parameters));

            Assert.Equal("t3_p1", (string?)Assert.Single(await Run(RedditConnector.GetPostOperation, new() { ["post"] = "p1" })).Fields["name"]);
            Assert.Equal(("GET", $"{Api}/by_id/t3_p1?raw_json=1", (string?)null), Last()); // a bare id read as a post's
            await Run(RedditConnector.GetPostOperation, new() { ["post"] = "t3_1abc2de" });
            Assert.Equal($"{Api}/by_id/t3_1abc2de?raw_json=1", Last().Item2);

            var comments = await Run(RedditConnector.CommentsOperation, new() { ["post"] = "t3_p1", ["sort"] = "Top" });
            Assert.Equal(("GET", $"{Api}/comments/p1?sort=top&limit=500&raw_json=1", (string?)null), Last());
            Assert.Equal(new[] { "c1", "c2", "c3" }, comments.Select(comment => (string?)comment.Fields["id"])); // depth first, no "more" stubs
            Assert.Equal(("comment", "t1_c1", "t3_p1", "t3_p1", "lin", "Yes & no", 3L, 0L, "2025-10-12T09:01:00Z", "https://www.reddit.com/r/fika/comments/p1/fika/c1/"),
                ((string?)comments[0].Fields["kind"], (string?)comments[0].Fields["name"], (string?)comments[0].Fields["post"], (string?)comments[0].Fields["parent"], (string?)comments[0].Fields["author"], (string?)comments[0].Fields["text"], (long?)comments[0].Fields["score"], (long?)comments[0].Fields["depth"], (string?)comments[0].Fields["created"], (string?)comments[0].Fields["permalink"]));
            Assert.Equal(("t1_c1", 1L), ((string?)comments[1].Fields["parent"], (long?)comments[1].Fields["depth"]));
            Assert.Equal(2, (await Run(RedditConnector.CommentsOperation, new() { ["post"] = "p1", ["max"] = "2" })).Count);
            Assert.Equal($"{Api}/comments/p1?limit=2&raw_json=1", Last().Item2);

            var made = Assert.Single(await Run(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "Fika & bullar", ["text"] = "Line one\nLine two", ["spoiler"] = "TRUE", ["send_replies"] = "false", ["flair"] = "a1b2c3d4-0000-1111-2222-333344445555" }));
            Assert.Equal(("POST", $"{Api}/api/submit", (string?)"api_type=json&sr=fika&title=Fika+%26+bullar&kind=self&text=Line+one%0ALine+two&spoiler=true&sendreplies=false&flair_id=a1b2c3d4-0000-1111-2222-333344445555"), Last());
            Assert.Equal(("p9", "t3_p9", "fika", "https://www.reddit.com/r/fika/comments/p9/fika/"), ((string?)made.Fields["id"], (string?)made.Fields["name"], (string?)made.Fields["subreddit"], (string?)made.Fields["url"]));
            await Run(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "A link", ["url"] = "https://example.com/fika?a=1&b=2", ["nsfw"] = "true", ["resubmit"] = "true" });
            Assert.Equal("api_type=json&sr=fika&title=A+link&kind=link&url=https%3A%2F%2Fexample.com%2Ffika%3Fa%3D1%26b%3D2&resubmit=true&nsfw=true&sendreplies=true", Last().Item3);
            await Run(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "Again", ["url"] = "https://example.com/fika" });
            Assert.Equal("api_type=json&sr=fika&title=Again&kind=link&url=https%3A%2F%2Fexample.com%2Ffika&resubmit=false&sendreplies=true", Last().Item3); // not resubmitted unless asked
            await Run(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "Empty" });
            Assert.Equal("api_type=json&sr=fika&title=Empty&kind=self&text=&sendreplies=true", Last().Item3); // a title alone: an empty text post

            var said = Assert.Single(await Run(RedditConnector.CommentOperation, new() { ["on"] = "t3_p1", ["text"] = "Fika & more" }));
            Assert.Equal(("POST", $"{Api}/api/comment", (string?)"api_type=json&thing_id=t3_p1&text=Fika+%26+more"), Last());
            Assert.Equal(("comment", "t1_c1"), ((string?)said.Fields["kind"], (string?)said.Fields["name"]));
            await Run(RedditConnector.CommentOperation, new() { ["on"] = "t1_c1", ["text"] = "Reply" });
            Assert.Equal("api_type=json&thing_id=t1_c1&text=Reply", Last().Item3);
            await Run(RedditConnector.EditOperation, new() { ["thing"] = "t1_c1", ["text"] = "Edited" });
            var postEdit = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"json\":{\"errors\":[],\"data\":{\"things\":[" + PostJson(9) + "]}}}") };
            var edited = Assert.Single(await Contexts.DrainAsync(Connector(), Context(postEdit, RedditConnector.EditOperation, new() { ["thing"] = "t3_p9", ["text"] = "Edited" })));
            Assert.Equal(("post", "t3_p9"), ((string?)edited.Fields["kind"], (string?)edited.Fields["name"])); // a text post's edit read back as a post
            Assert.Equal(("POST", $"{Api}/api/editusertext", (string?)"api_type=json&thing_id=t1_c1&text=Edited"), Last());
            var gone = Assert.Single(await Run(RedditConnector.DeleteOperation, new() { ["thing"] = "t3_p9" }));
            Assert.Equal(("POST", $"{Api}/api/del", (string?)"id=t3_p9"), Last());
            Assert.Equal(("t3_p9", true), ((string?)gone.Fields["thing"], (bool?)gone.Fields["done"]));
        }

        [Fact]
        public async Task The_account_a_user_and_their_activity_a_subreddit_its_rules_and_subreddits_are_read()
        {
            const string User = """{"name":"ada","id":"u1","link_karma":10,"comment_karma":32,"total_karma":42,"created_utc":1500000000.0,"has_verified_email":true,"is_mod":false}""";
            const string Sub = """{"kind":"t5","data":{"display_name":"fika","title":"Fika!","public_description":"Coffee & cake","subscribers":12345,"over18":false,"created_utc":1400000000,"url":"/r/fika/"}}""";
            var reddit = new Reddit
            {
                Answer = (_, url, _) =>
                    url.Contains("/api/v1/me", StringComparison.Ordinal) ? (HttpStatusCode.OK, User)
                    : url.Contains("/about/rules", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"rules":[{"kind":"all","description":"Be kind","short_name":"Kindness","violation_reason":"Unkind","created_utc":1400000000.0,"priority":0}]}""")
                    : url.Contains("/user/ada/about", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"kind\":\"t2\",\"data\":" + User + "}")
                    : url.Contains("/r/fika/about", StringComparison.Ordinal) ? (HttpStatusCode.OK, Sub)
                    : url.Contains("/subreddits/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"kind\":\"Listing\",\"data\":{\"after\":null,\"children\":[" + Sub + "]}}")
                    : (HttpStatusCode.OK, "{\"kind\":\"Listing\",\"data\":{\"after\":null,\"children\":[" + PostJson(1) + ",{\"kind\":\"t1\",\"data\":{\"id\":\"c1\",\"name\":\"t1_c1\",\"body\":\"Hi\"}}]}}"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(reddit, operation, parameters));

            var me = Assert.Single(await Run(RedditConnector.MeOperation, new())).Fields;
            Assert.Equal($"{Api}/api/v1/me?raw_json=1", reddit.Calls[^1].Url);
            Assert.Equal(("ada", "u1", 10L, 32L, 42L, "2017-07-14T02:40:00Z", true, false), ((string?)me["name"], (string?)me["id"], (long?)me["link_karma"], (long?)me["comment_karma"], (long?)me["total_karma"], (string?)me["created"], (bool?)me["verified"], (bool?)me["is_mod"]));
            Assert.Equal("ada", (string?)Assert.Single(await Run(RedditConnector.UserOperation, new() { ["user"] = "u/ada" })).Fields["name"]);
            Assert.Equal($"{Api}/user/ada/about?raw_json=1", reddit.Calls[^1].Url);
            var activity = await Run(RedditConnector.UserActivityOperation, new() { ["user"] = "/u/ada", ["what"] = "Submitted" });
            Assert.Equal($"{Api}/user/ada/submitted?limit=100&raw_json=1", reddit.Calls[^1].Url);
            Assert.Equal(new[] { "post", "comment" }, activity.Select(record => (string?)record.Fields["kind"])); // posts and comments each its own record
            await Run(RedditConnector.UserActivityOperation, new() { ["user"] = "ada" });
            Assert.Equal($"{Api}/user/ada/overview?limit=100&raw_json=1", reddit.Calls[^1].Url);

            var sub = Assert.Single(await Run(RedditConnector.SubredditOperation, new() { ["subreddit"] = "fika" })).Fields;
            Assert.Equal($"{Api}/r/fika/about?raw_json=1", reddit.Calls[^1].Url);
            Assert.Equal(("fika", "Fika!", "Coffee & cake", 12345L, false, "2014-05-13T16:53:20Z", "https://www.reddit.com/r/fika/"), ((string?)sub["name"], (string?)sub["title"], (string?)sub["description"], (long?)sub["subscribers"], (bool?)sub["nsfw"], (string?)sub["created"], (string?)sub["url"]));
            var rule = Assert.Single(await Run(RedditConnector.RulesOperation, new() { ["subreddit"] = "fika" })).Fields;
            Assert.Equal($"{Api}/r/fika/about/rules?raw_json=1", reddit.Calls[^1].Url);
            Assert.Equal(("fika", 0L, "Kindness", "Be kind", "all", "Unkind", "2014-05-13T16:53:20Z"), ((string?)rule["subreddit"], (long?)rule["priority"], (string?)rule["name"], (string?)rule["description"], (string?)rule["kind"], (string?)rule["violation_reason"], (string?)rule["created"]));
            Assert.Equal("fika", (string?)Assert.Single(await Run(RedditConnector.SubredditsOperation, new() { ["query"] = "fika & cake" })).Fields["name"]);
            Assert.Equal($"{Api}/subreddits/search?q=fika%20%26%20cake&limit=100&raw_json=1", reddit.Calls[^1].Url);
            await Run(RedditConnector.SubredditsOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/subreddits/popular?limit=5&raw_json=1", reddit.Calls[^1].Url); // no words: the popular ones
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_reddits_refusals_are_its_words()
        {
            var reddit = new Reddit();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(reddit, operation, parameters)))).Message;

            Assert.Equal("Give the subreddit - its name.", await Refused(RedditConnector.PostsOperation, new()));
            Assert.Equal("'r/fi ka' is not a subreddit - its name, fika.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "r/fi ka", ["title"] = "t" }));
            Assert.Equal("'_x' is not a subreddit - its name, fika.", await Refused(RedditConnector.SearchOperation, new() { ["query"] = "q", ["subreddit"] = "_x" }));
            Assert.Equal("Give the post - its id.", await Refused(RedditConnector.GetPostOperation, new()));
            Assert.Equal("'t1_c1' is not a post - its id, 1abc2de, or t3_1abc2de.", await Refused(RedditConnector.CommentsOperation, new() { ["post"] = "t1_c1" }));
            Assert.Equal("'P1' is not a post - its id, 1abc2de, or t3_1abc2de.", await Refused(RedditConnector.GetPostOperation, new() { ["post"] = "P1" }));
            Assert.Equal("Give the post or comment - t3_ or t1_ and its id.", await Refused(RedditConnector.CommentOperation, new() { ["text"] = "x" }));
            Assert.Equal("'p1' is not a post or comment - t3_ or t1_ and its id, t3_1abc2de.", await Refused(RedditConnector.DeleteOperation, new() { ["thing"] = "p1" }));
            Assert.Equal("'t5_x' is not a post or comment - t3_ or t1_ and its id, t3_1abc2de.", await Refused(RedditConnector.EditOperation, new() { ["thing"] = "t5_x", ["text"] = "x" }));
            Assert.Equal("Give the text.", await Refused(RedditConnector.CommentOperation, new() { ["on"] = "t3_p1", ["text"] = " " }));
            Assert.Equal("The text is longer than 10,000 characters.", await Refused(RedditConnector.EditOperation, new() { ["thing"] = "t1_c1", ["text"] = new string('t', 10001) }));
            Assert.Equal("Give the user - its name.", await Refused(RedditConnector.UserOperation, new()));
            Assert.Equal("'u/a' is not a user - its name, spez.", await Refused(RedditConnector.UserActivityOperation, new() { ["user"] = "u/a" }));
            Assert.Equal("'gilded' is not a user's activity - overview, submitted or comments.", await Refused(RedditConnector.UserActivityOperation, new() { ["user"] = "ada", ["what"] = "gilded" }));
            Assert.Equal("Give the post's title.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika" }));
            Assert.Equal("The title is longer than 300 characters.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = new string('t', 301) }));
            Assert.Equal("The title has a line break - one line only.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "a\nb" }));
            Assert.Equal("A post is text or a link - not both.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["text"] = "x", ["url"] = "https://example.com" }));
            Assert.Equal("'example.com' is not a link - https://example.com/fika.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["url"] = "example.com" }));
            Assert.Equal("'ftp://example.com/fika' is not a link - https://example.com/fika.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["url"] = "ftp://example.com/fika" }));
            Assert.Equal("'yes' is not true or false (nsfw).", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["nsfw"] = "yes" }));
            Assert.Equal("'no' is not true or false (replies to inbox).", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["send_replies"] = "no" }));
            Assert.Equal("'Recipe' is not a flair template's id.", await Refused(RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t", ["flair"] = "Recipe" }));
            Assert.Equal("'best' is not a sort - hot, new, top, rising or controversial.", await Refused(RedditConnector.PostsOperation, new() { ["subreddit"] = "fika", ["sort"] = "best" }));
            Assert.Equal("A time is for top and controversial only.", await Refused(RedditConnector.PostsOperation, new() { ["subreddit"] = "fika", ["time"] = "week" }));
            Assert.Equal("'decade' is not a time - hour, day, week, month, year or all.", await Refused(RedditConnector.PostsOperation, new() { ["subreddit"] = "fika", ["sort"] = "top", ["time"] = "decade" }));
            Assert.Equal("Give the words to search for.", await Refused(RedditConnector.SearchOperation, new()));
            Assert.Equal("The words has a line break - one line only.", await Refused(RedditConnector.SearchOperation, new() { ["query"] = "a\nb" }));
            Assert.Equal("'best' is not an order - relevance, hot, top, new or comments.", await Refused(RedditConnector.SearchOperation, new() { ["query"] = "q", ["sort"] = "best" }));
            Assert.Equal("'best' is not an order - confidence, top, new, controversial, old or qa.", await Refused(RedditConnector.CommentsOperation, new() { ["post"] = "p1", ["sort"] = "best" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(RedditConnector.PostsOperation, new() { ["subreddit"] = "fika", ["max"] = "0" }));
            Assert.StartsWith("Reddit has no operation 'vote' - me, submit, posts,", await Refused("vote", new()));
            Assert.Empty(reddit.Requests); // nothing sent - not even a token asked for

            var refused = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, """{"json":{"errors":[["SUBREDDIT_NOEXIST","that subreddit doesn't exist","sr"],["RATELIMIT","you are doing that too much",""]]}}""") };
            Assert.Equal("Reddit refused it: SUBREDDIT_NOEXIST - that subreddit doesn't exist - sr; RATELIMIT - you are doing that too much", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refused, RedditConnector.SubmitOperation, new() { ["subreddit"] = "fika", ["title"] = "t" })))).Message);
            var forbidden = new Reddit { Answer = (_, _, _) => (HttpStatusCode.Forbidden, """{"reason":"private","message":"Forbidden","error":403}""") };
            Assert.Equal("HTTP 403: Forbidden (private)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(forbidden, RedditConnector.SubredditOperation, new() { ["subreddit"] = "fika" })))).Message);
            var missing = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, Listing(PostJson, 1, 0, null)) };
            Assert.Equal("No post has the id t3_p404.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, RedditConnector.GetPostOperation, new() { ["post"] = "p404" })))).Message);
            var silent = new Reddit { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "") };
            Assert.Equal("HTTP 401.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, RedditConnector.MeOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var reddit = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, """{"name":"ada","total_karma":12345}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(reddit), Api, settings: Settings, credentials: Credentials));
            Assert.Equal((true, "Signed in as u/ada (12,345 karma)."), (tested.Success, tested.Message));
            Assert.Equal($"{Api}/api/v1/me", reddit.Calls[0].Url);
            var agentless = new Reddit { Answer = (_, _, _) => (HttpStatusCode.OK, """{"name":"ada"}""") };
            Assert.Equal("Signed in as u/ada.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(agentless), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://www.reddit.example.test/api/v1/access_token" }, credentials: Credentials))).Message);
            Assert.All(agentless.Requests, request => Assert.Equal(RedditConnector.DefaultUserAgent, request.Agent)); // the default agent, the token call too
            var bad = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Reddit()), Api, settings: new Dictionary<string, string> { ["user_agent"] = "a\nb" }, credentials: Credentials));
            Assert.Equal((false, "The user agent is one line of up to 200 characters."), (bad.Success, bad.Message));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "reddit"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing }, Connector().Categories);
            Assert.Equal(14, Connector().Operations.Count);
        }
    }
}
