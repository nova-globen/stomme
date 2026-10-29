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
using Stomme.Modules.Connectors.Freshdesk;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Freshdesk (n8n's Freshdesk node: tickets and contacts, and a reply and a note) over a helpdesk's API: the key by
    /// Freshdesk's Basic pair (the key and X); any page's address cut back to the helpdesk; tickets by requester, company and change,
    /// sorted, page by page up to the most asked for, each a plain record its status, priority and source as words and its address in
    /// the agent's view; a ticket got (its requester with it), created (open, low and portal by default; words or numbers taken),
    /// updated, deleted, replied to and noted (private unless said); contacts by address, phone, company, state and change; a contact
    /// got, created, updated and deleted; what is not an id, an address, a word of Freshdesk's, a time, a flag, custom fields or one
    /// line refused before anything is sent; Freshdesk's refusal its description and first field's error; a test naming the agent.
    /// </summary>
    public sealed class FreshdeskConnectorTests
    {
        private const string Site = "https://acme.freshdesk.com";

        private const string Api = Site + "/api/v2";

        private sealed class FreshdeskApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(FreshdeskApi freshdesk, string operation, Dictionary<string, string> parameters, string endpoint = Site) =>
            Contexts.Extract(new StubEgress(freshdesk), endpoint, operation, parameters, null, new Dictionary<string, string> { ["api-key"] = "fd-key-1" });

        private static FreshdeskConnector Connector() => new();

        private static string Tickets(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"subject\":\"Printer " + id + "\",\"status\":2,\"priority\":3,\"source\":1,\"type\":\"Incident\",\"requester_id\":7,\"responder_id\":8,\"group_id\":9,\"company_id\":10,"
                + "\"tags\":[\"vip\",\"printer\"],\"custom_fields\":{\"cf_order\":\"1001\"},\"due_by\":\"2026-10-31T17:00:00Z\",\"created_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\"}")) + "]";

        [Fact]
        public async Task Tickets_and_contacts_are_read_page_by_page_up_to_the_most_asked_for_the_key_by_basic()
        {
            var freshdesk = new FreshdeskApi { Answer = (_, url) => url.EndsWith("&page=1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Tickets(1, 100)) : (HttpStatusCode.OK, Tickets(101, 50)) };
            var records = await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.TicketsOperation, new()
            {
                ["requester_email"] = "ada@example.com", ["requester_id"] = "7", ["company_id"] = "10", ["updated_since"] = "2026-10-01T02:00:00+02:00", ["order_by"] = "Updated_At", ["order_type"] = "desc", ["max"] = "150",
            }, endpoint: "https://acme.freshdesk.com/a/tickets/filters/all_tickets"));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal(2, freshdesk.Requests.Count);
            Assert.Equal(Api + "/tickets?email=ada%40example.com&requester_id=7&company_id=10&updated_since=2026-10-01T00%3A00%3A00Z&order_by=updated_at&order_type=desc&per_page=100&page=1", freshdesk.Requests[0].Url); // a page's address: the helpdesk's
            Assert.EndsWith("&per_page=50&page=2", freshdesk.Requests[1].Url, StringComparison.Ordinal); // a page no bigger than what is left
            Assert.All(freshdesk.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fd-key-1:X")), request.Authorization));
            var ticket = records[0].Fields;
            Assert.Equal((1L, Site + "/a/tickets/1", "Printer 1", "open", "high", "email", "Incident", 7L, "vip, printer", "{\"cf_order\":\"1001\"}"),
                ((long?)ticket["id"], (string?)ticket["url"], (string?)ticket["subject"], (string?)ticket["status"], (string?)ticket["priority"], (string?)ticket["source"], (string?)ticket["type"], (long?)ticket["requester_id"], (string?)ticket["tags"], (string?)ticket["custom_fields"]));

            var ten = new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.OK, Tickets(1, 100)) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, FreshdeskConnector.TicketsOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(10, ten.Requests.Count);
            Assert.Equal(Api + "/tickets?per_page=100&page=1", ten.Requests[0].Url);

            var contacts = new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":7,\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"phone\":\"+46406280401\",\"company_id\":10,\"job_title\":\"Engineer\",\"active\":true,\"tags\":[\"vip\"]}]") };
            var contact = Assert.Single(await Contexts.DrainAsync(Connector(), Context(contacts, FreshdeskConnector.ContactsOperation, new()
            {
                ["email"] = "ada@example.com", ["phone"] = "+46406280401", ["mobile"] = "+46701740605", ["company_id"] = "10", ["state"] = "Verified", ["updated_since"] = "2026-10-01",
            }, endpoint: "http://127.0.0.1:5273"))).Fields; // a short page the last
            Assert.Equal("http://127.0.0.1:5273/api/v2/contacts?email=ada%40example.com&phone=%2B46406280401&mobile=%2B46701740605&company_id=10&state=verified&_updated_since=2026-10-01T00%3A00%3A00Z&per_page=100&page=1", Assert.Single(contacts.Requests).Url);
            Assert.Equal((7L, "Ada Lovelace", "ada@example.com", 10L, "Engineer", true, "vip"), ((long?)contact["id"], (string?)contact["name"], (string?)contact["email"], (long?)contact["company_id"], (string?)contact["job_title"], (bool?)contact["active"], (string?)contact["tags"]));
        }

        [Fact]
        public async Task A_ticket_is_got_created_updated_deleted_replied_to_and_noted_and_a_contact_the_same()
        {
            var freshdesk = new FreshdeskApi
            {
                Answer = (method, url) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : url.EndsWith("/reply", StringComparison.Ordinal) || url.EndsWith("/notes", StringComparison.Ordinal) ? (HttpStatusCode.Created, "{\"id\":501,\"ticket_id\":1001,\"private\":" + (url.EndsWith("/notes", StringComparison.Ordinal) ? "true" : "false") + ",\"body\":\"<p>Looking.</p>\",\"body_text\":\"Looking.\",\"created_at\":\"2026-10-12T08:00:00Z\"}")
                    : url.Contains("/contacts", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":7,\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}")
                    : (HttpStatusCode.OK, Tickets(1001, 1)[1..^1].Replace("\"subject\"", "\"requester\":{\"email\":\"ada@example.com\"},\"description_text\":\"It broke.\",\"subject\"", StringComparison.Ordinal)),
            };

            var got = Assert.Single(await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.GetTicketOperation, new() { ["ticket"] = "1001" }))).Fields;
            Assert.Equal((HttpMethod.Get, Api + "/tickets/1001?include=requester"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url));
            Assert.Equal(("ada@example.com", "It broke."), ((string?)got["requester_email"], (string?)got["description"]));

            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.CreateTicketOperation, new() { ["email"] = "ada@example.com", ["subject"] = "Printer on fire", ["description"] = "<p>It broke.</p>" }));
            Assert.Equal((HttpMethod.Post, Api + "/tickets", "{\"email\":\"ada@example.com\",\"subject\":\"Printer on fire\",\"description\":\"<p>It broke.</p>\",\"status\":2,\"priority\":1,\"source\":2}"),
                (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body)); // open, low and portal by default

            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.CreateTicketOperation, new()
            {
                ["requester_id"] = "7", ["name"] = "Ada", ["subject"] = "Printer on fire", ["description"] = "x", ["status"] = "Pending", ["priority"] = "4", ["source"] = "feedback widget", ["type"] = "Incident", ["tags"] = "vip, printer", ["cc_emails"] = "boss@example.com",
                ["group_id"] = "9", ["responder_id"] = "8", ["company_id"] = "10", ["product_id"] = "11", ["due_by"] = "2026-10-31T19:00:00+02:00", ["custom_fields"] = "{\"cf_order\": \"1001\"}",
            }));
            Assert.Equal("{\"requester_id\":7,\"name\":\"Ada\",\"subject\":\"Printer on fire\",\"description\":\"x\",\"status\":3,\"priority\":4,\"source\":9,\"type\":\"Incident\",\"tags\":[\"vip\",\"printer\"],\"cc_emails\":[\"boss@example.com\"],\"group_id\":9,\"responder_id\":8,\"company_id\":10,\"product_id\":11,\"due_by\":\"2026-10-31T17:00:00Z\",\"custom_fields\":{\"cf_order\":\"1001\"}}",
                freshdesk.Requests[^1].Body); // words and numbers Freshdesk's numbers, times in UTC

            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["status"] = "resolved", ["responder_id"] = "12" }));
            Assert.Equal((HttpMethod.Put, Api + "/tickets/1001", "{\"status\":4,\"responder_id\":12}"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body)); // no default on a change

            Assert.Equal((1001L, true), Fields(Assert.Single(await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.DeleteTicketOperation, new() { ["ticket"] = "1001" })))));
            Assert.Equal((HttpMethod.Delete, Api + "/tickets/1001"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url));

            var reply = Assert.Single(await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.ReplyOperation, new() { ["ticket"] = "1001", ["body"] = "<p>Looking.</p>" })));
            Assert.Equal((HttpMethod.Post, Api + "/tickets/1001/reply", "{\"body\":\"<p>Looking.</p>\"}"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body));
            Assert.Equal((501L, 1001L, false, "Looking."), ((long?)reply.Fields["id"], (long?)reply.Fields["ticket_id"], (bool?)reply.Fields["private"], (string?)reply.Fields["body"]));
            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.AddNoteOperation, new() { ["ticket"] = "1001", ["body"] = "Called." }));
            Assert.Equal((Api + "/tickets/1001/notes", "{\"body\":\"Called.\",\"private\":true}"), (freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body)); // private unless said
            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.AddNoteOperation, new() { ["ticket"] = "1001", ["body"] = "Called.", ["private"] = "FALSE" }));
            Assert.Equal("{\"body\":\"Called.\",\"private\":false}", freshdesk.Requests[^1].Body);

            Assert.Equal("Ada Lovelace", Assert.Single(await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.GetContactOperation, new() { ["contact"] = "7" }))).Fields["name"]);
            Assert.Equal((HttpMethod.Get, Api + "/contacts/7"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url));
            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.CreateContactOperation, new()
            {
                ["name"] = "Ada Lovelace", ["email"] = "ada@example.com", ["mobile"] = "+46701740605", ["job_title"] = "Engineer", ["company_id"] = "10", ["tags"] = "vip", ["other_emails"] = "a@example.com, b@example.com", ["custom_fields"] = "{\"cf_tier\": \"gold\"}",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/contacts", "{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"mobile\":\"+46701740605\",\"job_title\":\"Engineer\",\"company_id\":10,\"tags\":[\"vip\"],\"other_emails\":[\"a@example.com\",\"b@example.com\"],\"custom_fields\":{\"cf_tier\":\"gold\"}}"),
                (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.CreateContactOperation, new() { ["name"] = "Grace", ["phone"] = "+46406280401" }));
            Assert.Equal("{\"name\":\"Grace\",\"phone\":\"+46406280401\"}", freshdesk.Requests[^1].Body); // a phone instead of an address
            await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.UpdateContactOperation, new() { ["contact"] = "7", ["job_title"] = "Lead" }));
            Assert.Equal((HttpMethod.Put, Api + "/contacts/7", "{\"job_title\":\"Lead\"}"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url, freshdesk.Requests[^1].Body));
            Assert.Equal((7L, true), Fields(Assert.Single(await Contexts.DrainAsync(Connector(), Context(freshdesk, FreshdeskConnector.DeleteContactOperation, new() { ["contact"] = "7" })))));
            Assert.Equal((HttpMethod.Delete, Api + "/contacts/7"), (freshdesk.Requests[^1].Method, freshdesk.Requests[^1].Url));
        }

        private static (long?, bool?) Fields(ConnectorRecord record) => ((long?)record.Fields["id"], (bool?)record.Fields["deleted"]);

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_freshdesks_refusal_is_its_words()
        {
            var freshdesk = new FreshdeskApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(freshdesk, operation, parameters, endpoint)))).Message;

            Assert.Equal("'10/../x' is not a Freshdesk ticket's id (digits).", await Refused(FreshdeskConnector.GetTicketOperation, new() { ["ticket"] = "10/../x" }));
            Assert.Equal("'ada' is not an address.", await Refused(FreshdeskConnector.TicketsOperation, new() { ["requester_email"] = "ada" }));
            Assert.Equal("'abc' is not a Freshdesk company's id (digits).", await Refused(FreshdeskConnector.TicketsOperation, new() { ["company_id"] = "abc" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-01T00:00:00Z.", await Refused(FreshdeskConnector.TicketsOperation, new() { ["updated_since"] = "yesterday" }));
            Assert.StartsWith("'priority' is not a field to sort by", await Refused(FreshdeskConnector.TicketsOperation, new() { ["order_by"] = "priority" }));
            Assert.StartsWith("'up' is not a sort order", await Refused(FreshdeskConnector.TicketsOperation, new() { ["order_type"] = "up" }));
            Assert.StartsWith("'gone' is not a contact's state", await Refused(FreshdeskConnector.ContactsOperation, new() { ["state"] = "gone" }));
            Assert.Equal("Give the requester - an address, a requester_id, a phone or a unique_external_id.", await Refused(FreshdeskConnector.CreateTicketOperation, new() { ["subject"] = "x", ["description"] = "y" }));
            Assert.Equal("Give the ticket's subject.", await Refused(FreshdeskConnector.CreateTicketOperation, new() { ["email"] = "ada@example.com", ["description"] = "y" }));
            Assert.Equal("Give the ticket's description.", await Refused(FreshdeskConnector.CreateTicketOperation, new() { ["email"] = "ada@example.com", ["subject"] = "x" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(FreshdeskConnector.CreateTicketOperation, new() { ["email"] = "ada@example.com", ["subject"] = "a\nb", ["description"] = "y" }));
            Assert.Equal("'stuck' is not a status - open, pending, resolved, closed.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["status"] = "stuck" }));
            Assert.Equal("'6' is not a priority - low, medium, high, urgent.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["priority"] = "6" }));
            Assert.Equal("'fax' is not a source - email, portal, phone, chat, feedback_widget, outbound_email.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["source"] = "fax" }));
            Assert.Equal("'boss' is not an address.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["cc_emails"] = "a@example.com, boss" }));
            Assert.Equal("The custom fields are not JSON - {\"cf_order\": \"1001\"}.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["custom_fields"] = "cf_order=1001" }));
            Assert.Equal("The custom fields are a JSON object of the helpdesk's fields - {\"cf_order\": \"1001\"}.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001", ["custom_fields"] = "{}" }));
            Assert.Equal("Give something to change on the ticket.", await Refused(FreshdeskConnector.UpdateTicketOperation, new() { ["ticket"] = "1001" }));
            Assert.Equal("Give the reply.", await Refused(FreshdeskConnector.ReplyOperation, new() { ["ticket"] = "1001" }));
            Assert.Equal("'maybe' is not true or false (private).", await Refused(FreshdeskConnector.AddNoteOperation, new() { ["ticket"] = "1001", ["body"] = "x", ["private"] = "maybe" }));
            Assert.Equal("Give the contact's name.", await Refused(FreshdeskConnector.CreateContactOperation, new() { ["email"] = "ada@example.com" }));
            Assert.Equal("Give the contact's address - or a phone, a mobile or a unique_external_id.", await Refused(FreshdeskConnector.CreateContactOperation, new() { ["name"] = "Ada" }));
            Assert.Equal("Give something to change on the contact.", await Refused(FreshdeskConnector.UpdateContactOperation, new() { ["contact"] = "7" }));
            Assert.Equal("'nope' is not an address.", await Refused(FreshdeskConnector.UpdateContactOperation, new() { ["contact"] = "7", ["other_emails"] = "a@example.com, nope" }));
            Assert.StartsWith("Freshdesk has no operation 'merge_tickets'", await Refused("merge_tickets", new()));
            Assert.Equal("'ftp://x.example' is not a Freshdesk helpdesk's address - https://your-domain.freshdesk.com.", await Refused(FreshdeskConnector.TicketsOperation, new(), "ftp://x.example"));
            Assert.Empty(freshdesk.Requests);

            var refusing = new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"description\":\"Validation failed\",\"errors\":[{\"field\":\"email\",\"message\":\"There is no contact matching the given email\",\"code\":\"invalid_value\"}]}") };
            Assert.Equal("HTTP 400: Validation failed - email: There is no contact matching the given email",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, FreshdeskConnector.CreateTicketOperation, new() { ["email"] = "nobody@example.com", ["subject"] = "x", ["description"] = "y" })))).Message);
            var denied = new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"code\":\"access_denied\",\"message\":\"You are not authorized to perform this action.\"}") };
            Assert.Equal("HTTP 403: You are not authorized to perform this action.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(denied, FreshdeskConnector.DeleteContactOperation, new() { ["contact"] = "7" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_agent()
        {
            var freshdesk = new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":8,\"available\":true,\"contact\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@acme.example\"}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(freshdesk), Site + "/a/dashboard/default", credentials: new Dictionary<string, string> { ["api-key"] = "fd-key-1" }));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@acme.example)."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/agents/me", freshdesk.Requests[0].Url);
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new FreshdeskApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"code\":\"invalid_credentials\",\"message\":\"You have to be logged in to perform this action.\"}") }), Site, credentials: new Dictionary<string, string> { ["api-key"] = "fd-key-1" }));
            Assert.Equal((false, "HTTP 401: You have to be logged in to perform this action."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "freshdesk", ConnectorCategory.Crm), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "tickets", "get_ticket", "create_ticket", "update_ticket", "delete_ticket", "reply", "add_note", "contacts", "get_contact", "create_contact", "update_contact", "delete_contact" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
