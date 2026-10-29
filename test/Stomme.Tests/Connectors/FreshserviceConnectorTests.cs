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
using Stomme.Modules.Connectors.Freshservice;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Freshservice (n8n's Freshservice node: an IT service desk's tickets, changes, problems, releases, assets and the
    /// people and groups around them): the API key as HTTP Basic against the desk's API v2; records of any of its resources read
    /// page by page, no further than asked, tickets, agents and requesters by Freshservice's query too; one got, created,
    /// updated and deleted, the record unwrapped from Freshservice's key for it; roles read only; what could go wrong refused
    /// before anything is sent; Freshservice's description and the fields it names on a failure; a test naming the latest
    /// ticket.
    /// </summary>
    public sealed class FreshserviceConnectorTests
    {
        private const string Desk = "https://kafe.freshservice.com";

        private const string Api = Desk + "/api/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "fs-key-1" };

        private sealed class Freshservice : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Freshservice desk, string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new FreshserviceConnector(), Contexts.Extract(new StubEgress(desk), endpoint, operation, parameters, null, credentials ?? Credentials));

        private static string Tickets(int first, int count) =>
            "{\"tickets\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"subject\":\"Kassan " + i + "\",\"priority\":2,\"is_escalated\":false,\"custom_fields\":{\"site\":\"Söder\"},\"cc_emails\":[]}")) + "]}";

        [Fact]
        public async Task Records_are_read_page_by_page_no_further_than_asked_by_a_query_too()
        {
            var desk = new Freshservice { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=3", StringComparison.Ordinal) ? Tickets(201, 30) : url.Contains("page=2", StringComparison.Ordinal) ? Tickets(101, 100) : Tickets(1, 100)) };

            var tickets = await Run(desk, "records", new() { ["resource"] = "tickets", ["max"] = "150" });
            Assert.Equal(150, tickets.Count); // no further than asked
            Assert.Equal(new[] { $"{Api}/tickets?page=1&per_page=100", $"{Api}/tickets?page=2&per_page=100" }, desk.Requests.Select(request => request.Url));
            Assert.All(desk.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fs-key-1:X")), request.Authorization));
            var first = tickets[0].Fields;
            Assert.Equal((1L, "Kassan 1", 2L, false, "{\"site\":\"Söder\"}", "[]"), ((long?)first["id"], (string?)first["subject"], (long?)first["priority"], (bool?)first["is_escalated"], (string?)first["custom_fields"], (string?)first["cc_emails"]));
            desk.Requests.Clear();
            Assert.Equal(230, (await Run(desk, null!, new() { ["resource"] = "tickets", ["query"] = "priority:3 AND status:2" })).Count); // no operation: the records - a short page the end
            Assert.Equal($"{Api}/tickets/filter?query=%22priority%3A3%20AND%20status%3A2%22&page=1&per_page=100", desk.Requests[0].Url);
            Assert.Equal(3, desk.Requests.Count);
            var agents = new Freshservice { Answer = (_, _) => (HttpStatusCode.OK, "{\"agents\":[{\"id\":7,\"email\":\"ada@kafe.example.com\"}]}") };
            Assert.Single(await Run(agents, "records", new() { ["resource"] = "agents", ["query"] = "email:'ada@kafe.example.com'", ["max"] = "5" }));
            Assert.Equal($"{Api}/agents?query=%22email%3A%27ada%40kafe.example.com%27%22&page=1&per_page=5", agents.Requests[0].Url); // a page no larger than asked
            var endless = new Freshservice { Answer = (_, _) => (HttpStatusCode.OK, "{\"changes\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => "{\"id\":" + i + "}")) + "]}") };
            Assert.Equal(1000, (await Run(endless, "records", new() { ["resource"] = "changes" })).Count); // 1,000 at most when no max is given
            Assert.Equal($"{Api}/changes?page=10&per_page=100", endless.Requests[^1].Url);
        }

        [Fact]
        public async Task A_record_is_got_created_updated_and_deleted_unwrapped_from_its_key()
        {
            var desk = new Freshservice
            {
                Answer = (method, url) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "")
                    : url.Contains("/asset_types", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"asset_type\":{\"id\":5,\"name\":\"Kassaapparat\"}}")
                    : (method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, "{\"ticket\":{\"id\":1042,\"subject\":\"Kassan är nere\",\"status\":2}}"),
            };
            (string, string, string?) Last() => (desk.Requests[^1].Method.Method, desk.Requests[^1].Url, desk.Requests[^1].Body);

            var ticket = Assert.Single(await Run(desk, "record", new() { ["resource"] = "tickets", ["id"] = "1042" })).Fields;
            Assert.Equal(("GET", $"{Api}/tickets/1042", (string?)null), Last());
            Assert.Equal(("Kassan är nere", 2L), ((string?)ticket["subject"], (long?)ticket["status"]));
            Assert.Equal("Kassaapparat", (string?)Assert.Single(await Run(desk, "record", new() { ["resource"] = "asset_types", ["id"] = "5" })).Fields["name"]); // its own key

            await Run(desk, "create_record", new() { ["resource"] = "tickets", ["values"] = "{\"email\": \"ada@kafe.example.com\", \"subject\": \"Kassan är nere\", \"description\": \"<p>Hela kvällen</p>\", \"priority\": 2, \"status\": 2, \"cc_emails\": [\"bo@kafe.example.com\"]}" });
            Assert.Equal(("POST", $"{Api}/tickets", (string?)"{\"email\":\"ada@kafe.example.com\",\"subject\":\"Kassan är nere\",\"description\":\"<p>Hela kvällen</p>\",\"priority\":2,\"status\":2,\"cc_emails\":[\"bo@kafe.example.com\"]}"), Last());
            await Run(desk, "update_record", new() { ["resource"] = "tickets", ["id"] = "1042", ["values"] = "{\"status\": 4}" });
            Assert.Equal(("PUT", $"{Api}/tickets/1042", (string?)"{\"status\":4}"), Last());
            var deleted = Assert.Single(await Run(desk, "Delete_Record", new() { ["resource"] = "tickets", ["id"] = "1042" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/tickets/1042", (string?)null), Last());
            Assert.Equal(((object?)1042L, (object?)true), (deleted["id"], deleted["deleted"]));
            await Run(desk, "record", new() { ["resource"] = "tickets", ["id"] = "1042" }, endpoint: Desk + "/api/v2/tickets");
            Assert.Equal($"{Api}/tickets/1042", Last().Item2); // a pasted API address forgiven down to the desk
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_freshservices_words_on_a_failure()
        {
            var desk = new Freshservice();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(desk, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> Ticket(params (string Name, string Value)[] more) => new[] { ("resource", "tickets"), ("id", "1042") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);

            Assert.Equal("Freshservice has no operation 'tickets' - records, record, create_record, update_record or delete_record.", await Refused("tickets", new()));
            Assert.Equal("Give the desk's address - https://kafe.freshservice.com.", await Refused("records", new() { ["resource"] = "tickets" }, endpoint: ""));
            Assert.Equal("Give the desk's address - https://kafe.freshservice.com.", await Refused("records", new() { ["resource"] = "tickets" }, endpoint: "https://kafe.freshservice.com/a/tickets"));
            Assert.Equal("Give the API key (api-key) - Profile settings > Your API key.", await Refused("records", new() { ["resource"] = "tickets" }, credentials: new()));
            Assert.Equal("Give the API key (api-key) - Profile settings > Your API key.", await Refused("records", new() { ["resource"] = "tickets" }, credentials: new() { ["api-key"] = "ada:secret" }));
            Assert.Equal("Give the resource - tickets, agents, changes (resource).", await Refused("records", new()));
            Assert.Equal("'Tickets' is not one of Freshservice's resources - tickets, changes, problems, releases, assets, agents, requesters, groups, departments, locations, products, asset_types, applications, announcements, requester_groups or roles (resource).", await Refused("records", new() { ["resource"] = "Tickets" }));
            Assert.Equal("Freshservice's roles are read only - get them or one of them.", await Refused("delete_record", new() { ["resource"] = "roles", ["id"] = "1" }));
            Assert.Equal("Freshservice's roles are read only - get them or one of them.", await Refused("create_record", new() { ["resource"] = "roles", ["values"] = "{\"name\": \"x\"}" }));
            Assert.Equal("Give the record's id (id).", await Refused("record", new() { ["resource"] = "tickets" }));
            Assert.Equal("'INC-1042' is not a record's id - a number (id).", await Refused("delete_record", Ticket(("id", "INC-1042"))));
            Assert.Equal("'1042/conversations' is not a record's id - a number (id).", await Refused("record", Ticket(("id", "1042/conversations"))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("records", new() { ["resource"] = "tickets", ["max"] = "0" }));
            Assert.Equal("Freshservice takes a query for tickets, agents and requesters alone - not changes (query).", await Refused("records", new() { ["resource"] = "changes", ["query"] = "priority:3" }));
            Assert.Equal("'priority:3\" OR \"1' is not a query of 512 characters at most, without quotes or control characters (query).", await Refused("records", new() { ["resource"] = "tickets", ["query"] = "priority:3\" OR \"1" }));
            Assert.Equal("Give the values - a JSON object of fields (values).", await Refused("create_record", new() { ["resource"] = "tickets" }));
            const string NotAnObject = "The values are not a JSON object of fields - {\"subject\": \"Kassan är nere\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["resource"] = "tickets", ["values"] = "subject = Kassan" }));
            Assert.Equal(NotAnObject, await Refused("update_record", Ticket(("values", "{}"))));
            Assert.Equal("'Subject' is not a field's name - lower-case letters, digits and _ (values).", await Refused("update_record", Ticket(("values", "{\"Subject\": \"x\"}"))));
            Assert.Empty(desk.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "create_record") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Freshservice { Answer = (_, _) => (status, answer) }, operation, Ticket(("values", "{\"subject\": \"x\"}"))))).Message;
            Assert.Equal("HTTP 400 Validation failed - email: It should be a valid email address; priority: It should be one of these values: '1,2,3,4'", await Failed(HttpStatusCode.BadRequest, "{\"description\":\"Validation failed\",\"errors\":[{\"field\":\"email\",\"message\":\"It should be a valid email address\",\"code\":\"invalid_value\"},{\"field\":\"priority\",\"message\":\"It should be one of these values: '1,2,3,4'\",\"code\":\"invalid_value\"}]}"));
            Assert.Equal("HTTP 401 You have to be logged in to perform this action.", await Failed(HttpStatusCode.Unauthorized, "{\"code\":\"invalid_credentials\",\"message\":\"You have to be logged in to perform this action.\"}"));
            Assert.Equal("HTTP 404.", await Failed(HttpStatusCode.NotFound, "", "record"));
            Assert.Equal("HTTP 400 Validation failed - " + new string('e', 300 - "Validation failed - ".Length), await Failed(HttpStatusCode.BadRequest, "{\"description\":\"Validation failed\",\"errors\":[{\"message\":\"" + new string('e', 400) + "\"}]}")); // a long one cut
            Assert.Equal("Freshservice answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Log in</body></html>", "record"));
            Assert.Equal("Freshservice answered with no ticket.", await Failed(HttpStatusCode.OK, "{\"tickets\":[]}", "update_record"));
        }

        [Fact]
        public async Task A_test_names_the_latest_ticket()
        {
            Task<ConnectionTestResult> Test(Freshservice desk) => new FreshserviceConnector().TestConnectionAsync(Contexts.Test(new StubEgress(desk), Desk, null, Credentials));
            var desk = new Freshservice { Answer = (_, _) => (HttpStatusCode.OK, Tickets(1042, 1)) };
            var tested = await Test(desk);
            Assert.Equal((true, "Freshservice: signed in - the latest ticket is #1042."), (tested.Success, tested.Message));
            Assert.Equal($"{Api}/tickets?per_page=1", Assert.Single(desk.Requests).Url);
            Assert.Equal("Freshservice: signed in - no ticket yet.", (await Test(new Freshservice { Answer = (_, _) => (HttpStatusCode.OK, "{\"tickets\":[]}") })).Message);
            var refused = await Test(new Freshservice { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"code\":\"invalid_credentials\",\"message\":\"You have to be logged in to perform this action.\"}") });
            Assert.Equal((false, "HTTP 401 You have to be logged in to perform this action."), (refused.Success, refused.Message));
            var connector = new FreshserviceConnector();
            Assert.Equal((AuthenticationType.ApiKey, "freshservice", 5, "API key (Profile settings > Your API key)"), (connector.AuthType, connector.ConnectorType, connector.Operations.Count, connector.CredentialLabels!["api-key"]));
        }
    }
}
