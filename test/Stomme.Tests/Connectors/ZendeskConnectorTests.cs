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
using Stomme.Modules.Connectors.Zendesk;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Zendesk (n8n's Zendesk node: tickets found, got, created, updated and deleted, users and organizations found, got
    /// and created): the agent's address with /token and the API token by the Basic scheme; the tickets Zendesk's search finds -
    /// its query, a status, a priority, a group and a requester written into its syntax - page by page by its next_page, only
    /// while it stays the account's own search, up to the most asked for; a ticket made with its first comment and a requester,
    /// changed with a public reply or an internal note, deleted; users found and made, organizations listed and made; an id, a
    /// status, an address, custom fields and a change of nothing refused before anything is sent; Zendesk's refusal its error and
    /// details; a test naming the agent.
    /// </summary>
    public sealed class ZendeskConnectorTests
    {
        private const string Account = "https://acme.zendesk.example";

        private const string Api = Account + "/api/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "ada@acme.example", ["password"] = "zd-token-1" };

        private sealed class ZendeskApi : HttpMessageHandler
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

        private static ConnectorExtractContext Context(ZendeskApi zendesk, string operation, Dictionary<string, string> parameters, string endpoint = Account) =>
            Contexts.Extract(new StubEgress(zendesk), endpoint, operation, parameters, null, Credentials);

        private static ZendeskConnector Connector() => new();

        private static string Ticket(int id, string subject) =>
            "{\"id\":" + id + ",\"url\":\"" + Api + "/tickets/" + id + ".json\",\"subject\":\"" + subject + "\",\"description\":\"It broke.\",\"status\":\"open\",\"priority\":\"high\",\"type\":\"incident\","
            + "\"requester_id\":11,\"assignee_id\":null,\"group_id\":7,\"organization_id\":null,\"tags\":[\"vip\",\"billing\"],\"external_id\":null,\"via\":{\"channel\":\"email\"},"
            + "\"custom_fields\":[{\"id\":360001,\"value\":\"gold\"}],\"created_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\",\"result_type\":\"ticket\"}";

        private static string BasicFor(string user, string token) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + token));

        [Fact]
        public async Task The_tickets_zendesks_search_finds_are_read_page_by_page_by_its_own_next_page_up_to_the_most_asked_for()
        {
            var zendesk = new ZendeskApi
            {
                Answer = (_, url) => url.Contains("page=2", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"results\":[" + Ticket(3, "Third") + "," + Ticket(4, "Fourth") + "],\"next_page\":\"" + Api + "/search.json?page=3&per_page=3&query=type%3Aticket\",\"count\":5}")
                    : (HttpStatusCode.OK, "{\"results\":[" + Ticket(1, "First") + ",{\"id\":99,\"result_type\":\"user\",\"name\":\"Not a ticket\"}," + Ticket(2, "Second") + "],\"next_page\":\"" + Api + "/search.json?page=2&per_page=3&query=type%3Aticket\",\"count\":5}"),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.TicketsOperation, new()
            {
                ["query"] = "tags:vip", ["status"] = "Open", ["priority"] = "high", ["group_id"] = "7", ["requester"] = "grace@example.com", ["sort_by"] = "created_at", ["sort_order"] = "desc", ["max"] = "3",
            }, Account + "/agent/tickets/1"));

            Assert.Equal(new[] { "First", "Second", "Third" }, records.Select(record => (string?)record.Fields["subject"])); // a user among the finds skipped
            Assert.Equal(2, zendesk.Requests.Count); // the most asked for: no page more
            Assert.Equal(Api + "/search.json?query=type%3Aticket%20tags%3Avip%20status%3Aopen%20priority%3Ahigh%20group%3A7%20requester%3Agrace%40example.com&sort_by=created_at&sort_order=desc&per_page=3", zendesk.Requests[0].Url);
            Assert.Equal(Api + "/search.json?page=2&per_page=3&query=type%3Aticket", zendesk.Requests[1].Url);
            Assert.All(zendesk.Requests, request => Assert.Equal(BasicFor("ada@acme.example/token", "zd-token-1"), request.Authorization));
            var first = records[0].Fields;
            Assert.Equal((1L, Account + "/agent/tickets/1", "open", "high", "incident", 11L, 7L, "vip, billing", "email", "[{\"id\":360001,\"value\":\"gold\"}]"),
                ((long?)first["id"], (string?)first["url"], (string?)first["status"], (string?)first["priority"], (string?)first["type"], (long?)first["requester_id"], (long?)first["group_id"], (string?)first["tags"], (string?)first["channel"], (string?)first["custom_fields"]));
            Assert.Null(first["assignee_id"]);

            var elsewhere = new ZendeskApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"results\":[" + Ticket(1, "Only") + "],\"next_page\":\"https://elsewhere.example/api/v2/search.json?page=2\"}") };
            Assert.Single(await Contexts.DrainAsync(Connector(), Context(elsewhere, ZendeskConnector.TicketsOperation, new())));
            Assert.Single(elsewhere.Requests); // a next page off the account's own search is not followed
            Assert.Equal(Api + "/search.json?query=type%3Aticket&per_page=100", elsewhere.Requests[0].Url);
        }

        [Fact]
        public async Task A_ticket_is_got_made_changed_with_a_reply_or_a_note_and_deleted()
        {
            var zendesk = new ZendeskApi
            {
                Answer = (method, _) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "") : (HttpStatusCode.OK, "{\"ticket\":" + Ticket(5, "Printer on fire") + "}"),
            };

            var got = await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.GetTicketOperation, new() { ["ticket"] = "5" }));
            Assert.Equal((HttpMethod.Get, Api + "/tickets/5.json"), (zendesk.Requests[^1].Method, zendesk.Requests[^1].Url));
            Assert.Equal("Printer on fire", Assert.Single(got).Fields["subject"]);

            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.CreateTicketOperation, new()
            {
                ["subject"] = "Printer on fire", ["description"] = "It broke.\nPlease help.", ["requester_email"] = "grace@example.com", ["requester_name"] = "Grace Hopper", ["status"] = "new", ["priority"] = "Urgent",
                ["type"] = "incident", ["tags"] = "vip, billing", ["group_id"] = "7", ["assignee_email"] = "ada@acme.example", ["external_id"] = "ORD-42", ["custom_fields"] = "[{\"id\": 360001, \"value\": \"gold\"}]",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/tickets.json"), (zendesk.Requests[^1].Method, zendesk.Requests[^1].Url));
            Assert.Equal("{\"ticket\":{\"subject\":\"Printer on fire\",\"comment\":{\"body\":\"It broke.\\nPlease help.\"},\"requester\":{\"email\":\"grace@example.com\",\"name\":\"Grace Hopper\"},\"status\":\"new\",\"priority\":\"urgent\",\"type\":\"incident\","
                + "\"tags\":[\"vip\",\"billing\"],\"group_id\":7,\"assignee_email\":\"ada@acme.example\",\"external_id\":\"ORD-42\",\"custom_fields\":[{\"id\":360001,\"value\":\"gold\"}]}}", zendesk.Requests[^1].Body);

            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.UpdateTicketOperation, new() { ["ticket"] = "5", ["status"] = "solved", ["comment"] = "Fixed - try again." }));
            Assert.Equal((HttpMethod.Put, Api + "/tickets/5.json", "{\"ticket\":{\"comment\":{\"body\":\"Fixed - try again.\",\"public\":true},\"status\":\"solved\"}}"), (zendesk.Requests[^1].Method, zendesk.Requests[^1].Url, zendesk.Requests[^1].Body));
            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.UpdateTicketOperation, new() { ["ticket"] = "5", ["internal_note"] = "Called them." }));
            Assert.Equal("{\"ticket\":{\"comment\":{\"body\":\"Called them.\",\"public\":false}}}", zendesk.Requests[^1].Body);

            var removed = await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.DeleteTicketOperation, new() { ["ticket"] = "5" }));
            Assert.Equal((HttpMethod.Delete, Api + "/tickets/5.json"), (zendesk.Requests[^1].Method, zendesk.Requests[^1].Url));
            Assert.Equal((5L, true), ((long?)Assert.Single(removed).Fields["id"], (bool?)removed[0].Fields["deleted"]));
        }

        [Fact]
        public async Task Users_are_found_and_made_and_organizations_listed_and_made()
        {
            var zendesk = new ZendeskApi
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", _) when url.Contains("/users/search.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"users\":[{\"id\":11,\"name\":\"Grace Hopper\",\"email\":\"grace@example.com\",\"role\":\"end-user\",\"active\":true,\"tags\":[]}],\"next_page\":null}"),
                    ("POST", _) when url.EndsWith("/users.json", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"user\":{\"id\":12,\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"role\":\"end-user\",\"organization_id\":31}}"),
                    ("GET", _) when url.Contains("/organizations.json", StringComparison.Ordinal) && url.Contains("page=2", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"organizations\":[{\"id\":32,\"name\":\"Navy\",\"domain_names\":[\"navy.example\"]}],\"next_page\":null}"),
                    ("GET", _) when url.Contains("/organizations.json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"organizations\":[{\"id\":31,\"name\":\"Acme\",\"domain_names\":[\"acme.example\",\"acme.test\"],\"tags\":[\"gold\"]}],\"next_page\":\"" + Api + "/organizations.json?page=2&per_page=100\"}"),
                    ("POST", _) => (HttpStatusCode.Created, "{\"organization\":{\"id\":33,\"name\":\"Initech\",\"domain_names\":[\"initech.example\"]}}"),
                    _ => (HttpStatusCode.OK, "{\"user\":{\"id\":11,\"name\":\"Grace Hopper\"}}"),
                },
            };

            var users = await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.UsersOperation, new() { ["query"] = "grace@example.com" }));
            Assert.Equal(Api + "/users/search.json?query=grace%40example.com&per_page=100", zendesk.Requests[^1].Url);
            Assert.Equal(("Grace Hopper", "end-user", true), ((string?)Assert.Single(users).Fields["name"], (string?)users[0].Fields["role"], (bool?)users[0].Fields["active"]));

            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.GetUserOperation, new() { ["user"] = "11" }));
            Assert.Equal(Api + "/users/11.json", zendesk.Requests[^1].Url);

            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.CreateUserOperation, new() { ["name"] = "Ada Lovelace", ["email"] = "ada@example.com", ["role"] = "End-User", ["organization_id"] = "31" }));
            Assert.Equal("{\"user\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"role\":\"end-user\",\"organization_id\":31}}", zendesk.Requests[^1].Body);

            var organizations = await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.OrganizationsOperation, new()));
            Assert.Equal(new[] { "Acme", "Navy" }, organizations.Select(record => (string?)record.Fields["name"])); // both pages
            Assert.Equal("acme.example, acme.test", organizations[0].Fields["domain_names"]);

            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.GetOrganizationOperation, new() { ["organization"] = "31" }));
            Assert.Equal(Api + "/organizations/31.json", zendesk.Requests[^1].Url);
            await Contexts.DrainAsync(Connector(), Context(zendesk, ZendeskConnector.CreateOrganizationOperation, new() { ["name"] = "Initech", ["domain_names"] = "initech.example", ["tags"] = "silver, new" }));
            Assert.Equal("{\"organization\":{\"name\":\"Initech\",\"domain_names\":[\"initech.example\"],\"tags\":[\"silver\",\"new\"]}}", zendesk.Requests[^1].Body);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_zendesks_refusal_is_its_error()
        {
            var zendesk = new ZendeskApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Account) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(zendesk, operation, parameters, endpoint)))).Message;

            Assert.Equal("'5/../users' is not a Zendesk ticket's id (digits).", await Refused(ZendeskConnector.GetTicketOperation, new() { ["ticket"] = "5/../users" }));
            Assert.Equal("'x' is not a Zendesk group's id (digits).", await Refused(ZendeskConnector.TicketsOperation, new() { ["group_id"] = "x" }));
            Assert.StartsWith("'done' is not a status", await Refused(ZendeskConnector.TicketsOperation, new() { ["status"] = "done" }));
            Assert.StartsWith("'asap' is not a priority", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x", ["priority"] = "asap" }));
            Assert.StartsWith("'subject' is not a field to sort by", await Refused(ZendeskConnector.TicketsOperation, new() { ["sort_by"] = "subject" }));
            Assert.Equal("'grace' is not an address.", await Refused(ZendeskConnector.TicketsOperation, new() { ["requester"] = "grace" }));
            Assert.Equal("Give the ticket's description - its first comment.", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["subject"] = "x" }));
            Assert.Equal("The subject has a line break - one line only.", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["subject"] = "a\nb", ["description"] = "x" }));
            Assert.Equal("The custom fields are a list of an id (a number) and a value each - [{\"id\": 360001, \"value\": \"gold\"}].", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x", ["custom_fields"] = "{\"360001\": \"gold\"}" }));
            Assert.Equal("The custom fields are a list of an id (a number) and a value each - [{\"id\": 360001, \"value\": \"gold\"}].", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x", ["custom_fields"] = "[{\"id\": \"tier\", \"value\": \"gold\"}]" }));
            Assert.Equal("The custom fields are a list of an id (a number) and a value each - [{\"id\": 360001, \"value\": \"gold\"}].", await Refused(ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x", ["custom_fields"] = "[{\"id\": 360001}]" }));
            Assert.Equal("The custom fields are not JSON - [{\"id\": 360001, \"value\": \"gold\"}].",await Refused(ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x", ["custom_fields"] = "gold" }));
            Assert.Equal("Give something to change - a field, a public reply or an internal note.", await Refused(ZendeskConnector.UpdateTicketOperation, new() { ["ticket"] = "5" }));
            Assert.Equal("Give a public reply or an internal note - one change adds one comment.", await Refused(ZendeskConnector.UpdateTicketOperation, new() { ["ticket"] = "5", ["comment"] = "a", ["internal_note"] = "b" }));
            Assert.StartsWith("'owner' is not a role", await Refused(ZendeskConnector.CreateUserOperation, new() { ["name"] = "Ada", ["role"] = "owner" }));
            Assert.Equal("'not a url' is not a Zendesk account's address - https://your-subdomain.zendesk.com.", await Refused(ZendeskConnector.OrganizationsOperation, new(), "not a url"));
            Assert.Empty(zendesk.Requests);

            var invalid = new ZendeskApi { Answer = (_, _) => (HttpStatusCode.UnprocessableEntity, "{\"error\":\"RecordInvalid\",\"description\":\"Record validation errors\",\"details\":{\"requester\":[{\"description\":\"Requester: Email is invalid\",\"error\":\"InvalidValue\"}]}}") };
            Assert.Equal("HTTP 422: RecordInvalid - Record validation errors - Requester: Email is invalid", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(invalid, ZendeskConnector.CreateTicketOperation, new() { ["description"] = "x" })))).Message);
            var missing = new ZendeskApi { Answer = (_, _) => (HttpStatusCode.NotFound, "{\"error\":{\"title\":\"No help desk at acme.zendesk.example\",\"message\":\"There is no help desk configured at this address.\"}}") };
            Assert.Equal("HTTP 404: There is no help desk configured at this address.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, ZendeskConnector.GetTicketOperation, new() { ["ticket"] = "5" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_agent()
        {
            var zendesk = new ZendeskApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"user\":{\"id\":7,\"name\":\"Ada Lovelace\",\"role\":\"admin\"}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(zendesk), Account, credentials: new Dictionary<string, string> { ["username"] = "ada@acme.example/token", ["password"] = "zd-token-1" }));
            Assert.Equal((true, "Signed in as Ada Lovelace (admin)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users/me.json", BasicFor("ada@acme.example/token", "zd-token-1")), (zendesk.Requests[0].Url, zendesk.Requests[0].Authorization)); // /token not added twice
            var anonymous = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new ZendeskApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"user\":{\"id\":null,\"name\":\"Anonymous user\"}}") }), Account, credentials: Credentials));
            Assert.Equal((false, "Zendesk answered as an anonymous user - check the address and the API token."), (anonymous.Success, anonymous.Message));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new ZendeskApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"Couldn't authenticate you\"}") }), Account, credentials: Credentials));
            Assert.Equal((false, "HTTP 401: Couldn't authenticate you"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "zendesk", ConnectorCategory.Crm), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "tickets", "get_ticket", "create_ticket", "update_ticket", "delete_ticket", "users", "get_user", "create_user", "organizations", "get_organization", "create_organization" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
