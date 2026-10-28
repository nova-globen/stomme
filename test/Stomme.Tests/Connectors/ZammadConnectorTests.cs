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
using Stomme.Modules.Connectors.Zammad;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Zammad (n8n's Zammad node: its tickets, users, organizations and groups): every call the help desk's API v1 with
    /// the token in Zammad's Token header; a list read page by page, a short or empty page the end, no more than asked; a
    /// ticket got with its articles, created with its first article - a note or an e-mail - updated with an internal note and
    /// deleted; users searched and sorted, got, the token's own, created, updated and deleted; organizations and groups read,
    /// got, created, updated and deleted; custom fields as a JSON object; what could go wrong refused before anything is sent;
    /// Zammad's words on a failure; a test naming whose the token is.
    /// </summary>
    public sealed class ZammadConnectorTests
    {
        private const string Desk = "https://desk.kafe.example.test";

        private const string Api = Desk + "/api/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "zt_1" };

        private sealed class Zammad : HttpMessageHandler
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

        private static Task<List<ConnectorRecord>> Run(Zammad zammad, string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new ZammadConnector(), Contexts.Extract(new StubEgress(zammad), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Tickets(int first, int count) =>
            "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"number\":\"3100" + i + "\",\"title\":\"Fika " + i + "\",\"state_id\":2,\"preferences\":{\"channel_id\":1}}")) + "]";

        [Fact]
        public async Task Lists_are_read_page_by_page_and_tickets_kept()
        {
            var zammad = new Zammad
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/tickets?page=1&", StringComparison.Ordinal) => (HttpStatusCode.OK, Tickets(1, 100)),
                    ("GET", var u) when u.Contains("/tickets?page=2&", StringComparison.Ordinal) => (HttpStatusCode.OK, Tickets(101, 100)),
                    ("GET", var u) when u.Contains("/tickets?page=3&", StringComparison.Ordinal) => (HttpStatusCode.OK, Tickets(201, 30)),
                    ("GET", var u) when u.Contains("/tickets?page=", StringComparison.Ordinal) => (HttpStatusCode.OK, "[]"),
                    ("GET", var u) when u.Contains("/ticket_articles/by_ticket/", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"id\":9,\"body\":\"Fika?\",\"internal\":false}]"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"id\":7,\"title\":\"Fika\",\"state_id\":2}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{}"),
                    _ => (HttpStatusCode.Created, "{\"id\":7,\"number\":\"31007\",\"title\":\"Fika at 3\"}"),
                },
            };
            (string, string, string?) Last() => (zammad.Requests[^1].Method.Method, zammad.Requests[^1].Url, zammad.Requests[^1].Body);

            var read = await Run(zammad, ZammadConnector.TicketsOperation, new() { ["max"] = "150" });
            Assert.Equal(150, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/tickets?page=1&per_page=100", $"{Api}/tickets?page=2&per_page=100" }, zammad.Requests.Select(request => request.Url));
            Assert.All(zammad.Requests, request => Assert.Equal("Token token=zt_1", request.Authorization));
            Assert.Equal((1L, "31001", "{\"channel_id\":1}"), ((long?)read[0].Fields["id"], (string?)read[0].Fields["number"], (string?)read[0].Fields["preferences"]));
            zammad.Requests.Clear();
            Assert.Equal(230, (await Run(zammad, null!, new())).Count); // no operation: the tickets - a short page the end
            Assert.Equal(3, zammad.Requests.Count);
            zammad.Requests.Clear();
            Assert.Equal(5, (await Run(zammad, ZammadConnector.TicketsOperation, new() { ["max"] = "5" })).Count);
            Assert.Equal($"{Api}/tickets?page=1&per_page=5", Assert.Single(zammad.Requests).Url);
            var full = new Zammad { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=3", StringComparison.Ordinal) ? "[]" : Tickets(1, 100)) };
            Assert.Equal(200, (await Run(full, ZammadConnector.TicketsOperation, new())).Count); // an empty page the end
            Assert.Equal(3, full.Requests.Count);
            var endless = new Zammad { Answer = (_, _) => (HttpStatusCode.OK, Tickets(1, 100)) };
            Assert.Equal(1000, (await Run(endless, ZammadConnector.TicketsOperation, new())).Count); // 1,000 at most when no max is given

            var ticket = Assert.Single(await Run(zammad, ZammadConnector.TicketOperation, new() { ["id"] = "7" }, endpoint: Desk + "/api/v1/")).Fields;
            Assert.Equal(new[] { $"{Api}/tickets/7", $"{Api}/ticket_articles/by_ticket/7" }, zammad.Requests.TakeLast(2).Select(request => request.Url)); // the API's path given once
            Assert.Equal(("Fika", "[{\"id\":9,\"body\":\"Fika?\",\"internal\":false}]"), ((string?)ticket["title"], (string?)ticket["articles"]));

            Assert.Equal("31007", (string?)Assert.Single(await Run(zammad, ZammadConnector.CreateTicketOperation, new()
            {
                ["title"] = "Fika at 3", ["group"] = "Users", ["customer"] = "ada@kafe.example.test", ["state"] = "open", ["priority"] = "2 normal", ["subject"] = "Kafé", ["body"] = "Who is in? \"Bröd\"",
                ["fields"] = "{\"order_no\": \"A-17\", \"cups\": 3, \"vip_guest\": true, \"note_extra\": null}",
            })).Fields["number"]);
            Assert.Equal(("POST", $"{Api}/tickets", (string?)"{\"title\":\"Fika at 3\",\"group\":\"Users\",\"customer\":\"ada@kafe.example.test\",\"state\":\"open\",\"priority\":\"2 normal\",\"order_no\":\"A-17\",\"cups\":3,\"vip_guest\":true,\"note_extra\":null,\"article\":{\"body\":\"Who is in? \\\"Bröd\\\"\",\"type\":\"note\",\"internal\":true,\"sender\":\"Agent\",\"subject\":\"Kafé\"}}"), Last());
            await Run(zammad, ZammadConnector.CreateTicketOperation, new() { ["title"] = "Fika", ["group"] = "Users", ["customer"] = "12", ["state"] = "4", ["priority"] = "3", ["body"] = "Hej", ["type"] = "Email", ["internal"] = "False", ["sender"] = "customer", ["to"] = "bo@kafe.example.test, cy@kafe.example.test", ["cc"] = "ada@kafe.example.test", ["reply_to"] = "fika@kafe.example.test" });
            Assert.Equal("{\"title\":\"Fika\",\"group\":\"Users\",\"customer_id\":12,\"state_id\":4,\"priority_id\":3,\"article\":{\"body\":\"Hej\",\"type\":\"email\",\"internal\":false,\"sender\":\"Customer\",\"to\":\"bo@kafe.example.test, cy@kafe.example.test\",\"cc\":\"ada@kafe.example.test\",\"reply_to\":\"fika@kafe.example.test\"}}", Last().Item3);

            await Run(zammad, ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7", ["title"] = "Fika at 4", ["group"] = "Kafé", ["state"] = "pending reminder", ["priority"] = "3 high", ["owner"] = "3", ["customer"] = "bo@kafe.example.test", ["pending_time"] = "2026-10-20T10:00:00+02:00", ["note"] = "<b>Moved</b>", ["fields"] = "{\"cups\": 4}" });
            Assert.Equal(("PUT", $"{Api}/tickets/7", (string?)"{\"title\":\"Fika at 4\",\"group\":\"Kafé\",\"state\":\"pending reminder\",\"priority\":\"3 high\",\"owner_id\":3,\"customer\":\"bo@kafe.example.test\",\"pending_time\":\"2026-10-20T08:00:00Z\",\"cups\":4,\"article\":{\"body\":\"<b>Moved</b>\",\"type\":\"note\",\"internal\":true,\"content_type\":\"text/html\"}}"), Last());
            await Run(zammad, ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7", ["note"] = "Ping" });
            Assert.Equal("{\"article\":{\"body\":\"Ping\",\"type\":\"note\",\"internal\":true,\"content_type\":\"text/html\"}}", Last().Item3);
            var deleted = Assert.Single(await Run(zammad, ZammadConnector.DeleteTicketOperation, new() { ["id"] = "7" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/tickets/7", (string?)null), Last());
            Assert.Equal((7L, true), ((long?)deleted["id"], (bool?)deleted["deleted"]));
        }

        [Fact]
        public async Task Users_organizations_and_groups_are_kept()
        {
            var zammad = new Zammad
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("?", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"id\":3,\"login\":\"ada\",\"firstname\":\"Ada\",\"active\":true}]"),
                    ("GET", var u) when u.EndsWith("/users/me", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"id\":3,\"login\":\"ada\",\"firstname\":\"Ada\",\"role_ids\":[1,2]}"),
                    ("GET", _) => (HttpStatusCode.OK, "{\"id\":5,\"name\":\"Kafé\"}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{}"),
                    _ => (HttpStatusCode.OK, "{\"id\":5,\"name\":\"Kafé\",\"active\":true}"),
                },
            };
            (string, string, string?) Last() => (zammad.Requests[^1].Method.Method, zammad.Requests[^1].Url, zammad.Requests[^1].Body);

            var found = Assert.Single(await Run(zammad, ZammadConnector.UsersOperation, new() { ["query"] = "email:ada@kafe.example.test & bröd", ["sort_by"] = "created_at", ["order_by"] = "DESC" })).Fields;
            Assert.Equal(("ada", true), ((string?)found["login"], (bool?)found["active"]));
            Assert.Equal($"{Api}/users/search?query=email%3Aada%40kafe.example.test%20%26%20br%C3%B6d&sort_by=created_at&order_by=desc&page=1&per_page=100", Last().Item2);
            await Run(zammad, ZammadConnector.UsersOperation, new() { ["max"] = "2" });
            Assert.Equal($"{Api}/users?page=1&per_page=2", Last().Item2); // no query: the users
            Assert.Equal("[1,2]", (string?)Assert.Single(await Run(zammad, ZammadConnector.MeOperation, new())).Fields["role_ids"]);
            Assert.Equal(("GET", $"{Api}/users/me", (string?)null), Last());
            await Run(zammad, ZammadConnector.UserOperation, new() { ["id"] = "3" });
            Assert.Equal($"{Api}/users/3", Last().Item2);
            await Run(zammad, ZammadConnector.CreateUserOperation, new()
            {
                ["firstname"] = "Ada", ["lastname"] = "Kafé", ["email"] = "ada@kafe.example.test", ["organization"] = "Kafé", ["phone"] = "+46 8 123", ["mobile"] = "+46 70 1", ["fax"] = "-", ["department"] = "Fika",
                ["web"] = "https://kafe.example.test", ["note"] = "VIP", ["street"] = "Storgatan 1", ["zip"] = "111 22", ["city"] = "Stockholm", ["country"] = "Sverige", ["active"] = "True", ["verified"] = "false", ["vip"] = "true", ["fields"] = "{\"loyalty_no\": \"L-1\"}",
            });
            Assert.Equal(("POST", $"{Api}/users", (string?)"{\"firstname\":\"Ada\",\"lastname\":\"Kafé\",\"email\":\"ada@kafe.example.test\",\"organization\":\"Kafé\",\"phone\":\"+46 8 123\",\"mobile\":\"+46 70 1\",\"fax\":\"-\",\"department\":\"Fika\",\"web\":\"https://kafe.example.test\",\"note\":\"VIP\",\"street\":\"Storgatan 1\",\"zip\":\"111 22\",\"city\":\"Stockholm\",\"country\":\"Sverige\",\"active\":true,\"verified\":false,\"vip\":true,\"loyalty_no\":\"L-1\"}"), Last());
            await Run(zammad, ZammadConnector.UpdateUserOperation, new() { ["id"] = "3", ["lastname"] = "Lovelace", ["vip"] = "false" });
            Assert.Equal(("PUT", $"{Api}/users/3", (string?)"{\"lastname\":\"Lovelace\",\"vip\":false}"), Last());
            await Run(zammad, ZammadConnector.DeleteUserOperation, new() { ["id"] = "3" });
            Assert.Equal(("DELETE", $"{Api}/users/3", (string?)null), Last());

            await Run(zammad, ZammadConnector.OrganizationsOperation, new() { ["max"] = "1" });
            Assert.Equal($"{Api}/organizations?page=1&per_page=1", Last().Item2);
            Assert.Equal("Kafé", (string?)Assert.Single(await Run(zammad, ZammadConnector.OrganizationOperation, new() { ["id"] = "5" })).Fields["name"]);
            Assert.Equal($"{Api}/organizations/5", Last().Item2);
            await Run(zammad, ZammadConnector.CreateOrganizationOperation, new() { ["name"] = "Kafé", ["domain"] = "kafe.example.test", ["note"] = "Fika", ["shared"] = "false", ["domain_assignment"] = "true", ["active"] = "true", ["vip"] = "false" });
            Assert.Equal(("POST", $"{Api}/organizations", (string?)"{\"name\":\"Kafé\",\"domain\":\"kafe.example.test\",\"note\":\"Fika\",\"shared\":false,\"domain_assignment\":true,\"active\":true,\"vip\":false}"), Last());
            await Run(zammad, ZammadConnector.UpdateOrganizationOperation, new() { ["id"] = "5", ["vip"] = "true" });
            Assert.Equal(("PUT", $"{Api}/organizations/5", (string?)"{\"vip\":true}"), Last());
            await Run(zammad, ZammadConnector.DeleteOrganizationOperation, new() { ["id"] = "5" });
            Assert.Equal(("DELETE", $"{Api}/organizations/5", (string?)null), Last());

            await Run(zammad, ZammadConnector.GroupsOperation, new() { ["max"] = "1" });
            Assert.Equal($"{Api}/groups?page=1&per_page=1", Last().Item2);
            await Run(zammad, ZammadConnector.GroupOperation, new() { ["id"] = "2" });
            Assert.Equal($"{Api}/groups/2", Last().Item2);
            await Run(zammad, ZammadConnector.CreateGroupOperation, new() { ["name"] = "Fika & bröd", ["note"] = "Kafé", ["active"] = "true" });
            Assert.Equal(("POST", $"{Api}/groups", (string?)"{\"name\":\"Fika & bröd\",\"note\":\"Kafé\",\"active\":true}"), Last());
            await Run(zammad, ZammadConnector.UpdateGroupOperation, new() { ["id"] = "2", ["active"] = "false" });
            Assert.Equal(("PUT", $"{Api}/groups/2", (string?)"{\"active\":false}"), Last());
            Assert.True((bool?)Assert.Single(await Run(zammad, ZammadConnector.DeleteGroupOperation, new() { ["id"] = "2" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/groups/2", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_zammads_words_on_a_failure()
        {
            var zammad = new Zammad();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(zammad, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> Ticket(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["title"] = "Fika", ["group"] = "Users", ["customer"] = "ada@kafe.example.test", ["body"] = "Hej" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("Zammad has no operation 'ticket_tags' - tickets, ticket, create_ticket,", await Refused("ticket_tags", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(ZammadConnector.GroupsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the ticket's id.", await Refused(ZammadConnector.TicketOperation, new()));
            Assert.Equal("Give the user's id.", await Refused(ZammadConnector.DeleteUserOperation, new()));
            Assert.Equal("Give the organization's id.", await Refused(ZammadConnector.UpdateOrganizationOperation, new() { ["vip"] = "true" }));
            Assert.Equal("Give the group's id.", await Refused(ZammadConnector.GroupOperation, new()));
            Assert.Equal("'7/../users' is not an id (id).", await Refused(ZammadConnector.DeleteTicketOperation, new() { ["id"] = "7/../users" }));
            Assert.Equal("Give the ticket's title.", await Refused(ZammadConnector.CreateTicketOperation, new() { ["group"] = "Users" }));
            Assert.Equal("Give the ticket's group.", await Refused(ZammadConnector.CreateTicketOperation, new() { ["title"] = "Fika" }));
            Assert.Equal("Give the ticket's customer - an e-mail or an id.", await Refused(ZammadConnector.CreateTicketOperation, new() { ["title"] = "Fika", ["group"] = "Users" }));
            Assert.Equal("'ada' is not an e-mail or an id (customer).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("customer", "ada"))));
            Assert.Equal("'letter' is not note, email, phone, chat, fax or sms (type).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("type", "letter"))));
            Assert.Equal("Give the article's body.", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("body", ""))));
            Assert.Equal("'yes' is not true or false (internal).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("internal", "yes"))));
            Assert.Equal("'Bot' is not Agent, Customer or System (sender).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("sender", "Bot"))));
            Assert.Equal("Only an e-mail article has a to.", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("to", "bo@kafe.example.test"))));
            Assert.Equal("Only an e-mail article has a reply to.", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("reply_to", "bo@kafe.example.test"))));
            Assert.Equal("'bo@kafe, cy@kafe.example.test' is not an e-mail or a comma-separated list of them (to).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("type", "email"), ("to", "bo@kafe, cy@kafe.example.test"))));
            Assert.Equal("Give an e-mail article's address (to).", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("type", "email"), ("cc", "bo@kafe.example.test"))));
            Assert.Equal("The custom fields are not a JSON object - {\"order_no\": \"A-17\"}.", await Refused(ZammadConnector.CreateTicketOperation, Ticket(("fields", "order_no = A-17"))));
            Assert.Equal("The custom fields are not a JSON object - {\"order_no\": \"A-17\"}.", await Refused(ZammadConnector.UpdateUserOperation, new() { ["id"] = "3", ["fields"] = "[\"A-17\"]" }));
            Assert.Equal("'Order No' is not a field's name - lower-case letters, digits and '_' (fields).", await Refused(ZammadConnector.CreateGroupOperation, new() { ["name"] = "Fika", ["fields"] = "{\"Order No\": \"A-17\"}" }));
            Assert.Equal("'Order-No' is not a field's name - lower-case letters, digits and '_' (fields).", await Refused(ZammadConnector.CreateGroupOperation, new() { ["name"] = "Fika", ["fields"] = "{\"Order-No\": \"A-17\"}" }));
            Assert.Equal("'1st' is not a field's name - created_at (sort_by).", await Refused(ZammadConnector.UsersOperation, new() { ["sort_by"] = "1st" }));
            Assert.Equal("'tags' is not a text, a number, true or false (fields).", await Refused(ZammadConnector.CreateOrganizationOperation, new() { ["name"] = "Kafé", ["fields"] = "{\"tags\": [\"fika\"]}" }));
            Assert.Equal("'boss' is not an id (owner).", await Refused(ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7", ["owner"] = "boss" }));
            Assert.Equal("'2026-10-20' is not a time - 2026-10-20T08:00:00Z (pending_time).", await Refused(ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7", ["pending_time"] = "2026-10-20" }));
            Assert.Equal("'soonT' is not a time - 2026-10-20T08:00:00Z (pending_time).", await Refused(ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7", ["pending_time"] = "soonT" }));
            Assert.Equal("Give what to change - its title, group, state, priority, owner, customer, pending time, a note or fields.", await Refused(ZammadConnector.UpdateTicketOperation, new() { ["id"] = "7" }));
            Assert.Equal("Give what to change - a field of the user's.", await Refused(ZammadConnector.UpdateUserOperation, new() { ["id"] = "3" }));
            Assert.Equal("Give what to change - a field of the group's.", await Refused(ZammadConnector.UpdateGroupOperation, new() { ["id"] = "2" }));
            Assert.Equal("Give the user's first name.", await Refused(ZammadConnector.CreateUserOperation, new() { ["lastname"] = "Lovelace" }));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(ZammadConnector.CreateUserOperation, new() { ["firstname"] = "Ada", ["email"] = "ada@kafe" }));
            Assert.Equal("'maybe' is not true or false (verified).", await Refused(ZammadConnector.UpdateUserOperation, new() { ["id"] = "3", ["verified"] = "maybe" }));
            Assert.Equal("Give the organization's name.", await Refused(ZammadConnector.CreateOrganizationOperation, new() { ["domain"] = "kafe.example.test" }));
            Assert.Equal("Give the group's name.", await Refused(ZammadConnector.CreateGroupOperation, new() { ["note"] = "Fika" }));
            Assert.Equal("'created at' is not a field's name - created_at (sort_by).", await Refused(ZammadConnector.UsersOperation, new() { ["sort_by"] = "created at" }));
            Assert.Equal("'newest' is not asc or desc (order_by).", await Refused(ZammadConnector.UsersOperation, new() { ["order_by"] = "newest" }));
            Assert.Equal("Give the help desk's address - https://helpdesk.example.com.", await Refused(ZammadConnector.TicketsOperation, new(), endpoint: ""));
            Assert.Equal("The Zammad connection has no access token.", await Refused(ZammadConnector.TicketsOperation, new(), credentials: new()));
            Assert.Empty(zammad.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Zammad { Answer = (_, _) => (status, answer) }, ZammadConnector.CreateGroupOperation, new() { ["name"] = "Fika" }))).Message;
            Assert.Equal("HTTP 422: Object already exists!", await Failed(HttpStatusCode.UnprocessableEntity, "{\"error\":\"Object already exists!\"}"));
            Assert.Equal("HTTP 401: Authentication failed, the token is invalid.", await Failed(HttpStatusCode.Unauthorized, "{\"error\":\"authentication failed\",\"error_human\":\"Authentication failed, the token is invalid.\"}")); // its human words first
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Zammad answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Zammad zammad) =>
                new ZammadConnector().TestConnectionAsync(Contexts.Test(new StubEgress(zammad), Desk, null, Credentials));
            var me = new Zammad { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":3,\"firstname\":\"Ada\",\"lastname\":\"Lovelace\",\"email\":\"ada@kafe.example.test\",\"login\":\"ada\"}") };
            var tested = await Test(me);
            Assert.Equal((true, "Zammad: the token is Ada Lovelace's (ada@kafe.example.test)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/users/me", "GET", "Token token=zt_1"), (me.Requests[0].Url, me.Requests[0].Method.Method, me.Requests[0].Authorization));
            Assert.Equal("Zammad: the token is Ada's (ada).", (await Test(new Zammad { Answer = (_, _) => (HttpStatusCode.OK, "{\"firstname\":\"Ada\",\"lastname\":\"\",\"email\":\"\",\"login\":\"ada\"}") })).Message);
            Assert.Equal("Zammad: the token is a user's.", (await Test(new Zammad())).Message);
            var refused = await Test(new Zammad { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"Invalid token!\"}") });
            Assert.Equal((false, "HTTP 401: Invalid token!"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "zammad"), (new ZammadConnector().AuthType, new ZammadConnector().ConnectorType));
            Assert.Equal(21, new ZammadConnector().Operations.Count);
            Assert.Equal(("api-key", "Access token (Profile > Token Access)"), (Assert.Single(new ZammadConnector().CredentialLabels!).Key, Assert.Single(new ZammadConnector().CredentialLabels!).Value)); // in Zammad's words
        }
    }
}
