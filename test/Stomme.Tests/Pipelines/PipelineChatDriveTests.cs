#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Chat trigger, hosted by the site, DRIVEN over the real receiver on an installed SQLite host: a published
    /// pipeline's chat is a page any visitor opens (a session cookie of the chat's own, the welcome first); a message RUNS the
    /// pipeline and the reply is its last step's result, both kept in that visitor's conversation; another visitor sees only theirs;
    /// "New conversation" forgets it; a message too long is refused with the page again; a robot's is dropped. With
    /// Pipelines:Chats:Enabled off nothing is served.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineChatDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task A_published_chat_answers_each_visitor_in_a_conversation_of_their_own()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Chats"] = new JsonObject { ["Enabled"] = true } } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await SaveChatPipelineAsync(app, client, cookies, hostId, "help");
                using var visitor = GateDrive.NewClient(app, Host);
                using var other = GateDrive.NewClient(app, Host);

                // The page: the welcome first, a session cookie of this chat's own, every text encoded.
                var page = await visitor.GetAsync("/api/Pipelines/chats/HELP");
                var html = await page.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Equal(PipelineFormPage.ContentSecurityPolicy, page.Headers.GetValues("Content-Security-Policy").Single());
                var setCookie = page.Headers.GetValues("Set-Cookie").Single();
                output.WriteLine(setCookie);
                Assert.Contains("path=/api/Pipelines/chats/help", setCookie, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
                var session = setCookie.Split(';')[0];
                Assert.Contains("<h1>Help &lt;desk&gt;</h1>", html);
                Assert.Contains("Hi! Ask &lt;me&gt;.", html);
                Assert.DoesNotContain("<script", html);

                // A message runs the pipeline; the reply is its last step's result; both stay in this visitor's conversation.
                var sent = await SendAsync(visitor, session, new() { ["message"] = "Where is <b>order</b> 42?" });
                Assert.Equal(HttpStatusCode.SeeOther, sent.StatusCode);
                Assert.Equal("help", sent.Headers.Location?.OriginalString);
                Assert.Equal(new[] { "Reply", "You", "Reply" }, Lines(await PageAsync(visitor, session)).Select(line => line.Who)); // the welcome first
                await SendAsync(visitor, session, new() { ["message"] = "Thanks" });
                var lines = Lines(await PageAsync(visitor, session));
                Assert.Equal(new[]
                {
                    ("Reply", "Hi! Ask &lt;me&gt;."),
                    ("You", "Where is &lt;b&gt;order&lt;/b&gt; 42?"),
                    ("Reply", "You said: Where is &lt;b&gt;order&lt;/b&gt; 42?"),
                    ("You", "Thanks"),
                    ("Reply", "You said: Thanks"),
                }, lines);

                // Another visitor - another cookie - sees only the welcome.
                var theirs = await other.GetAsync("/api/Pipelines/chats/help");
                var theirSession = theirs.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                Assert.NotEqual(session, theirSession);
                Assert.Single(Lines(await theirs.Content.ReadAsStringAsync()));

                // Too long: the page again with the reason and the draft; a robot's message is dropped; a new conversation forgets.
                var tooLong = await SendAsync(visitor, session, new() { ["message"] = new string('x', PipelineChatOptionsDefaults.MaxMessageLength + 1) });
                Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
                Assert.Contains("A message is at most 2000 characters.", await tooLong.Content.ReadAsStringAsync());
                await SendAsync(visitor, session, new() { ["message"] = "spam", [PipelineFormPage.HoneypotField] = "x" });
                Assert.Equal(5, Lines(await PageAsync(visitor, session)).Count);
                var fresh = await SendAsync(visitor, session, new() { [PipelineChatPage.NewConversationField] = "1" });
                Assert.Equal(HttpStatusCode.SeeOther, fresh.StatusCode);
                var newSession = fresh.Headers.GetValues("Set-Cookie").Last().Split(';')[0];
                Assert.NotEqual(session, newSession);
                Assert.Single(Lines(await PageAsync(visitor, session))); // the old session's conversation is forgotten too
                Assert.Single(Lines(await PageAsync(visitor, newSession)));

                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/chats/nobody")).StatusCode);
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await visitor.PostAsync("/api/Pipelines/chats/help", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_chats_switched_off_no_chat_is_served_or_answered()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await SaveChatPipelineAsync(app, client, cookies, hostId, "help");
                using var visitor = GateDrive.NewClient(app, Host);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/Pipelines/chats/help")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync("/api/Pipelines/chats/help", new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = "hi" }))).StatusCode);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task SaveChatPipelineAsync(WebApplication app, HttpClient client, CookieContainer cookies, int hostId, string name)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Chat " + name }, output, "create " + name);
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-message", Name = "message" }, new PipelineGraphVariable { Id = "v-session", Name = "session" } },
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "chat", Type = "Stomme.ChatTrigger", Name = "A visitor writes",
                        Inputs = { ["ChatName"] = name, ["Title"] = "Help <desk>", ["Welcome"] = "Hi! Ask <me>." },
                        OutputVariables = { ["Result"] = "v-message", ["Session"] = "v-session" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "reply", Type = "Stomme.ConvertMarkdown", Name = "Echo",
                        Inputs = { ["Operation"] = "HtmlToText" },
                        InputExpressions = { ["Value"] = "You said: {{ message | replace: \"<\", \"&lt;\" | replace: \">\", \"&gt;\" }}" },
                    },
                },
                Connections = { new PipelineGraphConnection { SourceId = "chat", TargetId = "reply" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph " + name)).Status);
            var published = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish " + name);
            Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpClient visitor, string session, Dictionary<string, string> fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Pipelines/chats/help") { Content = new FormUrlEncodedContent(fields) };
            request.Headers.TryAddWithoutValidation("Cookie", session);
            return await visitor.SendAsync(request);
        }

        private static async Task<string> PageAsync(HttpClient visitor, string session)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Pipelines/chats/help");
            request.Headers.TryAddWithoutValidation("Cookie", session);
            return await (await visitor.SendAsync(request)).Content.ReadAsStringAsync();
        }

        // The conversation as the page shows it: who said each line, and the line as written into the page.
        private static List<(string Who, string Text)> Lines(string html) =>
            System.Text.RegularExpressions.Regex.Matches(html, "<li class=\"(?:user|assistant)\"><span class=\"who\">(You|Reply)</span>(.*?)</li>")
                .Select(match => (match.Groups[1].Value, match.Groups[2].Value)).ToList();
    }
}
