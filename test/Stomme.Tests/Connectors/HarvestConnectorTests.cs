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
using Stomme.Modules.Connectors.Harvest;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Harvest (n8n's Harvest node: an account's clients, contacts, projects, tasks, users, invoices, estimates,
    /// expenses and time entries created, got, read, updated and deleted, a timer restarted and stopped, the company read): every
    /// call Harvest's API v2 with the token as a bearer token, the account it acts in and a User-Agent; every list page by page,
    /// the page size fixed and no bigger than asked, Harvest's next page none the end, with its filters held to their kinds; each
    /// resource got, created, updated and deleted at its path; a timer restarted and stopped, an external reference removed;
    /// what could go wrong refused before anything is sent; Harvest's word on a failure; a test naming the account's company.
    /// </summary>
    public sealed class HarvestConnectorTests
    {
        private const string Api = "https://harvest.example.test/v2";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "harvest-token-1" };

        private static readonly Dictionary<string, string> Settings = new() { ["account_id"] = "123456" };

        private sealed class Harvest : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Account, string? Agent, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Harvest-Account-Id", out var account) ? account.Single() : null, string.Join(" ", request.Headers.UserAgent.Select(agent => agent.ToString())), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Harvest harvest, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new HarvestConnector(), Contexts.Extract(new StubEgress(harvest), Api, operation, parameters, settings: settings ?? Settings, credentials: credentials ?? Credentials));

        private static string Entries(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":" + i + ",\"notes\":\"Café " + i + "\",\"hours\":1.5,\"is_running\":false,\"project\":{\"id\":7,\"name\":\"Café\"},\"invoice\":null}")) + "]";

        [Fact]
        public async Task Lists_are_read_page_by_page_with_their_filters()
        {
            var harvest = new Harvest
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=2&", StringComparison.Ordinal)
                    ? "{\"time_entries\":" + Entries(101, 50) + ",\"per_page\":100,\"page\":2,\"next_page\":null}"
                    : "{\"time_entries\":" + Entries(1, 100) + ",\"per_page\":100,\"page\":1,\"next_page\":2}"),
            };

            var entries = await Run(harvest, "time_entries", new() { ["user_id"] = "11", ["client_id"] = "12", ["project_id"] = "7", ["task_id"] = "13", ["is_billed"] = "TRUE", ["is_running"] = "false", ["from"] = "2026-10-01", ["to"] = "2026-10-31", ["updated_since"] = "2026-10-01T02:00:00+02:00", ["external_reference_id"] = "JIRA-1 é" });
            Assert.Equal(150, entries.Count); // a thousand at most by default
            const string Filters = "user_id=11&client_id=12&project_id=7&task_id=13&is_billed=true&is_running=false&from=2026-10-01&to=2026-10-31&external_reference_id=JIRA-1%20%C3%A9&updated_since=2026-10-01T00%3A00%3A00Z";
            Assert.Equal(new[] { $"{Api}/time_entries?{Filters}&page=1&per_page=100", $"{Api}/time_entries?{Filters}&page=2&per_page=100" }, harvest.Requests.Select(request => request.Url)); // Harvest's next page none the end; a moment in UTC
            Assert.All(harvest.Requests, request => Assert.Equal(("Bearer harvest-token-1", "123456", "Stomme pipelines"), (request.Authorization, request.Account, request.Agent)));
            var first = entries[0].Fields;
            Assert.Equal((1L, "Café 1", 1.5, false, "{\"id\":7,\"name\":\"Café\"}"), ((long?)first["id"], (string?)first["notes"], (double?)first["hours"], (bool?)first["is_running"], (string?)first["project"]));
            Assert.Null(first["invoice"]);

            Assert.Equal(30, (await Run(harvest, "time_entries", new() { ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/time_entries?page=1&per_page=30", harvest.Requests[^1].Url); // a page no bigger than asked
            harvest.Answer = (_, _) => (HttpStatusCode.OK, "{\"invoices\":[{\"id\":5,\"state\":\"paid\"}],\"next_page\":null}");
            Assert.Equal("paid", (string?)Assert.Single(await Run(harvest, "invoices", new() { ["state"] = "Paid", ["client_id"] = "12", ["project_id"] = "7", ["from"] = "2026-01-01", ["to"] = "2026-12-31" })).Fields["state"]);
            Assert.Equal($"{Api}/invoices?client_id=12&project_id=7&state=paid&from=2026-01-01&to=2026-12-31&page=1&per_page=100", harvest.Requests[^1].Url); // a list's records under its own name
            foreach (var (operation, filters, query) in new (string, Dictionary<string, string>, string)[]
            {
                ("clients", new() { ["is_active"] = "true" }, "is_active=true&"),
                ("contacts", new() { ["client_id"] = "12" }, "client_id=12&"),
                ("projects", new() { ["is_active"] = "false", ["client_id"] = "12" }, "is_active=false&client_id=12&"),
                ("tasks", new() { ["is_active"] = "true" }, "is_active=true&"),
                ("users", new() { ["is_active"] = "true", ["updated_since"] = "2026-10-01T00:00:00Z" }, "is_active=true&updated_since=2026-10-01T00%3A00%3A00Z&"),
                ("estimates", new() { ["state"] = "accepted", ["client_id"] = "12", ["from"] = "2026-01-01", ["to"] = "2026-02-01" }, "client_id=12&state=accepted&from=2026-01-01&to=2026-02-01&"),
                ("expenses", new() { ["user_id"] = "11", ["client_id"] = "12", ["project_id"] = "7", ["is_billed"] = "false", ["from"] = "2026-01-01", ["to"] = "2026-02-01" }, "user_id=11&client_id=12&project_id=7&is_billed=false&from=2026-01-01&to=2026-02-01&"),
            })
            {
                await Run(harvest, operation, filters);
                Assert.Equal($"{Api}/{operation}?{query}page=1&per_page=100", harvest.Requests[^1].Url);
            }
        }

        [Fact]
        public async Task Each_resource_is_got_created_updated_and_deleted_at_its_path_and_a_timer_restarted_and_stopped()
        {
            var harvest = new Harvest { Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/company", StringComparison.Ordinal) ? "{\"name\":\"Café AB\",\"base_uri\":\"https://cafe.harvestapp.com\"}" : "{\"id\":42,\"name\":\"Café\",\"is_active\":true}") };
            (string, string, string?) Last() => (harvest.Requests[^1].Method.Method, harvest.Requests[^1].Url, harvest.Requests[^1].Body);

            Assert.Equal("Café AB", (string?)Assert.Single(await Run(harvest, "company", new())).Fields["name"]);
            Assert.Equal(("GET", $"{Api}/company", (string?)null), Last());
            await Run(harvest, "me", new());
            Assert.Equal(("GET", $"{Api}/users/me", (string?)null), Last());
            foreach (var (plural, singular) in new[] { ("clients", "client"), ("contacts", "contact"), ("projects", "project"), ("tasks", "task"), ("users", "user"), ("invoices", "invoice"), ("estimates", "estimate"), ("expenses", "expense"), ("time_entries", "time_entry") })
            {
                var got = Assert.Single(await Run(harvest, "get_" + singular, new() { ["id"] = "42" })).Fields;
                Assert.Equal(("GET", $"{Api}/{plural}/42", (string?)null), Last());
                Assert.Equal((42L, "Café", true), ((long?)got["id"], (string?)got["name"], (bool?)got["is_active"]));
                await Run(harvest, "create_" + singular, new() { ["fields"] = "{\"name\": \"Café AB\", \"is_active\": true}" });
                Assert.Equal(("POST", $"{Api}/{plural}", (string?)"{\"name\":\"Café AB\",\"is_active\":true}"), Last());
                await Run(harvest, "update_" + singular, new() { ["id"] = "42", ["fields"] = "{\"is_active\": false}" });
                Assert.Equal(("PATCH", $"{Api}/{plural}/42", (string?)"{\"is_active\":false}"), Last());
                var gone = Assert.Single(await Run(harvest, "delete_" + singular, new() { ["id"] = "42" })).Fields;
                Assert.Equal(("DELETE", $"{Api}/{plural}/42", (string?)null), Last());
                Assert.Equal((42L, true), ((long?)gone["id"], (bool?)gone["deleted"]));
            }

            await Run(harvest, "restart_time_entry", new() { ["id"] = "42" });
            Assert.Equal(("PATCH", $"{Api}/time_entries/42/restart", (string?)null), Last());
            await Run(harvest, "stop_time_entry", new() { ["id"] = "42" });
            Assert.Equal(("PATCH", $"{Api}/time_entries/42/stop", (string?)null), Last());
            await Run(harvest, "delete_time_entry_external_reference", new() { ["id"] = "42" });
            Assert.Equal(("DELETE", $"{Api}/time_entries/42/external_reference", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_harvests_word_on_a_failure()
        {
            var harvest = new Harvest();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(harvest, operation, parameters, settings, credentials))).Message;

            Assert.StartsWith("Harvest has no operation 'reports' - company, me, clients,", await Refused("reports", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("clients", new() { ["max"] = "0" }));
            Assert.Equal("Give the id.", await Refused("get_client", new()));
            Assert.Equal("'0' is not an id - a number.", await Refused("get_project", new() { ["id"] = "0" }));
            Assert.Equal("'4/2' is not an id - a number.", await Refused("stop_time_entry", new() { ["id"] = "4/2" }));
            Assert.Equal("'acme' is not an id - a number (client_id).", await Refused("contacts", new() { ["client_id"] = "acme" }));
            Assert.Equal("'yes' is not true or false (is_active).", await Refused("tasks", new() { ["is_active"] = "yes" }));
            Assert.Equal("'2026-13-01' is not a date - 2026-10-01 (from).", await Refused("expenses", new() { ["from"] = "2026-13-01" }));
            Assert.Equal("'2026-10-01T00:00:00' is not a date and time with its zone (updated_since).", await Refused("users", new() { ["updated_since"] = "2026-10-01T00:00:00" }));
            Assert.Equal("'lost' is not draft, open, paid, closed, sent, accepted or declined (state).", await Refused("invoices", new() { ["state"] = "lost" }));
            Assert.Equal("The external_reference_id is one line of up to 255 characters.", await Refused("time_entries", new() { ["external_reference_id"] = new string('x', 256) }));
            Assert.Equal("The external_reference_id is one line of up to 255 characters.", await Refused("time_entries", new() { ["external_reference_id"] = "a\u0001b" }));
            Assert.Equal("Give the fields - a JSON object as Harvest names them.", await Refused("create_task", new()));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"name\": \"Café AB\"}.", await Refused("create_client", new() { ["fields"] = "name=Café" }));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"name\": \"Café AB\"}.", await Refused("update_user", new() { ["id"] = "42", ["fields"] = "{}" }));
            Assert.Equal("The Harvest connection has no access token.", await Refused("company", new(), credentials: new()));
            Assert.Equal("The Harvest connection has no account - its id, beside the token on Harvest's Developers page.", await Refused("company", new(), settings: new()));
            Assert.Equal("'acme' is not a Harvest account's id - a number.", await Refused("company", new(), settings: new() { ["account_id"] = "acme" }));
            Assert.Empty(harvest.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Harvest { Answer = (_, _) => (status, answer) }, "clients", new()))).Message;
            Assert.Equal("HTTP 422: Name has already been taken", await Failed((HttpStatusCode)422, "{\"message\":\"Name has already been taken\"}"));
            Assert.Equal("HTTP 401: The access token provided is expired, revoked, malformed or invalid for other reasons.", await Failed(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\",\"error_description\":\"The access token provided is expired, revoked, malformed or invalid for other reasons.\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Harvest answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as nothing
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_accounts_company()
        {
            var harvest = new Harvest { Answer = (_, _) => (HttpStatusCode.OK, "{\"name\":\"Café AB\",\"base_uri\":\"https://cafe.harvestapp.com\"}") };
            var tested = await new HarvestConnector().TestConnectionAsync(Contexts.Test(new StubEgress(harvest), "", Settings, Credentials));
            Assert.Equal((true, "Harvest: Café AB answers."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.harvestapp.com/v2/company", "Bearer harvest-token-1", "123456"), (harvest.Requests[0].Url, harvest.Requests[0].Authorization, harvest.Requests[0].Account)); // Harvest's own address when none is given
            var nameless = new Harvest { Answer = (_, _) => (HttpStatusCode.OK, "{}") };
            Assert.Equal("Harvest: the account answers.", (await new HarvestConnector().TestConnectionAsync(Contexts.Test(new StubEgress(nameless), Api, Settings, Credentials))).Message);
            var refused = new Harvest { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\",\"error_description\":\"The access token provided is invalid.\"}") };
            var denied = await new HarvestConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), Api, Settings, Credentials));
            Assert.Equal((false, "HTTP 401: The access token provided is invalid."), (denied.Success, denied.Message));
            var unaccounted = await new HarvestConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), Api, new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "The Harvest connection has no account - its id, beside the token on Harvest's Developers page."), (unaccounted.Success, unaccounted.Message));
            Assert.Equal((AuthenticationType.Bearer, "harvest"), (new HarvestConnector().AuthType, new HarvestConnector().ConnectorType));
            Assert.Equal(50, new HarvestConnector().Operations.Count);
            Assert.Equal(new[] { "account_id" }, new HarvestConnector().ConfigSchema.Fields.Where(field => field.Required).Select(field => field.Name));
            ConnectorOperation Operation(string name) => new HarvestConnector().Operations.Single(operation => operation.Name == name);
            Assert.Equal(new[] { "user_id", "client_id", "project_id", "task_id", "is_billed", "is_running", "from", "to", "external_reference_id", "updated_since", "max" }, Operation("time_entries").Parameters!.Fields.Select(field => field.Name));
            Assert.Equal(new[] { "id", "fields" }, Operation("update_invoice").Parameters!.Fields.Select(field => field.Name));
            Assert.Equal(new[] { "fields" }, Operation("create_expense").Parameters!.Fields.Select(field => field.Name));
            Assert.Equal(new[] { "id" }, Operation("stop_time_entry").Parameters!.Fields.Select(field => field.Name));
        }
    }
}
