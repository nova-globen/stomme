#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the rest of n8n's Mattermost node, without a server: every id, name, emoji, address, sort and choice held to
    /// Mattermost's shape before anything is sent; the token a bearer; a team by its name and a user by its username looked up;
    /// a channel made, archived, restored, joined, searched and counted, its members as users page by page; a reaction only as
    /// the token's own user, its removal reading first whether there was one; a user signing in through a service made, one
    /// signing in by e-mail refused - a password is never a step's parameter - and invited instead; users listed, found by
    /// e-mail and by id; Mattermost's messages; the message posted as before.
    /// </summary>
    public sealed class MattermostApiTests
    {
        private const string Address = "https://chat.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "mm-token-1" };

        private const string Me = "1e5cbdkwqprtjfz44u15gwyx3e";

        private const string Ada = "xgn73yhyaj86ines6kiszf1nio";

        private const string Team = "s8qp1zum7tyt7d5gg5psrqb1fw";

        private const string Channel = "57nnr9fnijnztf791ka898ey6o";

        private const string Post = "4xetpu76djyy8rxt4p6zzpcx7e";

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Server : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Server(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        // Mattermost as the probe found it: the token's user, the team kafe, the user ada, an answer for anything else.
        private static Server Mattermost(Func<Sent, (HttpStatusCode, string)?>? special = null) => new(sent =>
            special?.Invoke(sent) ?? (sent.Url.EndsWith("/users/me", StringComparison.Ordinal) ? (HttpStatusCode.OK, $"{{\"id\":\"{Me}\",\"username\":\"kafe\"}}")
                : sent.Url.EndsWith("/teams/name/kafe", StringComparison.Ordinal) ? (HttpStatusCode.OK, $"{{\"id\":\"{Team}\",\"name\":\"kafe\"}}")
                : sent.Url.EndsWith("/users/username/ada", StringComparison.Ordinal) ? (HttpStatusCode.OK, $"{{\"id\":\"{Ada}\",\"username\":\"ada\"}}")
                : sent.Method == "DELETE" ? (HttpStatusCode.OK, "{\"status\":\"OK\"}")
                : (HttpStatusCode.OK, "{\"id\":\"made\"}")));

        private static Task<List<ConnectorRecord>> Run(Server server, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(server), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api/v4/" + route;

        [Fact]
        public void The_message_comes_first_and_n8ns_other_operations_follow_it()
        {
            var names = ((IConnectorExtractor)new MattermostConnector()).Operations.Select(operation => operation.Name).ToList();
            Assert.Equal(19, names.Count);
            Assert.Equal(new[] { "message", "message_ephemeral", "message_delete", "channel_create" }, names.Take(4)); // a send still names the first
            Assert.Equal(new[] { "user_by_email", "users_by_id", "user_invite" }, names.TakeLast(3));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_mattermosts_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var server = Mattermost();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(server, operation, parameters, endpoint));
                Assert.Empty(server.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("Mattermost has no operation 'channel_update' - message, and message_ephemeral, message_delete", await Refused("channel_update", new()));
            Assert.Equal("'nosuch' is not a Mattermost id - 26 lowercase letters and digits (channel).", await Refused("channel_statistics", new() { ["channel"] = "nosuch" })); // Mattermost: a 400, not a 404
            Assert.Equal("Give the post (post).", await Refused("message_delete", new()));
            Assert.Equal("'Kassa Lund' is not a channel's name - lowercase letters, digits, - and _, at most 64: bestallningar (name).", await Refused("channel_create", new() { ["team"] = Team, ["name"] = "Kassa Lund", ["display_name"] = "Kassa" })); // Mattermost: a 400
            Assert.Equal("'secret' is not public or private (type).", await Refused("channel_create", new() { ["team"] = Team, ["name"] = "kassa", ["display_name"] = "Kassa", ["type"] = "secret" }));
            Assert.Equal("A display name is one line of at most 64 characters (display_name).", await Refused("channel_create", new() { ["team"] = Team, ["name"] = "kassa", ["display_name"] = "Kassa\nLund" }));
            Assert.Equal("'kafé lund' is not a team's id or name - kafe (team).", await Refused("channel_search", new() { ["team"] = "kafé lund", ["term"] = "best" }));
            Assert.Equal("'☕' is not an emoji's name - coffee, or :coffee: (emoji).", await Refused("reaction_create", new() { ["post"] = Post, ["emoji"] = "☕" }));
            Assert.Equal("A user signing in by e-mail needs a password, and a password is never a step's parameter here - invite them with user_invite, or make them with their sign-in service (auth_service).", await Refused("user_create", new() { ["username"] = "ada", ["email"] = "ada@kafe.example", ["auth_service"] = "email" }));
            Assert.Equal("'github' is not gitlab, google, ldap, office365, saml or openid (auth_service).", await Refused("user_create", new() { ["username"] = "ada", ["email"] = "ada@kafe.example", ["auth_service"] = "github", ["auth_data"] = "1" }));
            Assert.Equal("'Ada' is not a username - a lowercase letter, then up to 63 letters, digits, . - or _ (username).", await Refused("user_create", new() { ["username"] = "Ada", ["email"] = "ada@kafe.example", ["auth_service"] = "ldap", ["auth_data"] = "ada" }));
            Assert.Equal("'ada.kafe.example' is not an e-mail address (email).", await Refused("user_by_email", new() { ["email"] = "ada.kafe.example" }));
            Assert.Equal("'status' sorts no list here - username; created_at or last_activity_at with in_team; status with in_channel (sort).", await Refused("users", new() { ["sort"] = "status" })); // Mattermost: a 400
            Assert.Equal("'lastActivityAt' sorts no list here - username; created_at or last_activity_at with in_team; status with in_channel (sort).", await Refused("users", new() { ["in_channel"] = Channel, ["sort"] = "lastActivityAt" }));
            Assert.Equal("Mattermost lists those not in a channel only within a team - give in_team too (not_in_channel).", await Refused("users", new() { ["not_in_channel"] = Channel }));
            Assert.Equal("Give 1 to 20 e-mail addresses, by commas (emails).", await Refused("user_invite", new() { ["team"] = Team, ["emails"] = " , " }));
            Assert.Equal("'igår' is not a moment - 2026-10-21T08:00, or a day (since).", await Refused("users_by_id", new() { ["users"] = Ada, ["since"] = "igår" }));
            Assert.Equal("'kanske' is not true or false (resolve).", await Refused("channel_members", new() { ["channel"] = Channel, ["resolve"] = "kanske" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("reactions", new() { ["post"] = Post, ["max"] = "0" }));
            Assert.Equal("Give the server's address - https://chat.kafe.example, with no query.", await Refused("reactions", new() { ["post"] = Post }, "chat.kafe.example"));
            Assert.Equal("The Mattermost connection has no access token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(Mattermost()), Address, "reactions", new Dictionary<string, string> { ["post"] = Post }, new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_channel_is_made_by_its_teams_name_archived_restored_joined_and_counted()
        {
            var server = Mattermost();
            await Run(server, "channel_create", new() { ["team"] = "kafe", ["name"] = "bestallningar", ["display_name"] = "Beställningar", ["type"] = "Private", ["purpose"] = "Kassans beställningar" }, Address + "/api/v4");
            Assert.Equal(new[] { ("GET", Api("teams/name/kafe")), ("POST", Api("channels")) }, server.Requests.Select(sent => (sent.Method, sent.Url))); // the API's own address given: the server's
            Assert.Equal(($"{{\"team_id\":\"{Team}\",\"name\":\"bestallningar\",\"display_name\":\"Beställningar\",\"type\":\"P\",\"purpose\":\"Kassans beställningar\"}}", "Bearer mm-token-1"), (server.Requests[1].Body, server.Requests[1].Authorization));

            var archived = Mattermost();
            Assert.Equal(((string?)Channel, (bool?)true), ((string?)Assert.Single(await Run(archived, "channel_delete", new() { ["channel"] = Channel })).Fields["id"], (bool?)(await Run(archived, "channel_delete", new() { ["channel"] = Channel }))[0].Fields["archived"]));
            await Run(archived, "channel_restore", new() { ["channel"] = Channel });
            await Run(archived, "channel_add_user", new() { ["channel"] = Channel, ["user"] = "@ada" });
            await Run(archived, "channel_statistics", new() { ["channel"] = Channel });
            Assert.Equal(new[] { ("DELETE", Api($"channels/{Channel}"), (string?)null), ("DELETE", Api($"channels/{Channel}"), null), ("POST", Api($"channels/{Channel}/restore"), null), ("GET", Api("users/username/ada"), null), ("POST", Api($"channels/{Channel}/members"), $"{{\"user_id\":\"{Ada}\"}}"), ("GET", Api($"channels/{Channel}/stats"), null) },
                archived.Requests.Select(sent => (sent.Method, sent.Url, sent.Body)));

            var search = Mattermost(sent => sent.Url.EndsWith("/channels/search", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]") : null);
            Assert.Equal(new[] { "a", "b" }, (await Run(search, "channel_search", new() { ["team"] = Team, ["term"] = "best", ["max"] = "2" })).Select(item => (string?)item.Fields["id"]));
            Assert.Equal(("POST", Api($"teams/{Team}/channels/search"), "{\"term\":\"best\"}"), (search.Requests[0].Method, search.Requests[0].Url, search.Requests[0].Body)); // a team's id as it is
        }

        [Fact]
        public async Task A_channels_members_go_page_by_page_and_as_users_when_asked()
        {
            var full = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => $"{{\"user_id\":\"u{i:D25}\"}}")) + "]";
            var server = Mattermost(sent => sent.Url.Contains("/members?page=0", StringComparison.Ordinal) ? (HttpStatusCode.OK, full)
                : sent.Url.Contains("/members?page=1", StringComparison.Ordinal) ? (HttpStatusCode.OK, $"[{{\"user_id\":\"{Ada}\"}}]") : null);
            Assert.Equal(201, (await Run(server, "channel_members", new() { ["channel"] = Channel, ["max"] = "500" })).Count);
            Assert.Equal(new[] { Api($"channels/{Channel}/members?page=0&per_page=200"), Api($"channels/{Channel}/members?page=1&per_page=200") }, server.Requests.Select(sent => sent.Url)); // a short page is the last
            Assert.Equal(5, (await Run(Mattermost(sent => sent.Url.Contains("/members?page=0", StringComparison.Ordinal) ? (HttpStatusCode.OK, full) : null), "channel_members", new() { ["channel"] = Channel, ["max"] = "5" })).Count);

            var users = Mattermost(sent => sent.Url.Contains("/members?", StringComparison.Ordinal) ? (HttpStatusCode.OK, $"[{{\"user_id\":\"{Me}\"}},{{\"user_id\":\"{Ada}\"}}]")
                : sent.Url.EndsWith("/users/ids", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[{\"username\":\"kafe\"},{\"username\":\"ada\"}]") : null);
            Assert.Equal(new[] { "kafe", "ada" }, (await Run(users, "channel_members", new() { ["channel"] = Channel, ["resolve"] = "true" })).Select(item => (string?)item.Fields["username"]));
            Assert.Equal(($"[\"{Me}\",\"{Ada}\"]", "POST"), (users.Requests[1].Body, users.Requests[1].Method));
        }

        [Fact]
        public async Task A_reaction_is_the_tokens_own_and_its_removal_says_whether_there_was_one()
        {
            var server = Mattermost();
            await Run(server, "reaction_create", new() { ["post"] = Post, ["emoji"] = ":Coffee:" });
            Assert.Equal(("POST", Api("reactions"), $"{{\"user_id\":\"{Me}\",\"post_id\":\"{Post}\",\"emoji_name\":\"coffee\"}}"), (server.Requests[^1].Method, server.Requests[^1].Url, server.Requests[^1].Body));
            var other = Mattermost();
            Assert.Equal("Mattermost saves a reaction only as the token's own user - leave the user out (user).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(other, "reaction_create", new() { ["post"] = Post, ["emoji"] = "tada", ["user"] = "ada" }))).Message); // Mattermost: a 403
            Assert.DoesNotContain(other.Requests, sent => sent.Method == "POST");

            var there = Mattermost(sent => sent.Url.EndsWith("/reactions", StringComparison.Ordinal) && sent.Method == "GET" ? (HttpStatusCode.OK, $"[{{\"user_id\":\"{Me}\",\"emoji_name\":\"coffee\"}}]") : null);
            Assert.Equal(true, (bool?)Assert.Single(await Run(there, "reaction_delete", new() { ["post"] = Post, ["emoji"] = "coffee" })).Fields["deleted"]);
            Assert.Equal(("DELETE", Api($"users/{Me}/posts/{Post}/reactions/coffee")), (there.Requests[^1].Method, there.Requests[^1].Url));
            var none = Mattermost(sent => sent.Url.EndsWith("/reactions", StringComparison.Ordinal) ? (HttpStatusCode.OK, "null") : null);
            Assert.Equal(false, (bool?)Assert.Single(await Run(none, "reaction_delete", new() { ["post"] = Post, ["emoji"] = "coffee" })).Fields["deleted"]); // Mattermost answers OK either way
            Assert.DoesNotContain(none.Requests, sent => sent.Method == "DELETE");
            Assert.Empty(await Run(Mattermost(sent => sent.Url.EndsWith("/reactions", StringComparison.Ordinal) ? (HttpStatusCode.OK, "null") : null), "reactions", new() { ["post"] = Post })); // none answers null
        }

        [Fact]
        public async Task Users_are_made_through_a_service_invited_listed_and_found()
        {
            var server = Mattermost();
            await Run(server, "user_create", new() { ["username"] = "bo", ["email"] = "bo@kafe.example", ["auth_service"] = "LDAP", ["auth_data"] = "bo", ["first_name"] = "Bo" });
            Assert.Equal("{\"username\":\"bo\",\"email\":\"bo@kafe.example\",\"auth_service\":\"ldap\",\"auth_data\":\"bo\",\"first_name\":\"Bo\"}", server.Requests[^1].Body);
            await Run(server, "user_invite", new() { ["team"] = "kafe", ["emails"] = "ada@kafe.example, bo@kafe.example, ADA@kafe.example" });
            Assert.Equal(("POST", Api($"teams/{Team}/invite/email"), "[\"ada@kafe.example\",\"bo@kafe.example\"]"), (server.Requests[^1].Method, server.Requests[^1].Url, server.Requests[^1].Body));
            await Run(server, "user_deactivate", new() { ["user"] = "ada" });
            Assert.Equal(("DELETE", Api($"users/{Ada}")), (server.Requests[^1].Method, server.Requests[^1].Url));
            await Run(server, "user_by_email", new() { ["email"] = "ed+kassa@kafe.example" });
            Assert.Equal(Api("users/email/ed%2Bkassa%40kafe.example"), server.Requests[^1].Url);
            var ids = Mattermost(sent => sent.Url.Contains("/users/ids", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[]") : null);
            await Run(ids, "users_by_id", new() { ["users"] = $"{Ada}, {Me}, {Ada}", ["since"] = "2026-10-01" });
            Assert.Equal((Api("users/ids?since=1790812800000"), $"[\"{Ada}\",\"{Me}\"]"), (ids.Requests[^1].Url, ids.Requests[^1].Body));

            var full = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => $"{{\"id\":\"u{i}\"}}")) + "]";
            var list = Mattermost(sent => sent.Url.Contains("/users?", StringComparison.Ordinal) ? (HttpStatusCode.OK, sent.Url.Contains("page=0", StringComparison.Ordinal) ? full : "[{\"id\":\"last\"}]") : null);
            Assert.Equal(201, (await Run(list, "users", new() { ["in_team"] = "kafe", ["not_in_channel"] = Channel, ["sort"] = "lastActivityAt", ["max"] = "1000" })).Count);
            Assert.Equal(new[] { Api($"users?in_team={Team}&not_in_channel={Channel}&sort=last_activity_at&page=0&per_page=200"), Api($"users?in_team={Team}&not_in_channel={Channel}&sort=last_activity_at&page=1&per_page=200") }, list.Requests.Where(sent => sent.Url.Contains("/users?", StringComparison.Ordinal)).Select(sent => sent.Url));
            var byName = Mattermost(sent => sent.Url.Contains("/users?", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[]") : null);
            await Run(byName, "users", new() { ["sort"] = "username" });
            Assert.Equal(Api("users?page=0&per_page=200"), Assert.Single(byName.Requests).Url); // username is Mattermost's own order
        }

        [Fact]
        public async Task A_message_to_one_user_and_a_post_deleted_and_the_message_posted_as_before()
        {
            var server = Mattermost();
            await Run(server, "message_ephemeral", new() { ["channel"] = Channel, ["user"] = Ada, ["text"] = "Bara för dig" });
            Assert.Equal(("POST", Api("posts/ephemeral"), $"{{\"user_id\":\"{Ada}\",\"post\":{{\"channel_id\":\"{Channel}\",\"message\":\"Bara för dig\"}}}}"), (server.Requests[^1].Method, server.Requests[^1].Url, server.Requests[^1].Body));
            Assert.Equal(true, (bool?)Assert.Single(await Run(server, "message_delete", new() { ["post"] = Post })).Fields["deleted"]);

            var posted = Mattermost(sent => (HttpStatusCode.Created, $"{{\"id\":\"{Post}\",\"channel_id\":\"{Channel}\"}}"));
            var sent = Assert.Single(await Run(posted, "message", new() { ["to"] = Channel, ["text"] = "Ny beställning" })).Fields;
            Assert.Equal((Post, Channel), ((string?)sent["id"], (string?)sent["to"]));
            Assert.Equal(Api("posts"), Assert.Single(posted.Requests).Url);
        }

        [Fact]
        public async Task Mattermosts_messages_are_said_and_an_answer_that_is_not_its_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "channel_statistics", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Server(_ => (status, body)), operation, parameters ?? new() { ["channel"] = Channel }))).Message;

            Assert.Equal("Mattermost answered HTTP 404: Unable to get the post.", await Failed(HttpStatusCode.NotFound, "{\"id\":\"app.post.get.app_error\",\"message\":\"Unable to get the post.\",\"status_code\":404}"));
            Assert.Equal("Mattermost answered HTTP 501: Email invitations are disabled.", await Failed((HttpStatusCode)501, "{\"id\":\"api.team.invite_members.disabled.app_error\",\"message\":\"Email invitations are disabled.\",\"status_code\":501}"));
            Assert.Equal("Mattermost answered HTTP 401: Invalid or expired session, please login again. - check the access token.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"Invalid or expired session, please login again.\"}"));
            Assert.Equal("Mattermost answered HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));
            Assert.Equal("Mattermost answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Mattermost</html>"));
            Assert.Equal("Mattermost answered without the item - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]"));
            Assert.Equal("Mattermost answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"id\":\"x\"}", "reactions", new() { ["post"] = Post }));
        }
    }

    /// <summary>
    /// BL-229 - the rest of n8n's Mattermost node LIVE, against this machine's throwaway Mattermost Team Edition 10.11
    /// (scripts/mattermost-test-server.ps1) - fail-not-skip without it (D46): a channel made in the team kafe by its name, a user
    /// signing in through GitLab made and added to it, its members as users, the channel found and counted; a message posted, a
    /// reaction added as the token's own user and another's refused, removed - and a second removal saying there was none; a
    /// message to the user alone; users found by e-mail, by id and listed in the channel; an invitation Mattermost refuses
    /// (the throwaway sends no mail); the channel archived and restored, the post deleted, the user deactivated.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MattermostApiLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Token => new() { ["token"] = MattermostTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { new Uri(MattermostTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(Egress, MattermostTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Token, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        [Fact]
        public async Task A_channel_its_member_a_post_and_its_reactions_are_made_and_undone()
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var channel = await One("channel_create", new() { ["team"] = "kafe", ["name"] = "bestallningar-" + marker, ["display_name"] = "Beställningar " + marker });
            var channelId = (string)channel["id"]!;
            output.WriteLine($"channel {channelId}");
            var user = await One("user_create", new() { ["username"] = "ada" + marker, ["email"] = $"ada.{marker}@kafe.example", ["auth_service"] = "gitlab", ["auth_data"] = marker, ["first_name"] = "Ada" });
            var userId = (string)user["id"]!;
            try
            {
                Assert.Equal(("O", "gitlab"), ((string?)channel["type"], (string?)user["auth_service"]));
                using (var client = new System.Net.Http.HttpClient())
                {
                    // Into the team first, as a channel's member must be: n8n's node has no team operation.
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{MattermostTestEndpoint.Endpoint}/api/v4/teams/{(string)channel["team_id"]!}/members") { Content = new StringContent($"{{\"team_id\":\"{(string)channel["team_id"]!}\",\"user_id\":\"{userId}\"}}", Encoding.UTF8, "application/json") };
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MattermostTestEndpoint.Token);
                    (await client.SendAsync(request)).EnsureSuccessStatusCode();
                }

                await One("channel_add_user", new() { ["channel"] = channelId, ["user"] = "ada" + marker });
                Assert.Contains(await All("channel_members", new() { ["channel"] = channelId, ["resolve"] = "true" }), item => (string?)item.Fields["username"] == "ada" + marker);
                Assert.Contains(await All("channel_search", new() { ["team"] = "kafe", ["term"] = "bestallningar-" + marker }), item => (string?)item.Fields["id"] == channelId);
                Assert.Equal(2L, (long?)(await One("channel_statistics", new() { ["channel"] = channelId }))["member_count"]);
                Assert.Equal(userId, (string?)(await One("user_by_email", new() { ["email"] = $"ada.{marker}@kafe.example" }))["id"]);
                Assert.Equal("ada" + marker, (string?)(await One("users_by_id", new() { ["users"] = userId }))["username"]);
                Assert.Contains(await All("users", new() { ["in_channel"] = channelId, ["sort"] = "status" }), item => (string?)item.Fields["id"] == userId);

                var post = (string)(await One("message", new() { ["to"] = channelId, ["text"] = "Ny beställning " + marker }))["id"]!;
                Assert.Equal("coffee", (string?)(await One("reaction_create", new() { ["post"] = post, ["emoji"] = ":coffee:" }))["emoji_name"]);
                Assert.Equal("Mattermost saves a reaction only as the token's own user - leave the user out (user).", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("reaction_create", new() { ["post"] = post, ["emoji"] = "tada", ["user"] = userId }))).Message);
                Assert.Equal("coffee", (string?)Assert.Single(await All("reactions", new() { ["post"] = post })).Fields["emoji_name"]);
                Assert.Equal(true, (bool?)(await One("reaction_delete", new() { ["post"] = post, ["emoji"] = "coffee" }))["deleted"]);
                Assert.Equal(false, (bool?)(await One("reaction_delete", new() { ["post"] = post, ["emoji"] = "coffee" }))["deleted"]); // Mattermost said OK to this too
                Assert.Empty(await All("reactions", new() { ["post"] = post }));
                Assert.Equal(channelId, (string?)(await One("message_ephemeral", new() { ["channel"] = channelId, ["user"] = userId, ["text"] = "Bara för dig" }))["channel_id"]);
                Assert.Equal("Mattermost answered HTTP 501: Email invitations are disabled.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("user_invite", new() { ["team"] = "kafe", ["emails"] = $"fia.{marker}@kafe.example" }))).Message);

                Assert.Equal(true, (bool?)(await One("message_delete", new() { ["post"] = post }))["deleted"]);
                Assert.Equal("Mattermost answered HTTP 404: Unable to get the post.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("message_delete", new() { ["post"] = post }))).Message);
                Assert.Equal(true, (bool?)(await One("channel_delete", new() { ["channel"] = channelId }))["archived"]);
                Assert.Equal(0L, (long?)(await One("channel_restore", new() { ["channel"] = channelId }))["delete_at"]);
            }
            finally
            {
                Assert.Equal(true, (bool?)(await One("user_deactivate", new() { ["user"] = userId }))["deactivated"]);
                await One("channel_delete", new() { ["channel"] = channelId });
            }
        }
    }
}
