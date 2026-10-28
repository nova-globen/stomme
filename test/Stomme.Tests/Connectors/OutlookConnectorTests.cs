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
using Stomme.Modules.Connectors.Outlook;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft Outlook (n8n's Get Many, Send and Reply): an app-only token from the tenant's token endpoint, sent as a
    /// bearer, acting as the one mailbox the connection names; a folder's messages read newest first through Graph's next links -
    /// never one that leaves the mailbox's address - up to the most asked for, a search or a filter passed on, each a record with
    /// its people and its body by its kind; a message made a draft and sent, so it has an id; a reply Outlook makes in the
    /// original's conversation, the body given above it; messages marked read; a subject with a line break, a recipient that is
    /// not an address, an id or a mailbox that could reach the path refused before anything is sent - a send's refusal as its
    /// record, Graph's own code and message in it.
    /// </summary>
    public sealed class OutlookConnectorTests
    {
        private const string Box = "https://graph.example.test/v1.0/users/orders%40contoso.com";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = "https://login.example.test/tenant/oauth2/v2.0/token" };

        /// <summary>Microsoft as a test sees it: the token endpoint, then Graph, each request recorded with its body.</summary>
        private sealed class Microsoft : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Graph { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://login.example.test/", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : Graph(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public IEnumerable<(HttpMethod Method, string Url, string? Authorization, string? Body)> GraphRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://login.example.test/", StringComparison.Ordinal));
        }

        private static ConnectorExtractContext Context(Microsoft microsoft, string operation, Dictionary<string, string> parameters, string mailbox = "orders@contoso.com") =>
            Contexts.Extract(new StubEgress(microsoft), "https://graph.example.test/v1.0", operation, parameters,
                settings: new Dictionary<string, string> { ["mailbox"] = mailbox }, credentials: Credentials);

        private static OutlookConnector Connector() => new(new OAuth2TokenProvider());

        private static string Message(string id, string subject, bool html = false) => """
            {"id":"@ID","conversationId":"c-@ID","internetMessageId":"<@ID@contoso.com>","receivedDateTime":"2026-10-11T09:30:00Z",
             "from":{"emailAddress":{"name":"Ada Lovelace","address":"ada@example.com"}},"toRecipients":[{"emailAddress":{"name":"Orders","address":"orders@contoso.com"}}],
             "ccRecipients":[],"replyTo":[],"subject":"@SUBJECT","body":{"contentType":"@KIND","content":"@BODY"},
             "bodyPreview":"Twelve, please.","isRead":false,"hasAttachments":true,"webLink":"https://outlook.office.com/owa/?ItemID=@ID"}
            """.Replace("@ID", id, StringComparison.Ordinal).Replace("@SUBJECT", subject, StringComparison.Ordinal)
            .Replace("@KIND", html ? "html" : "text", StringComparison.Ordinal).Replace("@BODY", html ? "<p>Twelve, please.</p>" : "Twelve, please.", StringComparison.Ordinal);

        [Fact]
        public async Task A_folders_messages_are_read_newest_first_through_graphs_next_links_up_to_the_most_asked_for()
        {
            var microsoft = new Microsoft
            {
                Graph = (_, url, _) => url.Contains("skiptoken", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, $$"""{"value":[{{Message("AAMk3", "Third")}},{{Message("AAMk4", "Fourth")}}],"@odata.nextLink":"https://evil.example/steal"}""")
                    : (HttpStatusCode.OK, $$"""{"value":[{{Message("AAMk1", "Order 1001", html: true)}},{{Message("AAMk2", "Second")}}],"@odata.nextLink":"{{Box}}/mailFolders/inbox/messages?$skiptoken=page-2"}"""),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(microsoft, OutlookConnector.SearchOperation, new() { ["filter"] = "isRead eq false", ["max"] = "10", ["body_max_chars"] = "10" }));

            var token = microsoft.Requests[0];
            Assert.Equal((HttpMethod.Post, "https://login.example.test/tenant/oauth2/v2.0/token"), (token.Method, token.Url));
            Assert.Contains("grant_type=client_credentials", token.Body);
            Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", token.Body);
            var calls = microsoft.GraphRequests.ToList();
            Assert.Equal(2, calls.Count); // the next link that leaves the mailbox's address never followed, though more were asked for
            Assert.StartsWith($"{Box}/mailFolders/inbox/messages?$top=10&$select=", calls[0].Url);
            Assert.EndsWith("&$orderby=receivedDateTime%20desc&$filter=isRead%20eq%20false", calls[0].Url);
            Assert.All(calls, call => Assert.Equal("Bearer access-1", call.Authorization));
            Assert.Equal(new[] { "AAMk1", "AAMk2", "AAMk3", "AAMk4" }, records.Select(record => (string)record.Fields["id"]!));

            var capped = new Microsoft { Graph = microsoft.Graph };
            Assert.Equal(new[] { "AAMk1", "AAMk2", "AAMk3" }, (await Contexts.DrainAsync(Connector(), Context(capped, OutlookConnector.SearchOperation, new() { ["max"] = "3" }))).Select(record => (string)record.Fields["id"]!)); // at most three
            var ada = records[0].Fields;
            Assert.Equal(("c-AAMk1", "AAMk1@contoso.com", "\"Ada Lovelace\" <ada@example.com>", "\"Orders\" <orders@contoso.com>"), ((string?)ada["conversation_id"], (string?)ada["message_id"], (string?)ada["from"], (string?)ada["to"]));
            Assert.Equal(((string?)null, "<p>Twelve,", true, true), ((string?)ada["text"], (string?)ada["html"], (bool?)ada["truncated"], (bool?)ada["has_attachments"]));
            Assert.Equal(("Twelve, pl", (string?)null), ((string?)records[1].Fields["text"], (string?)records[1].Fields["html"]));

            var searching = new Microsoft { Graph = (_, _, _) => (HttpStatusCode.OK, """{"value":[]}""") };
            await Contexts.DrainAsync(Connector(), Context(searching, OutlookConnector.SearchOperation, new() { ["folder"] = "archive", ["query"] = "from:ada \"order\"" }));
            var searched = searching.GraphRequests.Single().Url;
            Assert.StartsWith($"{Box}/mailFolders/archive/messages?$top=50&", searched);
            Assert.EndsWith("&$search=%22from%3Aada%20order%22", searched); // quoted once, its own quotes dropped; no order with a search
        }

        [Fact]
        public async Task A_message_is_made_a_draft_and_sent_and_a_reply_is_made_by_outlook_in_its_conversation()
        {
            var microsoft = new Microsoft
            {
                Graph = (method, url, _) => url.EndsWith("/send", StringComparison.Ordinal)
                    ? (HttpStatusCode.Accepted, "")
                    : (HttpStatusCode.Created, """{"id":"AAMkD1","conversationId":"c-1","internetMessageId":"<d1@contoso.com>"}"""),
            };
            var sent = await Contexts.DrainAsync(Connector(), Context(microsoft, OutlookConnector.SendOperation, new()
            {
                ["to"] = "Ada <ada@example.com>, lin@example.com", ["cc"] = "grace@example.com", ["subject"] = "Your order", ["text"] = "It ships today.\nTuesday.", ["html"] = "<p>It ships today.</p>",
            }));

            var calls = microsoft.GraphRequests.ToList();
            Assert.Equal((HttpMethod.Post, $"{Box}/messages"), (calls[0].Method, calls[0].Url));
            var draft = JsonNode.Parse(calls[0].Body!)!;
            Assert.Equal(("Your order", "HTML", "<p>It ships today.</p>"), (draft["subject"]!.GetValue<string>(), draft["body"]!["contentType"]!.GetValue<string>(), draft["body"]!["content"]!.GetValue<string>()));
            Assert.Equal(new[] { "ada@example.com", "lin@example.com" }, draft["toRecipients"]!.AsArray().Select(recipient => recipient!["emailAddress"]!["address"]!.GetValue<string>()));
            Assert.Equal("grace@example.com", draft["ccRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
            Assert.Equal((HttpMethod.Post, $"{Box}/messages/AAMkD1/send"), (calls[1].Method, calls[1].Url));
            Assert.Equal(("AAMkD1", "c-1", "d1@contoso.com"), ((string?)Assert.Single(sent).Fields["id"], (string?)sent[0].Fields["thread_id"], (string?)sent[0].Fields["message_id"]));

            var replying = new Microsoft { Graph = microsoft.Graph };
            await Contexts.DrainAsync(Connector(), Context(replying, OutlookConnector.SendOperation, new() { ["reply_to"] = "AAMk/1+x=", ["text"] = "Shipped." }));
            var reply = replying.GraphRequests.ToList();
            Assert.Equal((HttpMethod.Post, $"{Box}/messages/AAMk%2F1%2Bx%3D/createReply", """{"comment":"Shipped."}"""), (reply[0].Method, reply[0].Url, reply[0].Body));
            Assert.Equal((HttpMethod.Post, $"{Box}/messages/AAMkD1/send"), (reply[1].Method, reply[1].Url)); // nothing changed: no PATCH, Outlook's own reply sent

            var redirected = new Microsoft { Graph = microsoft.Graph };
            await Contexts.DrainAsync(Connector(), Context(redirected, OutlookConnector.SendOperation, new() { ["reply_to"] = "AAMk1", ["to"] = "lin@example.com", ["text"] = "Shipped." }));
            var patched = redirected.GraphRequests.ElementAt(1);
            Assert.Equal((HttpMethod.Patch, $"{Box}/messages/AAMkD1", """{"toRecipients":[{"emailAddress":{"address":"lin@example.com"}}]}"""), (patched.Method, patched.Url, patched.Body));
        }

        [Fact]
        public async Task Messages_are_marked_read_and_what_could_reach_the_path_or_a_header_is_refused_before_anything_is_sent()
        {
            var microsoft = new Microsoft { Graph = (_, _, _) => (HttpStatusCode.OK, """{"id":"x","isRead":true}""") };
            var marked = await Contexts.DrainAsync(Connector(), Context(microsoft, OutlookConnector.MarkReadOperation, new() { ["ids"] = """["AAMk1","AAMk2","AAMk1"]""" }));
            Assert.Equal(new[] { $"{Box}/messages/AAMk1", $"{Box}/messages/AAMk2" }, microsoft.GraphRequests.Select(call => call.Url));
            Assert.All(microsoft.GraphRequests, call => Assert.Equal((HttpMethod.Patch, """{"isRead":true}"""), (call.Method, call.Body)));
            Assert.Equal(2L, (long?)Assert.Single(marked).Fields["marked"]);

            var quiet = new Microsoft();
            async Task<string?> Refused(Dictionary<string, string> parameters) =>
                (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(quiet, OutlookConnector.SendOperation, parameters))).Fields["refused"];
            Assert.Equal("Microsoft Outlook: The subject has a line break - one line only.", await Refused(new() { ["to"] = "ada@example.com", ["subject"] = "Hi\nBcc: everyone@example.com", ["text"] = "x" }));
            Assert.Equal("Microsoft Outlook: 'ada' is not a list of addresses - for to.", await Refused(new() { ["to"] = "ada", ["text"] = "x" }));
            Assert.Equal("Microsoft Outlook: '../me' is not an Outlook message id.", await Refused(new() { ["reply_to"] = "../me", ["text"] = "x" }));
            Assert.StartsWith("'../users' is not a folder", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(quiet, OutlookConnector.SearchOperation, new() { ["folder"] = "../users" })))).Message);
            Assert.StartsWith("'ada/../x' is not a mailbox", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(quiet, OutlookConnector.SearchOperation, new(), mailbox: "ada/../x")))).Message);
            Assert.Equal("Give a search or a filter - Graph takes one or the other.", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(quiet, OutlookConnector.SearchOperation, new() { ["query"] = "a", ["filter"] = "isRead eq false" })))).Message);
            Assert.Empty(quiet.Requests); // not even a token asked for

            var refusing = new Microsoft { Graph = (_, _, _) => (HttpStatusCode.BadRequest, """{"error":{"code":"ErrorInvalidRecipients","message":"At least one recipient is not valid."}}""") };
            Assert.Equal("Microsoft Outlook: HTTP 400 ErrorInvalidRecipients. At least one recipient is not valid.", (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(refusing, OutlookConnector.SendOperation, new() { ["to"] = "ada@example.com", ["text"] = "x" }))).Fields["refused"]);
            Assert.Equal("HTTP 400 ErrorInvalidRecipients.", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, OutlookConnector.MarkReadOperation, new() { ["ids"] = "AAMk1" })))).Message);
        }

        [Fact]
        public async Task A_test_reads_the_mailboxs_inbox()
        {
            var microsoft = new Microsoft { Graph = (_, _, _) => (HttpStatusCode.OK, """{"displayName":"Inbox","totalItemCount":42}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(microsoft), "https://graph.example.test/v1.0", settings: new Dictionary<string, string> { ["mailbox"] = "orders@contoso.com" }, credentials: Credentials));
            Assert.Equal((true, "Signed in: Inbox holds 42 messages."), (tested.Success, tested.Message));
            Assert.Equal($"{Box}/mailFolders/inbox?$select=displayName,totalItemCount", microsoft.GraphRequests.Single().Url);
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "outlook", ConnectorCategory.Messaging), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "search", "send", "mark_read" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
