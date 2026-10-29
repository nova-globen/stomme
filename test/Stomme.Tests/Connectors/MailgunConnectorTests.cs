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
using Stomme.Modules.Connectors.Mailgun;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Mailgun (n8n's Mailgun node: an e-mail sent): the key as HTTP Basic, the sending domain a setting; an e-mail sent as
    /// a form with its sender, recipients, copies, reply-to, subject, text and HTML as written, or a stored template and its
    /// variables, tags, headers, a delivery time and the test mode; the domain's events page by page by Mailgun's own next link -
    /// followed only under the domain's events - up to the most asked for, each a record; bounces, unsubscribes and complaints, a
    /// suppression deleted, the domains; what could go wrong refused before anything is sent; Mailgun's refusal in its words; a
    /// test naming the domain.
    /// </summary>
    public sealed class MailgunConnectorTests
    {
        private const string Api = "https://api.mailgun.example.test/v3";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "key-1" };

        private static readonly Dictionary<string, string> Settings = new() { ["domain"] = "MG.Example.com" };

        private sealed class Mailgun : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Mailgun mailgun, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(mailgun), Api, operation, parameters, settings: Settings, credentials: Credentials);

        private static string Events(int from, int count, string? next) =>
            "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"e" + n + "\",\"event\":\"failed\",\"timestamp\":1760259600.123,\"recipient\":\"c" + n + "@example.com\",\"severity\":\"permanent\",\"reason\":\"bounce\","
                + "\"delivery-status\":{\"code\":550,\"message\":\"\",\"description\":\"No such mailbox\"},\"tags\":[\"orders\",\"eu\"],\"message\":{\"headers\":{\"message-id\":\"m" + n + "@mg.example.com\",\"subject\":\"Café\",\"from\":\"shop@mg.example.com\"}}}"))
            + "],\"paging\":{\"next\":\"" + (next ?? Api + "/mg.example.com/events/end") + "\",\"previous\":\"" + Api + "/mg.example.com/events/prev\"}}";

        [Fact]
        public async Task An_email_is_sent_as_a_form_with_a_body_or_a_template()
        {
            var mailgun = new Mailgun { Answer = (_, _) => (HttpStatusCode.OK, """{"id":"<20261012.1@mg.example.com>","message":"Queued. Thank you."}""") };
            (string, string, string?) Last() => (mailgun.Requests[^1].Method.Method, mailgun.Requests[^1].Url, mailgun.Requests[^1].Body);

            var sent = Assert.Single(await Contexts.DrainAsync(new MailgunConnector(), Context(mailgun, MailgunConnector.SendEmailOperation, new()
            {
                ["from"] = "Café Shop <shop@mg.example.com>", ["to"] = "Ada <ada@example.com>,lin@example.com", ["cc"] = "boss@example.com", ["bcc"] = "audit@example.com", ["reply_to"] = "help@example.com",
                ["subject"] = "Your order", ["text"] = "  Café\nThanks\n", ["html"] = "<p>Café &amp; more</p>", ["tags"] = "orders, eu", ["headers"] = "{\"X-Order\": \"1001\", \"X-Count\": 2}",
                ["delivery_time"] = "2026-10-20T09:00:00+02:00", ["test_mode"] = "TRUE",
            })));
            Assert.Equal(("POST", Api + "/mg.example.com/messages"), (Last().Item1, Last().Item2)); // the domain in lower case
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:key-1")), mailgun.Requests[^1].Authorization);
            Assert.Equal("from=Caf%C3%A9+Shop+%3Cshop%40mg.example.com%3E&to=Ada+%3Cada%40example.com%3E%2C+lin%40example.com&cc=boss%40example.com&bcc=audit%40example.com&subject=Your+order"
                + "&text=++Caf%C3%A9%0AThanks%0A&html=%3Cp%3ECaf%C3%A9+%26amp%3B+more%3C%2Fp%3E&h%3AReply-To=help%40example.com&h%3AX-Order=1001&h%3AX-Count=2&o%3Atag=orders&o%3Atag=eu"
                + "&o%3Adeliverytime=Tue%2C+20+Oct+2026+07%3A00%3A00+%2B0000&o%3Atestmode=yes", Last().Item3); // the text as written; a time in RFC 2822, UTC
            Assert.Equal(("<20261012.1@mg.example.com>", "Queued. Thank you."), ((string?)sent.Fields["id"], (string?)sent.Fields["message"]));

            await Contexts.DrainAsync(new MailgunConnector(), Context(mailgun, MailgunConnector.SendEmailOperation, new() { ["from"] = "shop@mg.example.com", ["to"] = "ada@example.com", ["template"] = "order-shipped", ["variables"] = "{\"order\": \"1001\", \"name\": \"Café\"}", ["test_mode"] = "false" }));
            Assert.Equal("from=shop%40mg.example.com&to=ada%40example.com&template=order-shipped&t%3Avariables=%7B%22order%22%3A%221001%22%2C%22name%22%3A%22Caf%C3%A9%22%7D", Last().Item3); // a template's own subject; no test mode
        }

        [Fact]
        public async Task Events_and_suppressions_are_read_page_by_page_by_mailguns_own_link()
        {
            var mailgun = new Mailgun
            {
                Answer = (_, url) => url.EndsWith("/events/p2", StringComparison.Ordinal) ? (HttpStatusCode.OK, Events(301, 20, null))
                    : url.EndsWith("/events/end", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"items":[],"paging":{"next":"https://api.mailgun.example.test/v3/mg.example.com/events/end"}}""")
                    : (HttpStatusCode.OK, Events(1, 300, Api + "/mg.example.com/events/p2")),
            };
            var events = await Contexts.DrainAsync(new MailgunConnector(), Context(mailgun, MailgunConnector.EventsOperation, new()
            {
                ["event"] = "Failed", ["recipient"] = "ada+shop@example.com", ["begin"] = "2026-10-01", ["end"] = "2026-10-12T11:00:00+02:00", ["order"] = "asc",
            }));

            Assert.Equal(320, events.Count);
            Assert.Equal(new[]
            {
                Api + "/mg.example.com/events?event=failed&recipient=ada%2Bshop%40example.com&begin=1790812800&end=1791795600&ascending=yes&limit=300",
                Api + "/mg.example.com/events/p2",
                Api + "/mg.example.com/events/end",
            }, mailgun.Requests.Select(request => request.Url)); // an empty page the end
            var first = events[0].Fields;
            Assert.Equal(("e1", "failed", "2025-10-12T09:00:00.123Z", "c1@example.com", "m1@mg.example.com", "Café", "shop@mg.example.com", "permanent", "bounce", 550L, "No such mailbox", "orders, eu"),
                ((string?)first["id"], (string?)first["event"], (string?)first["time"], (string?)first["recipient"], (string?)first["message_id"], (string?)first["subject"], (string?)first["from"],
                    (string?)first["severity"], (string?)first["reason"], (long?)first["status_code"], (string?)first["status"], (string?)first["tags"]));

            var few = new Mailgun { Answer = (_, _) => (HttpStatusCode.OK, Events(1, 300, Api + "/mg.example.com/events/again")) };
            Assert.Equal(310, (await Contexts.DrainAsync(new MailgunConnector(), Context(few, MailgunConnector.EventsOperation, new() { ["max"] = "310" }))).Count);
            Assert.Equal(new[] { Api + "/mg.example.com/events?ascending=no&limit=300", Api + "/mg.example.com/events/again" }, few.Requests.Select(request => request.Url)); // newest first by default

            var elsewhere = new Mailgun { Answer = (_, _) => (HttpStatusCode.OK, Events(1, 10, "https://elsewhere.example.test/v3/mg.example.com/events/p2")) };
            Assert.Equal(10, (await Contexts.DrainAsync(new MailgunConnector(), Context(elsewhere, MailgunConnector.EventsOperation, new()))).Count);
            Assert.Single(elsewhere.Requests); // a next link off the domain's events is not followed
            var otherDomain = new Mailgun { Answer = (_, _) => (HttpStatusCode.OK, Events(1, 10, Api + "/other.example.com/events/p2")) };
            await Contexts.DrainAsync(new MailgunConnector(), Context(otherDomain, MailgunConnector.EventsOperation, new()));
            Assert.Single(otherDomain.Requests);

            var lists = new Mailgun
            {
                Answer = (method, url) => method == HttpMethod.Delete ? (HttpStatusCode.OK, """{"address":"ada+x@example.com","message":"Bounced address has been removed"}""")
                    : url.Contains("/domains", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"total_count":1,"items":[{"name":"mg.example.com","state":"active","type":"custom","created_at":"Mon, 05 Oct 2026 09:00:00 GMT"}]}""")
                    : url.Contains("page=next", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"items":[],"paging":{}}""")
                    : url.Contains("/bounces?", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"items":[{"address":"ada@example.com","code":"550","error":"No such mailbox","created_at":"Mon, 05 Oct 2026 09:00:00 UTC"}],"paging":{"next":"https://api.mailgun.example.test/v3/mg.example.com/bounces?page=next&address=ada%40example.com"}}""")
                    : (HttpStatusCode.OK, """{"items":[{"address":"lin@example.com","tags":["*"],"created_at":"Tue, 06 Oct 2026 09:00:00 UTC"}],"paging":{}}"""),
            };
            var bounce = Assert.Single(await Contexts.DrainAsync(new MailgunConnector(), Context(lists, MailgunConnector.BouncesOperation, new())));
            Assert.Equal(new[] { Api + "/mg.example.com/bounces?limit=1000", Api + "/mg.example.com/bounces?page=next&address=ada%40example.com" }, lists.Requests.Select(request => request.Url));
            Assert.Equal(("ada@example.com", "550", "No such mailbox"), ((string?)bounce.Fields["address"], (string?)bounce.Fields["code"], (string?)bounce.Fields["error"]));
            var unsubscribed = Assert.Single(await Contexts.DrainAsync(new MailgunConnector(), Context(lists, MailgunConnector.UnsubscribesOperation, new() { ["max"] = "5" })));
            Assert.Equal(Api + "/mg.example.com/unsubscribes?limit=5", lists.Requests[^1].Url);
            Assert.Equal(("lin@example.com", "*"), ((string?)unsubscribed.Fields["address"], (string?)unsubscribed.Fields["tags"]));
            await Contexts.DrainAsync(new MailgunConnector(), Context(lists, MailgunConnector.ComplaintsOperation, new()));
            Assert.Equal(Api + "/mg.example.com/complaints?limit=1000", lists.Requests[^1].Url);
            var removed = Assert.Single(await Contexts.DrainAsync(new MailgunConnector(), Context(lists, MailgunConnector.DeleteSuppressionOperation, new() { ["list"] = "Bounces", ["address"] = "ada+x@example.com" })));
            Assert.Equal(("DELETE", Api + "/mg.example.com/bounces/ada%2Bx%40example.com"), (lists.Requests[^1].Method.Method, lists.Requests[^1].Url));
            Assert.Equal("Bounced address has been removed", (string?)removed.Fields["message"]);
            var domain = Assert.Single(await Contexts.DrainAsync(new MailgunConnector(), Context(lists, MailgunConnector.DomainsOperation, new())));
            Assert.Equal(Api + "/domains?limit=1000", lists.Requests[^1].Url);
            Assert.Equal(("mg.example.com", "active"), ((string?)domain.Fields["name"], (string?)domain.Fields["state"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_mailguns_refusal_is_its_words()
        {
            var mailgun = new Mailgun();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MailgunConnector(), Contexts.Extract(new StubEgress(mailgun), Api, operation, parameters, settings: settings ?? Settings, credentials: Credentials)))).Message;
            var mail = new Dictionary<string, string> { ["from"] = "shop@mg.example.com", ["to"] = "ada@example.com", ["subject"] = "x", ["text"] = "y" };
            Dictionary<string, string> Mail(string name, string value) => new(mail) { [name] = value };

            Assert.Equal("Give the sending domain - mg.example.com.", await Refused(MailgunConnector.DomainsOperation, new(), new Dictionary<string, string>()));
            Assert.Equal("'mg example' is not a domain - mg.example.com.", await Refused(MailgunConnector.DomainsOperation, new(), new Dictionary<string, string> { ["domain"] = "mg example" }));
            Assert.Equal("'../domains' is not a domain - mg.example.com.", await Refused(MailgunConnector.BouncesOperation, new(), new Dictionary<string, string> { ["domain"] = "../domains" }));
            Assert.Equal("Give the sender - Shop <shop@mg.example.com>.", await Refused(MailgunConnector.SendEmailOperation, new() { ["to"] = "ada@example.com" }));
            Assert.Equal("Give one address (sender).", await Refused(MailgunConnector.SendEmailOperation, Mail("from", "a@example.com, b@example.com")));
            Assert.Equal("'Ada <ada at example.com>' is not an address (recipient) - Ada <ada@example.com>, or ada@example.com.", await Refused(MailgunConnector.SendEmailOperation, Mail("to", "Ada <ada at example.com>")));
            Assert.Equal("'boss' is not an address (cc) - Ada <ada@example.com>, or ada@example.com.", await Refused(MailgunConnector.SendEmailOperation, Mail("cc", "boss")));
            Assert.Equal("Give one address (reply-to).", await Refused(MailgunConnector.SendEmailOperation, Mail("reply_to", "a@example.com,b@example.com")));
            Assert.Equal("Give a body - text or HTML - or a template.", await Refused(MailgunConnector.SendEmailOperation, new() { ["from"] = "shop@mg.example.com", ["to"] = "ada@example.com", ["subject"] = "x" }));
            Assert.Equal("Give the subject.", await Refused(MailgunConnector.SendEmailOperation, new() { ["from"] = "shop@mg.example.com", ["to"] = "ada@example.com", ["text"] = "y" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(MailgunConnector.SendEmailOperation, Mail("subject", "a\nb")));
            Assert.Equal("'order shipped' is not a template's name.", await Refused(MailgunConnector.SendEmailOperation, Mail("template", "order shipped")));
            Assert.Equal("The template variables are not a JSON object - {\"order\": \"1001\"}.", await Refused(MailgunConnector.SendEmailOperation, Mail("variables", "order=1001")));
            Assert.Equal("The headers are not a JSON object - {\"X-Order\": \"1001\"}.", await Refused(MailgunConnector.SendEmailOperation, Mail("headers", "X-Order: 1001")));
            Assert.Equal("'X Order' is not a header's name - X-Order.", await Refused(MailgunConnector.SendEmailOperation, Mail("headers", "{\"X Order\": \"1\"}")));
            Assert.Equal("The header X-Note has a line break - one line only.", await Refused(MailgunConnector.SendEmailOperation, Mail("headers", "{\"X-Note\": \"a\\nb\"}")));
            Assert.Equal("Give at most three tags - Mailgun keeps no more.", await Refused(MailgunConnector.SendEmailOperation, Mail("tags", "a, b, c, d")));
            Assert.Equal("'soon' is not a time (send at) - 2026-10-20T09:00:00+02:00.", await Refused(MailgunConnector.SendEmailOperation, Mail("delivery_time", "soon")));
            Assert.Equal("'yes' is not true or false (test mode).", await Refused(MailgunConnector.SendEmailOperation, Mail("test_mode", "yes")));
            Assert.Equal("'sent' is not an event - accepted, rejected, delivered, failed, opened, clicked, unsubscribed, complained or stored.", await Refused(MailgunConnector.EventsOperation, new() { ["event"] = "sent" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(MailgunConnector.EventsOperation, new() { ["recipient"] = "ada" }));
            Assert.Equal("'yesterday' is not a time (from) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(MailgunConnector.EventsOperation, new() { ["begin"] = "yesterday" }));
            Assert.Equal("'later' is not a time (to) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(MailgunConnector.EventsOperation, new() { ["end"] = "later" }));
            Assert.Equal("'newest' is not an order - desc or asc.", await Refused(MailgunConnector.EventsOperation, new() { ["order"] = "newest" }));
            Assert.Equal("Give the list - bounces, unsubscribes or complaints.", await Refused(MailgunConnector.DeleteSuppressionOperation, new() { ["address"] = "ada@example.com" }));
            Assert.Equal("'whitelists' is not a list - bounces, unsubscribes or complaints.", await Refused(MailgunConnector.DeleteSuppressionOperation, new() { ["list"] = "whitelists", ["address"] = "ada@example.com" }));
            Assert.Equal("Give the address.", await Refused(MailgunConnector.DeleteSuppressionOperation, new() { ["list"] = "bounces" }));
            Assert.Equal("'ada' is not an e-mail address.", await Refused(MailgunConnector.DeleteSuppressionOperation, new() { ["list"] = "bounces", ["address"] = "ada" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MailgunConnector.BouncesOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Mailgun has no operation 'lists' - send_email, events,", await Refused("lists", new()));
            Assert.Empty(mailgun.Requests); // nothing sent

            var refusing = new Mailgun { Answer = (_, _) => (HttpStatusCode.BadRequest, """{"message":"to parameter is not a valid address. please check documentation"}""") };
            Assert.Equal("HTTP 400: to parameter is not a valid address. please check documentation", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MailgunConnector(), Context(refusing, MailgunConnector.SendEmailOperation, mail)))).Message);
            var forbidden = new Mailgun { Answer = (_, _) => (HttpStatusCode.Unauthorized, "Forbidden") };
            Assert.Equal("HTTP 401: Forbidden", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MailgunConnector(), Context(forbidden, MailgunConnector.DomainsOperation, new())))).Message); // Mailgun's plain-text refusal
        }

        [Fact]
        public async Task A_test_names_the_domain_and_its_state()
        {
            var mailgun = new Mailgun { Answer = (_, _) => (HttpStatusCode.OK, """{"domain":{"name":"mg.example.com","state":"active"},"receiving_dns_records":[],"sending_dns_records":[]}""") };
            var tested = await new MailgunConnector().TestConnectionAsync(Contexts.Test(new StubEgress(mailgun), Api, settings: Settings, credentials: Credentials));
            Assert.Equal((true, "Signed in - mg.example.com (active)."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/domains/mg.example.com", mailgun.Requests[0].Url);
            var keyless = await new MailgunConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Mailgun()), Api, settings: Settings, credentials: new Dictionary<string, string>()));
            Assert.Equal((false, "The Mailgun connection has no API key."), (keyless.Success, keyless.Message));
            Assert.Equal((AuthenticationType.ApiKey, "mailgun"), (new MailgunConnector().AuthType, new MailgunConnector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Marketing }, new MailgunConnector().Categories);
            Assert.Equal(7, new MailgunConnector().Operations.Count);
        }
    }
}
