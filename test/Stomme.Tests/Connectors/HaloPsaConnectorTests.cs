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
using Stomme.Modules.Connectors.HaloPsa;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// HaloPSA (n8n's HaloPSA node: a service desk's clients, their sites and users, and its tickets): the API
    /// application's client credentials at Halo's token endpoint, Halo's resource server with the token as a bearer; records
    /// read page by page, no further than asked, a short page or Halo's count the end, by Halo's search, active or inactive
    /// ones alone and Halo's own query parameters; one got, created (never with an id), updated (got first, so never created)
    /// and deleted; a password never a value; what could go wrong refused before a token is asked for; Halo's message on a
    /// failure; a test counting the tickets.
    /// </summary>
    public sealed class HaloPsaConnectorTests
    {
        private const string Desk = "https://kafe.halopsa.com";

        private const string Api = Desk + "/api";

        private const string TokenUrl = Desk + "/auth/token";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "halo-app", ["client-secret"] = "halo-secret", ["token-url"] = TokenUrl };

        private sealed class Halo : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url == TokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"halo-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}") : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Halo halo, string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new HaloPsaConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(halo), endpoint, operation, parameters, settings, credentials ?? Credentials));

        private static string Tickets(int first, int count, int? total) =>
            "{" + (total is null ? "" : "\"record_count\":" + total + ",") + "\"tickets\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"summary\":\"Kassan " + i + "\",\"client_id\":12,\"inactive\":false,\"customfields\":[{\"name\":\"CFsite\",\"value\":\"Söder\"}]}")) + "]}";

        [Fact]
        public async Task Records_are_read_page_by_page_no_further_than_asked_by_search_status_and_halos_own_parameters()
        {
            var halo = new Halo { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page_no=3", StringComparison.Ordinal) ? Tickets(201, 30, 230) : url.Contains("page_no=2", StringComparison.Ordinal) ? Tickets(101, 100, 230) : Tickets(1, 100, 230)) };

            var tickets = await Run(halo, "records", new() { ["resource"] = "tickets", ["max"] = "150" });
            Assert.Equal(150, tickets.Count); // no further than asked
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=halo-app&client_secret=halo-secret&scope=admin+edit%3Atickets+edit%3Acustomers"), (halo.Requests[0].Method.Method, halo.Requests[0].Url, halo.Requests[0].Body));
            Assert.Equal(new[] { $"{Api}/Tickets?pageinate=true&page_size=100&page_no=1", $"{Api}/Tickets?pageinate=true&page_size=100&page_no=2" }, halo.Requests.Skip(1).Select(request => request.Url));
            Assert.All(halo.Requests.Skip(1), request => Assert.Equal("Bearer halo-token", request.Authorization));
            var first = tickets[0].Fields;
            Assert.Equal((1L, "Kassan 1", 12L, false, "[{\"name\":\"CFsite\",\"value\":\"Söder\"}]"), ((long?)first["id"], (string?)first["summary"], (long?)first["client_id"], (bool?)first["inactive"], (string?)first["customfields"]));

            halo.Requests.Clear();
            Assert.Equal(230, (await Run(halo, null!, new() { ["resource"] = "tickets", ["search"] = "Kassan ä & co", ["status"] = "Active", ["filters"] = "{\"client_id\": 12, \"open_only\": true, \"agent\": \"Ada & Bo\"}" })).Count); // no operation: the records
            Assert.Equal($"{Api}/Tickets?pageinate=true&page_size=100&page_no=1&search=Kassan%20%C3%A4%20%26%20co&includeactive=true&includeinactive=false&client_id=12&open_only=true&agent=Ada%20%26%20Bo", halo.Requests[1].Url);
            Assert.Equal(4, halo.Requests.Count); // the token, three pages
            var sites = new Halo { Answer = (_, _) => (HttpStatusCode.OK, "{\"record_count\":1,\"sites\":[{\"id\":3,\"name\":\"Söder\",\"client_id\":12}]}") };
            Assert.Equal("Söder", (string?)Assert.Single(await Run(sites, "records", new() { ["resource"] = "sites", ["status"] = "inactive", ["max"] = "5" })).Fields["name"]);
            Assert.Equal($"{Api}/Site?pageinate=true&page_size=5&page_no=1&includeactive=false&includeinactive=true", sites.Requests[^1].Url); // a page no larger than asked
            await Run(sites, "records", new() { ["resource"] = "sites", ["status"] = "all" });
            Assert.Equal($"{Api}/Site?pageinate=true&page_size=100&page_no=1", sites.Requests[^1].Url);

            var counted = new Halo { Answer = (_, url) => (HttpStatusCode.OK, Tickets(1, 100, 200)) };
            Assert.Equal(200, (await Run(counted, "records", new() { ["resource"] = "tickets" })).Count); // Halo's count reached: no third page
            Assert.Equal($"{Api}/Tickets?pageinate=true&page_size=100&page_no=2", counted.Requests[^1].Url);
            var uncounted = new Halo { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page_no=2", StringComparison.Ordinal) ? Tickets(101, 30, null) : Tickets(1, 100, null)) };
            Assert.Equal(130, (await Run(uncounted, "records", new() { ["resource"] = "tickets" })).Count); // no count: a short page the end
            Assert.Equal($"{Api}/Tickets?pageinate=true&page_size=100&page_no=2", uncounted.Requests[^1].Url);
            var endless = new Halo { Answer = (_, _) => (HttpStatusCode.OK, Tickets(1, 100, null)) };
            Assert.Equal(1000, (await Run(endless, "records", new() { ["resource"] = "tickets" })).Count); // 1,000 at most when no max is given
            Assert.Equal($"{Api}/Tickets?pageinate=true&page_size=100&page_no=10", endless.Requests[^1].Url);
            var users = new Halo { Answer = (_, _) => (HttpStatusCode.OK, "{\"record_count\":0,\"users\":[]}") };
            Assert.Empty(await Run(users, "records", new() { ["resource"] = "users" }, settings: new() { ["scope"] = "read:customers" }));
            Assert.Equal(("grant_type=client_credentials&client_id=halo-app&client_secret=halo-secret&scope=read%3Acustomers", $"{Api}/Users?pageinate=true&page_size=100&page_no=1"), (users.Requests[0].Body, users.Requests[1].Url)); // the connection's scope
        }

        [Fact]
        public async Task A_record_is_got_created_updated_never_creating_one_and_deleted()
        {
            var halo = new Halo
            {
                Answer = (method, url) => method == HttpMethod.Delete ? (HttpStatusCode.OK, "")
                    : url.Contains("/Client", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[{\"id\":5,\"name\":\"Kafé\",\"is_vip\":true}]")
                    : url.Contains("/Users", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":7,\"name\":\"Ada\",\"site_id\":3,\"emailaddress\":\"ada@kafe.example.com\"}")
                    : (method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, "{\"id\":1042,\"summary\":\"Kassan är nere\",\"status_id\":1}"),
            };
            (string, string, string?) Last() => (halo.Requests[^1].Method.Method, halo.Requests[^1].Url, halo.Requests[^1].Body);

            var ticket = Assert.Single(await Run(halo, "record", new() { ["resource"] = "tickets", ["id"] = "1042" })).Fields;
            Assert.Equal(("GET", $"{Api}/Tickets/1042", (string?)null), Last());
            Assert.Equal(("Kassan är nere", 1L), ((string?)ticket["summary"], (long?)ticket["status_id"]));

            await Run(halo, "create_record", new() { ["resource"] = "tickets", ["values"] = "{\"summary\": \"Kassan är nere\", \"details\": \"<p>Sedan nio</p>\", \"tickettype_id\": 1, \"customfields\": [{\"name\": \"CFsite\", \"value\": \"Söder\"}]}" });
            Assert.Equal(("POST", $"{Api}/Tickets", (string?)"[{\"summary\":\"Kassan är nere\",\"details\":\"<p>Sedan nio</p>\",\"tickettype_id\":1,\"customfields\":[{\"name\":\"CFsite\",\"value\":\"Söder\"}]}]"), Last());
            var client = Assert.Single(await Run(halo, "create_record", new() { ["resource"] = "clients", ["values"] = "{\"name\": \"Kafé\", \"is_vip\": true}" })).Fields;
            Assert.Equal(("Kafé", true), ((string?)client["name"], (bool?)client["is_vip"])); // answered as a list of one

            halo.Requests.Clear();
            var user = Assert.Single(await Run(halo, "update_record", new() { ["resource"] = "users", ["id"] = "7", ["values"] = "{\"emailaddress\": \"ada@kafe.example.com\", \"inactive\": false}" })).Fields;
            Assert.Equal(new[] { ("GET", $"{Api}/Users/7", (string?)null), ("POST", $"{Api}/Users", (string?)"[{\"id\":7,\"emailaddress\":\"ada@kafe.example.com\",\"inactive\":false}]") }, halo.Requests.Skip(1).Select(request => (request.Method.Method, request.Url, request.Body)));
            Assert.Equal("ada@kafe.example.com", (string?)user["emailaddress"]);

            var missing = new Halo { Answer = (_, _) => (HttpStatusCode.NotFound, "\"Kunde inte hitta användaren\"") };
            Assert.Equal("HTTP 404 Kunde inte hitta användaren", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "update_record", new() { ["resource"] = "users", ["id"] = "8", ["values"] = "{\"name\": \"Bo\"}" }))).Message);
            Assert.DoesNotContain(missing.Requests, request => request.Method == HttpMethod.Post && request.Url != TokenUrl); // nothing written
            var silent = new Halo { Answer = (_, _) => (HttpStatusCode.OK, "") };
            Assert.Equal("HaloPSA answered with no record 8 - nothing was written.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(silent, "update_record", new() { ["resource"] = "users", ["id"] = "8", ["values"] = "{\"name\": \"Bo\"}" }))).Message);
            Assert.DoesNotContain(silent.Requests, request => request.Method == HttpMethod.Post && request.Url != TokenUrl);

            var deleted = Assert.Single(await Run(halo, "Delete_Record", new() { ["resource"] = "clients", ["id"] = "5" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/Client/5", (string?)null), Last());
            Assert.Equal(((object?)5L, (object?)true), (deleted["id"], deleted["deleted"]));
            await Run(halo, "record", new() { ["resource"] = "sites", ["id"] = "3" }, endpoint: Desk + "/api/");
            Assert.Equal($"{Api}/Site/3", Last().Item2); // the resource server's address forgiven down to the desk
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_halos_words_on_a_failure()
        {
            var halo = new Halo();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Desk, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(halo, operation, parameters, endpoint, credentials: credentials))).Message;
            Dictionary<string, string> Ticket(params (string Name, string Value)[] more) => new[] { ("resource", "tickets"), ("id", "1042") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);

            Assert.Equal("HaloPSA has no operation 'tickets' - records, record, create_record, update_record or delete_record.", await Refused("tickets", new()));
            foreach (var endpoint in new[] { "", "kafe.halopsa.com", "https://kafe.halopsa.com/portal", "https://kafe.halopsa.com/api/Tickets", "https://kafe.halopsa.com/api?tenant=kafe", "https://ada:x@kafe.halopsa.com" })
            {
                Assert.Equal("Give HaloPSA's address - https://kafe.halopsa.com (its resource server, /api, forgiven).", await Refused("records", new() { ["resource"] = "tickets" }, endpoint));
            }

            Assert.Equal("Give the resource - clients, sites, tickets or users (resource).", await Refused("records", new()));
            foreach (var resource in new[] { "Tickets", "agents", "Client" })
            {
                Assert.Equal($"'{resource}' is not one of HaloPSA's resources - clients, sites, tickets or users (resource).", await Refused("records", new() { ["resource"] = resource }));
            }

            Assert.Equal("Give the record's id (id).", await Refused("record", new() { ["resource"] = "tickets" }));
            Assert.Equal("'TK-1042' is not a record's id - a number (id).", await Refused("delete_record", Ticket(("id", "TK-1042"))));
            Assert.Equal("'1042/actions' is not a record's id - a number (id).", await Refused("record", Ticket(("id", "1042/actions"))));
            Assert.Equal("'7/actions' is not a record's id - a number (id).", await Refused("update_record", Ticket(("id", "7/actions"), ("values", "{\"summary\": \"x\"}"))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("records", new() { ["resource"] = "tickets", ["max"] = "0" }));
            Assert.Equal("'Kassan\u0001' is not a search of 512 characters at most, without control characters (search).", await Refused("records", new() { ["resource"] = "tickets", ["search"] = "Kassan\u0001" }));
            Assert.Equal("'open' is not active, inactive or all (status).", await Refused("records", new() { ["resource"] = "tickets", ["status"] = "open" }));
            Assert.Equal("The filters are not a JSON object of Halo's query parameters - {\"client_id\": 12} (filters).", await Refused("records", new() { ["resource"] = "tickets", ["filters"] = "client_id=12" }));
            foreach (var set in new[] { "page_no", "count", "pageinate", "search", "includeinactive" })
            {
                Assert.Equal($"'{set}' is set by the step - give search, status or max instead (filters).", await Refused("records", new() { ["resource"] = "tickets", ["filters"] = "{\"" + set + "\": 1}" }));
            }

            Assert.Equal("'Client_ID' is not one of Halo's query parameters - lower-case letters, digits and _ (filters).", await Refused("records", new() { ["resource"] = "tickets", ["filters"] = "{\"Client_ID\": 12}" }));
            Assert.Equal("'client_id&x' is not one of Halo's query parameters - lower-case letters, digits and _ (filters).", await Refused("records", new() { ["resource"] = "tickets", ["filters"] = "{\"client_id&x\": 12}" }));
            foreach (var value in new[] { "[12]", "{\"id\": 12}", "null", "\"12\\u0001\"", "\"\"" })
            {
                Assert.Equal("The filter 'client_id' is not a text, a number or true or false (filters).", await Refused("records", new() { ["resource"] = "tickets", ["filters"] = "{\"client_id\": " + value + "}" }));
            }

            Assert.Equal("Give the values - a JSON object of fields (values).", await Refused("create_record", new() { ["resource"] = "tickets" }));
            const string NotAnObject = "The values are not a JSON object of fields - {\"summary\": \"Kassan är nere\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["resource"] = "tickets", ["values"] = "summary = Kassan" }));
            Assert.Equal(NotAnObject, await Refused("update_record", Ticket(("values", "{}"))));
            Assert.Equal("'Summary' is not a field's name - lower-case letters, digits and _ (values).", await Refused("update_record", Ticket(("values", "{\"Summary\": \"x\"}"))));
            const string NoId = "A record's id is never among its values - Halo would update that record; give it as the id of an update (values).";
            Assert.Equal(NoId, await Refused("create_record", new() { ["resource"] = "tickets", ["values"] = "{\"id\": 7, \"summary\": \"x\"}" }));
            Assert.Equal(NoId, await Refused("update_record", Ticket(("values", "{\"summary\": \"x\", \"id\": 7}"))));
            foreach (var password in new[] { "password", "new_password" })
            {
                Assert.Equal("A password is never a step's value - set it in HaloPSA (values).", await Refused("create_record", new() { ["resource"] = "users", ["values"] = "{\"name\": \"Ada\", \"" + password + "\": \"x\"}" }));
            }

            Assert.Equal("Give Halo's token endpoint - https://kafe.halopsa.com/auth/token.", await Refused("records", new() { ["resource"] = "tickets" }, credentials: new() { ["client-id"] = "halo-app", ["client-secret"] = "halo-secret" }));
            Assert.Empty(halo.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "record") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Halo { Answer = (_, _) => (status, answer) }, operation, Ticket(("values", "{\"summary\": \"x\"}"))))).Message;
            Assert.Equal("HTTP 400 Please enter a Summary", await Failed(HttpStatusCode.BadRequest, "\"Please enter a Summary\"", "create_record"));
            Assert.Equal("HTTP 401 Authorization has been denied for this request.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"Authorization has been denied for this request.\"}"));
            Assert.Equal("HTTP 403 You do not have permission to delete this Ticket", await Failed(HttpStatusCode.Forbidden, "{\"ClassName\":\"HaloException\",\"Message\":\"You do not have permission to delete this Ticket\"}", "delete_record"));
            Assert.Equal("HTTP 400 invalid_scope", await Failed(HttpStatusCode.BadRequest, "{\"error\":\"invalid_scope\"}"));
            Assert.Equal("HTTP 400 " + new string('e', 300), await Failed(HttpStatusCode.BadRequest, "\"" + new string('e', 400) + "\"", "create_record")); // a long one cut
            Assert.Equal("HTTP 404.", await Failed(HttpStatusCode.NotFound, "[]"));
            Assert.Equal("HaloPSA answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Log in</html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "delete_record"));
            Assert.Equal("HaloPSA answered with no ticket.", await Failed(HttpStatusCode.OK, ""));
            Assert.Equal("HaloPSA answered with no ticket.", await Failed(HttpStatusCode.OK, "[{\"id\":1},{\"id\":2}]", "create_record"));
        }

        [Fact]
        public async Task A_test_counts_the_tickets()
        {
            Task<ConnectionTestResult> Test(Halo halo) =>
                new HaloPsaConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(halo), Desk, null, Credentials));
            var desk = new Halo { Answer = (_, _) => (HttpStatusCode.OK, "{\"record_count\":42,\"tickets\":[{\"id\":42}]}") };
            Assert.Equal((true, "HaloPSA: signed in - 42 tickets."), ((await Test(desk)).Success, (await Test(desk)).Message));
            Assert.Equal($"{Api}/Tickets?pageinate=true&page_size=1&page_no=1", desk.Requests[1].Url);
            Assert.Equal("HaloPSA: signed in - 1 ticket.", (await Test(new Halo { Answer = (_, _) => (HttpStatusCode.OK, "{\"record_count\":1,\"tickets\":[{\"id\":1}]}") })).Message);
            Assert.Equal("HaloPSA: signed in.", (await Test(new Halo { Answer = (_, _) => (HttpStatusCode.OK, "{\"tickets\":[]}") })).Message);
            var refused = await Test(new Halo { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Authorization has been denied for this request.\"}") });
            Assert.Equal((false, "HTTP 401 Authorization has been denied for this request."), (refused.Success, refused.Message));
            var connector = new HaloPsaConnector();
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "halopsa", 5), (connector.AuthType, connector.ConnectorType, connector.Operations.Count));
        }
    }
}
