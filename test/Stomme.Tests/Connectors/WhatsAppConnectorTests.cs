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
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - WhatsApp Business (n8n's WhatsApp Business Cloud node, its text message sent) for Send chat message: Meta's Cloud
    /// API under the endpoint's Graph version, a system user's token as the bearer, the connection's phone number ID the sender
    /// and the path's one segment of digits; a text to a phone number - its spaces, dashes and + dropped - quoting a message when
    /// it answers one; a number, a message id and a text too long refused before anything is sent; Meta's refusal its message
    /// with its details (the 24-hour window said in its words); a test naming the number.
    /// </summary>
    public sealed class WhatsAppConnectorTests
    {
        private const string Graph = "https://graph.example.test/v23.0";

        private static readonly Dictionary<string, string> Settings = new() { [WhatsAppConnector.PhoneNumberSetting] = "106540352242922" };

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "EAAG-system-user" };

        private sealed class GraphApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public (HttpStatusCode Status, string Body) Answer { get; set; } =
                (HttpStatusCode.OK, "{\"messaging_product\":\"whatsapp\",\"contacts\":[{\"input\":\"46701740605\",\"wa_id\":\"46701740605\"}],\"messages\":[{\"id\":\"wamid.HBgLNDY3MDEyMzQ1NjcVAgARGBI\"}]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(Answer.Status) { Content = new StringContent(Answer.Body, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(GraphApi graph, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(graph), Graph, "message", parameters, settings ?? Settings, Credentials);

        [Fact]
        public async Task A_text_goes_to_a_phone_number_from_the_connections_number_quoting_a_message_it_answers()
        {
            var graph = new GraphApi();
            var sent = await Contexts.DrainAsync(new WhatsAppConnector(), Context(graph, new() { ["to"] = "+46 70-174 06 05", ["text"] = "Order 42 shipped", ["reply_to"] = "wamid.HBgLNDY3MDEyMzQ1NjcVAgASGBQzQUY" }));

            Assert.Equal(("wamid.HBgLNDY3MDEyMzQ1NjcVAgARGBI", "46701740605"), ((string?)Assert.Single(sent).Fields["id"], (string?)sent[0].Fields["to"]));
            var request = Assert.Single(graph.Requests);
            Assert.Equal((HttpMethod.Post, Graph + "/106540352242922/messages", "Bearer EAAG-system-user"), (request.Method, request.Url, request.Authorization));
            var body = JsonNode.Parse(request.Body!)!;
            Assert.Equal("{\"messaging_product\":\"whatsapp\",\"recipient_type\":\"individual\",\"to\":\"46701740605\",\"type\":\"text\",\"text\":{\"preview_url\":false,\"body\":\"Order 42 shipped\"},\"context\":{\"message_id\":\"wamid.HBgLNDY3MDEyMzQ1NjcVAgASGBQzQUY\"}}", body.ToJsonString());

            var plain = new GraphApi();
            await Contexts.DrainAsync(new WhatsAppConnector(), Context(plain, new() { ["to"] = "46701740605", ["text"] = "Hello" }));
            Assert.False(JsonNode.Parse(plain.Requests[0].Body!)!.AsObject().ContainsKey("context")); // no reply, no quote
        }

        [Fact]
        public async Task A_number_a_message_id_and_a_text_too_long_are_refused_before_anything_is_sent_and_metas_refusal_is_its_words()
        {
            var graph = new GraphApi();
            async Task<string?> Refused(Dictionary<string, string> parameters) =>
                (string?)Assert.Single(await Contexts.DrainAsync(new WhatsAppConnector(), Context(graph, parameters))).Fields["refused"];

            Assert.Equal("WhatsApp: '070-123' is not a phone number with its country code (+46701740605).", await Refused(new() { ["to"] = "070-123", ["text"] = "Hi" }));
            Assert.Equal("WhatsApp: '+46 70 CALL ME' is not a phone number with its country code (+46701740605).", await Refused(new() { ["to"] = "+46 70 CALL ME", ["text"] = "Hi" }));
            Assert.Equal("WhatsApp: '12345' is not a WhatsApp message's id (wamid....).", await Refused(new() { ["to"] = "46701740605", ["text"] = "Hi", ["reply_to"] = "12345" }));
            Assert.Equal("WhatsApp: a text message holds at most 4096 characters - this one has 4097.", await Refused(new() { ["to"] = "46701740605", ["text"] = new string('a', 4097) }));
            var noNumber = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WhatsAppConnector(), Context(graph, new() { ["to"] = "46701740605", ["text"] = "Hi" }, new() { [WhatsAppConnector.PhoneNumberSetting] = "+46 70 123" })));
            Assert.Equal("The connection's phone number ID must be its digits - WhatsApp Manager > Phone numbers names it.", noNumber.Message);
            Assert.Empty(graph.Requests);

            var outside = new GraphApi
            {
                Answer = (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"(#131047) Re-engagement message\",\"type\":\"OAuthException\",\"code\":131047,\"error_data\":{\"messaging_product\":\"whatsapp\",\"details\":\"Message failed to send because more than 24 hours have passed since the customer last replied to this number.\"},\"fbtrace_id\":\"Az8or2yhqkZfEZ-_4Qn_Bam\"}}"),
            };
            var refused = await Contexts.DrainAsync(new WhatsAppConnector(), Context(outside, new() { ["to"] = "46701740605", ["text"] = "Hi" }));
            Assert.Equal("WhatsApp: (#131047) Re-engagement message - Message failed to send because more than 24 hours have passed since the customer last replied to this number.", Assert.Single(refused).Fields["refused"]);

            var down = new GraphApi { Answer = (HttpStatusCode.BadGateway, "<html>bad gateway</html>") };
            Assert.Equal("HTTP 502: not WhatsApp's answer.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new WhatsAppConnector(), Context(down, new() { ["to"] = "46701740605", ["text"] = "Hi" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_number()
        {
            var graph = new GraphApi { Answer = (HttpStatusCode.OK, "{\"display_phone_number\":\"+1 555-0100\",\"verified_name\":\"Acme Orders\",\"id\":\"106540352242922\"}") };
            var tested = await new WhatsAppConnector().TestConnectionAsync(Contexts.Test(new StubEgress(graph), Graph, Settings, Credentials));
            Assert.Equal((true, "Signed in as Acme Orders (+1 555-0100)."), (tested.Success, tested.Message));
            Assert.Equal((Graph + "/106540352242922?fields=display_phone_number,verified_name", "Bearer EAAG-system-user"), (graph.Requests[0].Url, graph.Requests[0].Authorization));

            var refused = await new WhatsAppConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new GraphApi { Answer = (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid OAuth access token - Cannot parse access token\",\"code\":190}}") }), Graph, Settings, Credentials));
            Assert.Equal((false, "WhatsApp: Invalid OAuth access token - Cannot parse access token"), (refused.Success, refused.Message));
            var unnamed = await new WhatsAppConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new GraphApi()), Graph, new Dictionary<string, string> { [WhatsAppConnector.PhoneNumberSetting] = "abc" }, Credentials));
            Assert.Equal((false, "The connection's phone number ID must be its digits - WhatsApp Manager > Phone numbers names it."), (unnamed.Success, unnamed.Message));

            var connector = new WhatsAppConnector();
            Assert.Equal((AuthenticationType.Bearer, "whatsapp", ConnectorCategory.Messaging), (connector.AuthType, connector.ConnectorType, Assert.Single(connector.Categories)));
            Assert.Equal("message", Assert.Single(connector.Operations).Name);
        }
    }
}
