#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Gmail;
using Stomme.Modules.Connectors.Shared;
using MimeKit;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Gmail (n8n's Get Many, Send, Reply and Mark as Read): the refresh token exchanged at the token endpoint, the access
    /// token sent as a bearer; the messages Gmail's search keeps read page by page up to the most asked for, each parsed - its
    /// headers, text, HTML and attachments' names; a message built by MimeKit and sent, or sent as a reply in a message's thread to
    /// its sender; messages marked read by their ids; a subject with a line break, an address that is not one, a message id that
    /// could reach the path refused before anything is sent; Google's refusal its status word; a throttled call sent again.
    /// </summary>
    public sealed class GmailConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        /// <summary>Google as a test sees it: the token endpoint, then the Gmail API, each request recorded with its body.</summary>
        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Gmail { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            /// <summary>How many Gmail calls are answered "too many requests, try again now" before Gmail answers.</summary>
            public int Throttled { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
                }

                if (Throttled > 0)
                {
                    Throttled--;
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""", Encoding.UTF8, "application/json") };
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return throttled;
                }

                var (status, answer) = Gmail(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public IEnumerable<(HttpMethod Method, string Url, string? Authorization, string? Body)> GmailRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal));
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), "https://gmail.example.test/gmail/v1", operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static GmailConnector Connector() => new(new OAuth2TokenProvider());

        // A message as Gmail keeps it raw: base64url of its RFC 822 bytes.
        private static string Raw(string from, string subject, string text, string? html = null, string? attachment = null)
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse("ops@example.com"));
            message.Subject = subject;
            message.MessageId = subject.Replace(' ', '-') + "@example.com";
            message.Date = new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero);
            var body = new BodyBuilder { TextBody = text, HtmlBody = html };
            if (attachment is not null)
            {
                body.Attachments.Add(attachment, Encoding.UTF8.GetBytes("1001;Ada;12"), new ContentType("text", "csv"));
            }

            message.Body = body.ToMessageBody();
            using var bytes = new MemoryStream();
            message.WriteTo(bytes);
            return Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static MimeMessage Sent(string body)
        {
            var raw = JsonNode.Parse(body)!["raw"]!.GetValue<string>().Replace('-', '+').Replace('_', '/');
            raw += new string('=', (4 - (raw.Length % 4)) % 4);
            return MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw)));
        }

        [Fact]
        public async Task The_messages_a_search_keeps_are_read_page_by_page_up_to_the_most_asked_for_each_parsed()
        {
            var google = new Google
            {
                Gmail = (_, url, _) => url switch
                {
                    _ when url.Contains("/messages?", StringComparison.Ordinal) && !url.Contains("pageToken=", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, """{"messages":[{"id":"m1","threadId":"t1"},{"id":"m2","threadId":"t2"}],"nextPageToken":"page-2"}"""),
                    _ when url.Contains("/messages?", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, """{"messages":[{"id":"m3","threadId":"t3"},{"id":"m4","threadId":"t4"}],"nextPageToken":"page-3"}"""),
                    _ when url.Contains("/messages/m1?", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, $$"""{"id":"m1","threadId":"t1","labelIds":["INBOX","UNREAD"],"snippet":"Order 1001","raw":"{{Raw("Ada <ada@example.com>", "Order 1001", "Twelve, please.", "<p>Twelve, please.</p>", "order.csv")}}"}"""),
                    _ => (HttpStatusCode.OK, $$"""{"id":"{{url.Split("/messages/")[1].Split('?')[0]}}","threadId":"t","labelIds":["INBOX"],"raw":"{{Raw("Lin <lin@example.com>", "Hello", new string('x', 30))}}"}"""),
                },
            };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.SearchOperation, new() { ["query"] = "from:ada is:unread", ["labels"] = "INBOX, UNREAD", ["max"] = "3", ["body_max_chars"] = "25" }));

            Assert.Equal(3, records.Count); // at most three: the second page's second message never read
            var calls = google.GmailRequests.Select(request => request.Url).ToList();
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/messages?maxResults=3&q=from%3Aada%20is%3Aunread&labelIds=INBOX&labelIds=UNREAD", calls[0]);
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/messages/m1?format=raw", calls[1]);
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/messages?maxResults=1&q=from%3Aada%20is%3Aunread&labelIds=INBOX&labelIds=UNREAD&pageToken=page-2", calls[3]);
            Assert.Equal(5, calls.Count); // a list, two reads, a list, one read - no third list
            Assert.All(google.GmailRequests, request => Assert.Equal("Bearer access-1", request.Authorization));

            var ada = records[0].Fields;
            Assert.Equal(("m1", "t1", "[\"INBOX\",\"UNREAD\"]", "Order 1001", "Order-1001@example.com"), ((string?)ada["id"], (string?)ada["thread_id"], (string?)ada["labels"], (string?)ada["snippet"], (string?)ada["message_id"]));
            Assert.Equal(("\"Ada\" <ada@example.com>", "ops@example.com", "Order 1001", "2026-10-11T09:30:00.0000000Z"), ((string?)ada["from"], (string?)ada["to"], (string?)ada["subject"], (string?)ada["date"]));
            Assert.Equal(("Twelve, please.", "<p>Twelve, please.</p>", false), ((string?)ada["text"], (string?)ada["html"], (bool?)ada["truncated"]));
            Assert.Equal("""[{"name":"order.csv","contentType":"text/csv","size":11}]""", (string?)ada["attachments"]);
            Assert.Equal((new string('x', 25), true), ((string?)records[1].Fields["text"], (bool?)records[1].Fields["truncated"])); // cut, and said
        }

        [Fact]
        public async Task A_message_is_built_and_sent_and_a_reply_goes_in_its_thread_to_its_sender()
        {
            var google = new Google { Gmail = (_, _, _) => (HttpStatusCode.OK, """{"id":"s1","threadId":"t9","labelIds":["SENT"]}"""), Throttled = 1 };
            var sent = await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.SendOperation, new()
            {
                ["to"] = "Ada <ada@example.com>, lin@example.com", ["cc"] = "grace@example.com", ["bcc"] = "audit@example.com",
                ["subject"] = "Your order - åäö", ["text"] = "It ships today.", ["html"] = "<p>It ships today.</p>",
            }));

            var posts = google.GmailRequests.Where(request => request.Method == HttpMethod.Post).ToList();
            Assert.Equal(2, posts.Count); // throttled once: sent again
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/messages/send", posts[1].Url);
            var message = Sent(posts[1].Body!);
            Assert.Equal(("\"Ada\" <ada@example.com>, lin@example.com", "grace@example.com", "audit@example.com"), (message.To.ToString(), message.Cc.ToString(), message.Bcc.ToString()));
            Assert.Equal(("Your order - åäö", "It ships today.", "<p>It ships today.</p>"), (message.Subject, message.TextBody, message.HtmlBody));
            Assert.Null(JsonNode.Parse(posts[1].Body!)!["threadId"]);
            Assert.Equal(("s1", "t9", "[\"SENT\"]"), ((string?)Assert.Single(sent).Fields["id"], (string?)sent[0].Fields["thread_id"], (string?)sent[0].Fields["labels"]));

            var replying = new Google
            {
                Gmail = (method, url, _) => method == HttpMethod.Get
                    ? (HttpStatusCode.OK, """{"id":"m1","threadId":"t1","payload":{"headers":[{"name":"From","value":"Ada <ada@example.com>"},{"name":"Subject","value":"Order 1001"},{"name":"Message-ID","value":"<order-1001@example.com>"},{"name":"References","value":"<first@example.com>"}]}}""")
                    : (HttpStatusCode.OK, """{"id":"s2","threadId":"t1","labelIds":["SENT"]}"""),
            };
            await Contexts.DrainAsync(Connector(), Context(replying, GmailConnector.SendOperation, new() { ["reply_to"] = "m1", ["text"] = "Shipped." }));
            var metadata = replying.GmailRequests.First();
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/messages/m1?format=metadata&metadataHeaders=Message-ID&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Reply-To&metadataHeaders=References", metadata.Url);
            var replyBody = replying.GmailRequests.Single(request => request.Method == HttpMethod.Post).Body!;
            var reply = Sent(replyBody);
            Assert.Equal(("t1", "\"Ada\" <ada@example.com>", "Re: Order 1001"), (JsonNode.Parse(replyBody)!["threadId"]!.GetValue<string>(), reply.To.ToString(), reply.Subject));
            Assert.Equal("order-1001@example.com", reply.InReplyTo);
            Assert.Equal(new[] { "first@example.com", "order-1001@example.com" }, reply.References);
        }

        [Fact]
        public async Task Messages_are_marked_read_by_their_ids()
        {
            var google = new Google { Gmail = (_, _, _) => (HttpStatusCode.NoContent, "") };
            var marked = await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.MarkReadOperation, new() { ["ids"] = """["m1","m2","m1"]""" }));
            var call = Assert.Single(google.GmailRequests);
            Assert.Equal((HttpMethod.Post, "https://gmail.example.test/gmail/v1/users/me/messages/batchModify", """{"ids":["m1","m2"],"removeLabelIds":["UNREAD"]}"""), (call.Method, call.Url, call.Body));
            Assert.Equal(2L, (long?)Assert.Single(marked).Fields["marked"]);

            var listed = new Google { Gmail = google.Gmail };
            await Contexts.DrainAsync(Connector(), Context(listed, GmailConnector.MarkReadOperation, new() { ["ids"] = "m1, m3" }));
            Assert.Equal("""{"ids":["m1","m3"],"removeLabelIds":["UNREAD"]}""", Assert.Single(listed.GmailRequests).Body);
        }

        [Fact]
        public async Task What_could_reach_a_header_or_the_path_is_refused_before_anything_is_sent_and_googles_refusal_is_its_status()
        {
            var google = new Google();
            async Task<string> Thrown(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;
            async Task<string?> Refused(Dictionary<string, string> parameters) => // a send says why as its record
                (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.SendOperation, parameters))).Fields["refused"];

            Assert.Equal("Gmail: The subject has a line break - one line only.", await Refused(new() { ["to"] = "ada@example.com", ["subject"] = "Hi\r\nBcc: everyone@example.com", ["text"] = "x" }));
            Assert.Equal("Gmail: 'not an address' is not a list of addresses - for to.", await Refused(new() { ["to"] = "not an address", ["text"] = "x" }));
            Assert.Equal("Gmail: Give its body - 'text', 'html' or both.", await Refused(new() { ["to"] = "ada@example.com" }));
            Assert.Equal("Gmail: '../drafts' is not a Gmail message id.", await Refused(new() { ["reply_to"] = "../drafts", ["text"] = "x" }));
            Assert.Equal("'m1/../x' is not a Gmail message id.", await Thrown(GmailConnector.MarkReadOperation, new() { ["ids"] = "m1, m1/../x" }));
            Assert.Equal("'IN BOX' is not a label id - INBOX, UNREAD, or a label's own id.", await Thrown(GmailConnector.SearchOperation, new() { ["labels"] = "IN BOX" }));
            Assert.Empty(google.Requests); // not even a token asked for

            var missing = new Google { Gmail = (_, _, _) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}""") };
            Assert.Equal("HTTP 404 NOT_FOUND.", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, GmailConnector.MarkReadOperation, new() { ["ids"] = "m1" })))).Message);
            var unsent = new Google { Gmail = (_, _, _) => (HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Mail service not enabled","status":"FAILED_PRECONDITION"}}""") };
            Assert.Equal("Gmail: HTTP 400 FAILED_PRECONDITION. Mail service not enabled", (string?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(unsent, GmailConnector.SendOperation, new() { ["to"] = "ada@example.com", ["text"] = "x" }))).Fields["refused"]);
        }

        [Fact]
        public async Task A_labels_new_messages_are_read_from_its_history_as_the_email_trigger_reads_a_mailbox()
        {
            var google = new Google
            {
                Gmail = (_, url, _) => url switch
                {
                    _ when url.EndsWith("/users/me/profile", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"emailAddress":"ops@example.com","messagesTotal":12,"historyId":"5000"}"""),
                    _ when url.Contains("/history?", StringComparison.Ordinal) && !url.Contains("pageToken=", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, """{"history":[{"id":"4101","messagesAdded":[{"message":{"id":"m1","threadId":"t1","labelIds":["INBOX","UNREAD"]}}]},{"id":"4100","messagesAdded":[{"message":{"id":"d1","threadId":"t0","labelIds":["DRAFT"]}}]},{"id":"4090","messagesAdded":[{"message":{"id":"m0","threadId":"t0","labelIds":["INBOX"]}}]}],"nextPageToken":"h2","historyId":"4200"}"""),
                    _ when url.Contains("/history?", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, """{"history":[{"id":"4150","messagesAdded":[{"message":{"id":"gone","threadId":"t2","labelIds":["INBOX"]}},{"message":{"id":"m2","threadId":"t3","labelIds":["INBOX"]}}]}],"historyId":"4200"}"""),
                    _ when url.Contains("/messages/m1?format=raw", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, $$"""{"id":"m1","threadId":"t1","historyId":"4101","raw":"{{Raw("Ada <ada@example.com>", "Order 1001", "Twelve, please.", attachment: "order.csv")}}"}"""),
                    _ when url.Contains("/messages/m2?format=raw", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, $$"""{"id":"m2","threadId":"t3","historyId":"4150","raw":"{{Raw("Lin <lin@example.com>", "Hello", "Hi.")}}"}"""),
                    _ when url.Contains("/messages/d1?format=raw", StringComparison.Ordinal) => // a draft Gmail still has: left out for its label, not for being gone
                        (HttpStatusCode.OK, $$"""{"id":"d1","threadId":"t0","historyId":"4100","raw":"{{Raw("Ops <ops@example.com>", "Draft", "Unsent.")}}"}"""),
                    _ when url.Contains("/messages/m0?format=raw", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, $$"""{"id":"m0","threadId":"t0","historyId":"4090","raw":"{{Raw("Grace <grace@example.com>", "Earlier", "First.")}}"}"""),
                    _ => (HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                },
            };

            var status = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.StatusOperation, new() { ["mailbox"] = "INBOX" })));
            Assert.Equal(("INBOX", 1L, 5001L, 12L), ((string?)status.Fields["mailbox"], (long?)status.Fields["uid_validity"], (long?)status.Fields["uid_next"], (long?)status.Fields["messages"]));

            var records = await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.ReadOperation, new() { ["mailbox"] = "INBOX", ["after_uid"] = "4000", ["uid_validity"] = "1", ["attachments"] = "content", ["attachment_max_bytes"] = "100" }));
            var lists = google.GmailRequests.Where(request => request.Url.Contains("/history?", StringComparison.Ordinal)).Select(request => request.Url).ToList();
            Assert.Equal("https://gmail.example.test/gmail/v1/users/me/history?startHistoryId=4000&historyTypes=messageAdded&labelId=INBOX&maxResults=100", lists[0]);
            Assert.EndsWith("&pageToken=h2", lists[1]);
            Assert.Equal(new[] { ("m0", 4090L), ("m1", 4101L), ("m2", 4150L) }, records.Select(record => ((string)record.Fields["id"]!, (long)record.Fields["uid"]!))); // oldest first; the draft is not the label's; the gone one passed over
            var ada = records[1].Fields;
            Assert.Equal((1L, "Order 1001", "\"Ada\" <ada@example.com>"), ((long?)ada["uid_validity"], (string?)ada["subject"], (string?)ada["from"]));
            Assert.True((long)ada["size"]! > 0);
            Assert.Equal("""[{"name":"order.csv","contentType":"text/csv","size":11,"content":"MTAwMTtBZGE7MTI="}]""", (string?)ada["attachments"]);

            var small = await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.ReadOperation, new() { ["after_uid"] = "4000", ["attachments"] = "content", ["attachment_max_bytes"] = "4" }));
            Assert.Equal("""[{"name":"order.csv","contentType":"text/csv","size":11,"skipped":"larger than 4 bytes"}]""", (string?)small[1].Fields["attachments"]);

            // A point read under another numbering: watched afresh from where the history stands, the history not asked.
            var asked = google.GmailRequests.Count();
            var other = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.ReadOperation, new() { ["after_uid"] = "4000", ["uid_validity"] = "7" })));
            Assert.Equal((true, 5001L), ((bool?)other.Fields["renumbered"], (long?)other.Fields["uid_next"]));
            Assert.DoesNotContain(google.GmailRequests.Skip(asked), request => request.Url.Contains("/history?", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_point_gmail_no_longer_keeps_is_answered_as_a_mailbox_made_again_and_the_newest_message_is_read_by_itself()
        {
            var google = new Google
            {
                Gmail = (_, url, _) => url switch
                {
                    _ when url.EndsWith("/users/me/profile", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"emailAddress":"ops@example.com","historyId":"9000"}"""),
                    _ when url.Contains("/history?", StringComparison.Ordinal) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}"""),
                    _ when url.Contains("/messages?maxResults=1&labelIds=Label_7", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"messages":[{"id":"m9","threadId":"t9"}]}"""),
                    _ when url.Contains("/messages/m9?format=raw", StringComparison.Ordinal) =>
                        (HttpStatusCode.OK, $$"""{"id":"m9","threadId":"t9","historyId":"8990","raw":"{{Raw("Ada <ada@example.com>", "Newest", "Last one.")}}"}"""),
                    _ => (HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                },
            };

            var stale = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.ReadOperation, new() { ["after_uid"] = "12" })));
            Assert.Equal((true, 1L, 9001L), ((bool?)stale.Fields["renumbered"], (long?)stale.Fields["uid_validity"], (long?)stale.Fields["uid_next"]));
            var newest = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GmailConnector.NewestOperation, new() { ["mailbox"] = "Label_7" })));
            Assert.Equal(("m9", 8990L, "Newest"), ((string?)newest.Fields["id"], (long?)newest.Fields["uid"], (string?)newest.Fields["subject"]));

            Assert.Equal("Give after_uid - a point in the label's history, Label status's uid_next less one.",
                (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, GmailConnector.ReadOperation, new() { ["after_uid"] = "0" })))).Message);
            Assert.StartsWith("'IN BOX' is not a label id", (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, GmailConnector.StatusOperation, new() { ["mailbox"] = "IN BOX" })))).Message);
        }

        [Fact]
        public async Task A_test_reads_the_mailboxs_address()
        {
            var google = new Google { Gmail = (_, _, _) => (HttpStatusCode.OK, """{"emailAddress":"ops@example.com","messagesTotal":12}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), "https://gmail.example.test/gmail/v1",
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in as ops@example.com."), (tested.Success, tested.Message));
            Assert.Equal(("https://gmail.example.test/gmail/v1/users/me/profile", "Bearer access-1"), (google.GmailRequests.Single().Url, google.GmailRequests.Single().Authorization));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "gmail", ConnectorCategory.Messaging), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "search", "send", "mark_read", "status", "read", "newest" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
