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
using Stomme.Modules.Connectors.Messaging;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Microsoft Teams (n8n's Microsoft Teams node, its channel and chat message create) as a chat connector: the
    /// refresh token exchanged at the tenant's token endpoint with Graph's scope, the access token a bearer; a message to a channel
    /// (team/channel), a reply in a channel message's thread, a message to a chat - each one record, its id; a channel, a team, a
    /// chat or a message id that could reach the path refused before a token is asked for, a reply in a chat refused; Teams'
    /// refusal a record in Graph's words; a test naming who the token is.
    /// </summary>
    public sealed class TeamsConnectorTests
    {
        private const string Api = "https://graph.example.test/v1.0";

        private const string Team = "4e6f7661-476c-6f62-656e-414220202020";

        private const string Channel = "19:a1b2c3d4e5@thread.tacv2";

        private const string Chat = "19:9f8e7d6c5b@thread.v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Graph : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.Created, "{\"id\":\"1760000000001\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.StartsWith("https://login.example.test/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"access-1\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> GraphRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://login.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static readonly Dictionary<string, string> Settings = new() { ["token_url"] = "https://login.example.test/tenant-1/oauth2/v2.0/token" };

        private static ConnectorExtractContext Context(Graph graph, string to, string? replyTo = null)
        {
            var parameters = new Dictionary<string, string> { [ChatMessages.ToParameter] = to, [ChatMessages.TextParameter] = "Order 42 shipped" };
            if (replyTo is not null)
            {
                parameters[ChatMessages.ReplyToParameter] = replyTo;
            }

            return Contexts.Extract(new StubEgress(graph), Api, ChatMessages.Operation, parameters, Settings, Credentials);
        }

        private static TeamsConnector Connector() => new(new OAuth2TokenProvider());

        [Fact]
        public async Task A_message_goes_to_a_channel_into_a_thread_or_to_a_chat_with_the_refreshed_token()
        {
            var graph = new Graph();
            var sent = await Contexts.DrainAsync(Connector(), Context(graph, $"{Team}/{Channel}"));

            var token = graph.Requests[0];
            Assert.Equal("https://login.example.test/tenant-1/oauth2/v2.0/token", token.Url);
            Assert.Contains("grant_type=refresh_token", token.Body);
            Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default+offline_access", token.Body); // Microsoft's v2 endpoint wants a scope on a refresh
            var post = graph.GraphRequests[0];
            Assert.Equal((HttpMethod.Post, $"{Api}/teams/{Team}/channels/19%3Aa1b2c3d4e5%40thread.tacv2/messages", "Bearer access-1"), (post.Method, post.Url, post.Authorization));
            Assert.Equal(("text", "Order 42 shipped"), ((string?)JsonNode.Parse(post.Body!)!["body"]!["contentType"], (string?)JsonNode.Parse(post.Body!)!["body"]!["content"]));
            Assert.Equal(("1760000000001", $"{Team}/{Channel}"), ((string?)Assert.Single(sent).Fields["id"], (string?)sent[0].Fields["to"]));

            await Contexts.DrainAsync(Connector(), Context(graph, $"{Team}/{Channel}", replyTo: "1759999999999"));
            Assert.Equal($"{Api}/teams/{Team}/channels/19%3Aa1b2c3d4e5%40thread.tacv2/messages/1759999999999/replies", graph.GraphRequests[1].Url); // in its thread

            await Contexts.DrainAsync(Connector(), Context(graph, Chat));
            Assert.Equal($"{Api}/chats/19%3A9f8e7d6c5b%40thread.v2/messages", graph.GraphRequests[2].Url);
        }

        [Fact]
        public async Task What_could_reach_the_path_is_refused_before_a_token_is_asked_for_and_teams_refusal_is_its_words()
        {
            var graph = new Graph();
            async Task<string?> Refused(string to, string? replyTo = null) => (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(graph, to, replyTo))).Fields["refused"];

            Assert.StartsWith("Microsoft Teams: '../x/19:a@thread.tacv2' is not a channel", await Refused("../x/19:a@thread.tacv2"));
            Assert.StartsWith("Microsoft Teams: '../19:a1b2c3d4e5@thread.tacv2' is not a channel", await Refused("../19:a1b2c3d4e5@thread.tacv2")); // a team is a GUID
            Assert.StartsWith($"Microsoft Teams: '{Team}/19:a/../b' is not", await Refused($"{Team}/19:a/../b"));
            Assert.StartsWith($"Microsoft Teams: '{Team}/general' is not a channel", await Refused($"{Team}/general"));
            Assert.StartsWith("Microsoft Teams: '#general' is not a channel", await Refused("#general"));
            Assert.Equal("Microsoft Teams: 'the last one' is not a message's id (digits).", await Refused($"{Team}/{Channel}", "the last one"));
            Assert.Equal("Microsoft Teams: a chat has no threads to answer in - leave In reply to empty.", await Refused(Chat, "1759999999999"));
            Assert.Empty(graph.Requests); // not even a token asked for

            var forbidden = new Graph { Answer = (_, _, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Missing role permissions on the request.\"}}") };
            Assert.Equal("Microsoft Teams: Missing role permissions on the request.", Assert.Single(await Contexts.DrainAsync(Connector(), Context(forbidden, Chat))).Fields["refused"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(new Graph { Answer = (_, _, _) => (HttpStatusCode.BadGateway, "<html/>") }, Chat)));
        }

        [Fact]
        public async Task A_test_names_who_the_token_is()
        {
            var graph = new Graph { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"displayName\":\"Ops Bot\",\"userPrincipalName\":\"ops@contoso.example\"}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(graph), Api, Settings, Credentials));
            Assert.Equal((true, "Signed in as Ops Bot."), (tested.Success, tested.Message));
            Assert.Equal($"{Api}/me?$select=displayName,userPrincipalName", graph.GraphRequests[0].Url);
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Graph { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token is empty.\"}}") }), Api, Settings, Credentials));
            Assert.Equal((false, "Microsoft Teams: Access token is empty."), (refused.Success, refused.Message));
            var unrefreshable = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(graph), Api, Settings, new Dictionary<string, string> { ["client-id"] = "app-1", ["client-secret"] = "secret-1" }));
            Assert.False(unrefreshable.Success); // no refresh token: said, never thrown
            Assert.Contains("refresh-token", unrefreshable.Message);
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "teams", ConnectorCategory.Messaging), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "message" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
