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
using Stomme.Modules.Connectors.Grafana;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Grafana (n8n's Grafana node: its dashboards, teams, team members and the organization's users): every call the
    /// server's HTTP API with the token as a bearer token; dashboards searched page by page, got by uid or address, created
    /// in a folder with tags, updated with their panels kept - read first, saved at their version - and deleted; teams
    /// searched page by page, got, created, updated with what is not given kept, and deleted; members listed, added and
    /// removed; the organization's users listed, added with a role, given a role and removed; what could go wrong refused
    /// before anything is sent; Grafana's message on a failure; a test naming the token's organization.
    /// </summary>
    public sealed class GrafanaConnectorTests
    {
        private const string Server = "https://grafana.kafe.example.test";

        private const string Api = Server + "/api";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "glsa_1" };

        private sealed class Grafana : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Grafana grafana, string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new GrafanaConnector(), Contexts.Extract(new StubEgress(grafana), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Dashboards(int first, int count) =>
            "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"uid\":\"d" + i + "\",\"title\":\"Fika " + i + "\",\"type\":\"dash-db\",\"tags\":[\"kafe\"],\"isStarred\":false}")) + "]";

        private const string Current = "{\"dashboard\":{\"id\":7,\"uid\":\"abc123\",\"title\":\"Sales\",\"version\":4,\"tags\":[\"old\"],\"panels\":[{\"id\":1,\"type\":\"timeseries\",\"title\":\"Fika\"}]},\"meta\":{\"folderUid\":\"f1\",\"url\":\"/d/abc123/sales\"}}";

        [Fact]
        public async Task Dashboards_are_searched_page_by_page_and_kept_with_their_panels()
        {
            var grafana = new Grafana
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/search?", StringComparison.Ordinal) && u.EndsWith("page=2", StringComparison.Ordinal) => (HttpStatusCode.OK, Dashboards(101, 30)),
                    ("GET", var u) when u.Contains("/search?", StringComparison.Ordinal) && u.EndsWith("page=1", StringComparison.Ordinal) => (HttpStatusCode.OK, Dashboards(1, 100)),
                    ("GET", var u) when u.Contains("/search?", StringComparison.Ordinal) => (HttpStatusCode.OK, "[]"),
                    ("GET", _) => (HttpStatusCode.OK, Current),
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"title\":\"Sales\",\"message\":\"Dashboard Sales deleted\",\"id\":7}"),
                    _ => (HttpStatusCode.OK, "{\"id\":8,\"uid\":\"new1\",\"url\":\"/d/new1/fika\",\"status\":\"success\",\"version\":1,\"slug\":\"fika\"}"),
                },
            };
            (string, string, string?) Last() => (grafana.Requests[^1].Method.Method, grafana.Requests[^1].Url, grafana.Requests[^1].Body);

            var read = await Run(grafana, GrafanaConnector.DashboardsOperation, new() { ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/search?type=dash-db&limit=100&page=1", $"{Api}/search?type=dash-db&limit=100&page=2" }, grafana.Requests.Select(request => request.Url));
            Assert.All(grafana.Requests, request => Assert.Equal("glsa_1", request.Token));
            Assert.Equal(("d1", "Fika 1", "[\"kafe\"]", false), ((string?)read[0].Fields["uid"], (string?)read[0].Fields["title"], (string?)read[0].Fields["tags"], (bool?)read[0].Fields["isStarred"]));
            grafana.Requests.Clear();
            Assert.Equal(130, (await Run(grafana, null!, new())).Count); // no operation: the dashboards - a short page the end
            Assert.Equal(2, grafana.Requests.Count);
            var full = new Grafana { Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("page=3", StringComparison.Ordinal) ? "[]" : Dashboards(1, 100)) };
            Assert.Equal(200, (await Run(full, GrafanaConnector.DashboardsOperation, new())).Count); // an empty page the end
            Assert.Equal(3, full.Requests.Count);
            var endless = new Grafana { Answer = (_, _) => (HttpStatusCode.OK, Dashboards(1, 100)) };
            Assert.Equal(1000, (await Run(endless, GrafanaConnector.DashboardsOperation, new())).Count); // 1,000 at most when no max is given
            await Run(grafana, GrafanaConnector.DashboardsOperation, new() { ["query"] = "fika & bröd", ["tags"] = "kafe, prod, kafe", ["folders"] = "f1,f2", ["max"] = "5" });
            Assert.Equal($"{Api}/search?type=dash-db&query=fika%20%26%20br%C3%B6d&tag=kafe&tag=prod&folderUIDs=f1&folderUIDs=f2&limit=5&page=1", Last().Item2);

            var got = Assert.Single(await Run(grafana, GrafanaConnector.DashboardOperation, new() { ["dashboard"] = "https://grafana.kafe.example.test/d/abc123/sales?orgId=1" }, endpoint: Server + "/api/")).Fields;
            Assert.Equal(("GET", $"{Api}/dashboards/uid/abc123", (string?)null), Last()); // by its address, the API's path given once
            Assert.Contains("\"panels\"", (string?)got["dashboard"], StringComparison.Ordinal);
            Assert.Equal("new1", (string?)Assert.Single(await Run(grafana, GrafanaConnector.CreateDashboardOperation, new() { ["title"] = "Fika & bröd", ["folder"] = "f1", ["tags"] = "kafe,prod" })).Fields["uid"]);
            Assert.Equal(("POST", $"{Api}/dashboards/db", (string?)"{\"dashboard\":{\"id\":null,\"uid\":null,\"title\":\"Fika & bröd\",\"tags\":[\"kafe\",\"prod\"]},\"overwrite\":false,\"folderUid\":\"f1\"}"), Last());
            await Run(grafana, GrafanaConnector.CreateDashboardOperation, new() { ["title"] = "Fika" });
            Assert.Equal("{\"dashboard\":{\"id\":null,\"uid\":null,\"title\":\"Fika\"},\"overwrite\":false}", Last().Item3);

            grafana.Requests.Clear();
            Assert.Equal(1L, (long?)Assert.Single(await Run(grafana, GrafanaConnector.UpdateDashboardOperation, new() { ["dashboard"] = "abc123", ["title"] = "Försäljning" })).Fields["version"]);
            Assert.Equal(new[] { ("GET", $"{Api}/dashboards/uid/abc123", (string?)null), ("POST", $"{Api}/dashboards/db", (string?)"{\"dashboard\":{\"id\":7,\"uid\":\"abc123\",\"title\":\"Försäljning\",\"version\":4,\"tags\":[\"old\"],\"panels\":[{\"id\":1,\"type\":\"timeseries\",\"title\":\"Fika\"}]},\"folderUid\":\"f1\",\"overwrite\":false}") },
                grafana.Requests.Select(request => (request.Method.Method, request.Url, request.Body))); // its panels and folder kept, at its version
            await Run(grafana, GrafanaConnector.UpdateDashboardOperation, new() { ["dashboard"] = "abc123", ["folder"] = "f2", ["tags"] = "new" });
            Assert.Equal("{\"dashboard\":{\"id\":7,\"uid\":\"abc123\",\"title\":\"Sales\",\"version\":4,\"tags\":[\"new\"],\"panels\":[{\"id\":1,\"type\":\"timeseries\",\"title\":\"Fika\"}]},\"folderUid\":\"f2\",\"overwrite\":false}", Last().Item3);
            var deleted = Assert.Single(await Run(grafana, GrafanaConnector.DeleteDashboardOperation, new() { ["dashboard"] = "abc123" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/dashboards/uid/abc123", (string?)null), Last());
            Assert.Equal(("abc123", "Sales", true), ((string?)deleted["uid"], (string?)deleted["title"], (bool?)deleted["deleted"]));
        }

        [Fact]
        public async Task Teams_members_and_users_are_kept()
        {
            static string Teams(int first, int count) => "{\"totalCount\":130,\"teams\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"name\":\"Lag " + i + "\",\"memberCount\":2}")) + "],\"page\":1,\"perPage\":100}";
            var grafana = new Grafana
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/teams/search?", StringComparison.Ordinal) => (HttpStatusCode.OK, u.EndsWith("page=2", StringComparison.Ordinal) ? Teams(101, 29) : Teams(1, 100)),
                    ("GET", var u) when u.EndsWith("/members", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"userId\":3,\"login\":\"ada\",\"email\":\"ada@kafe.example.test\"},{\"userId\":4,\"login\":\"bo\"}]"),
                    ("GET", var u) when u.EndsWith("/org/users", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"userId\":3,\"login\":\"ada\",\"role\":\"Admin\"},{\"userId\":4,\"login\":\"bo\",\"role\":\"Viewer\"}]"),
                    ("GET", var u) when u.Contains("/teams/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"id\":5,\"name\":\"Fika\",\"email\":\"fika@kafe.example.test\",\"memberCount\":2}"),
                    ("POST", var u) when u.EndsWith("/teams", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"message\":\"Team created\",\"teamId\":5}"),
                    ("POST", var u) when u.EndsWith("/org/users", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"message\":\"User added to organization\",\"userId\":12}"),
                    _ => (HttpStatusCode.OK, "{\"message\":\"Done\"}"),
                },
            };
            (string, string, string?) Last() => (grafana.Requests[^1].Method.Method, grafana.Requests[^1].Url, grafana.Requests[^1].Body);

            Assert.Equal(120, (await Run(grafana, GrafanaConnector.TeamsOperation, new() { ["max"] = "120" })).Count);
            Assert.Equal(new[] { $"{Api}/teams/search?perpage=100&page=1", $"{Api}/teams/search?perpage=100&page=2" }, grafana.Requests.Select(request => request.Url));
            await Run(grafana, GrafanaConnector.TeamsOperation, new() { ["name"] = "Fika & bröd", ["query"] = "fi", ["max"] = "1" });
            Assert.Equal($"{Api}/teams/search?name=Fika%20%26%20br%C3%B6d&query=fi&perpage=1&page=1", Last().Item2);
            Assert.Equal(2L, (long?)Assert.Single(await Run(grafana, GrafanaConnector.TeamOperation, new() { ["team"] = "5" })).Fields["memberCount"]);
            Assert.Equal($"{Api}/teams/5", Last().Item2);
            var made = Assert.Single(await Run(grafana, GrafanaConnector.CreateTeamOperation, new() { ["name"] = "Fika & bröd", ["email"] = "fika@kafe.example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/teams", (string?)"{\"name\":\"Fika & bröd\",\"email\":\"fika@kafe.example.test\"}"), Last());
            Assert.Equal((5L, "Fika & bröd", "Team created"), ((long?)made["id"], (string?)made["name"], (string?)made["message"]));
            await Run(grafana, GrafanaConnector.CreateTeamOperation, new() { ["name"] = "Bröd" });
            Assert.Equal("{\"name\":\"Bröd\"}", Last().Item3);
            grafana.Requests.Clear();
            var updated = Assert.Single(await Run(grafana, GrafanaConnector.UpdateTeamOperation, new() { ["team"] = "5", ["name"] = "Fika!" })).Fields;
            Assert.Equal(new[] { ("GET", $"{Api}/teams/5", (string?)null), ("PUT", $"{Api}/teams/5", (string?)"{\"name\":\"Fika!\",\"email\":\"fika@kafe.example.test\"}") }, grafana.Requests.Select(request => (request.Method.Method, request.Url, request.Body))); // its e-mail kept
            Assert.Equal(("Fika!", "fika@kafe.example.test"), ((string?)updated["name"], (string?)updated["email"]));
            await Run(grafana, GrafanaConnector.UpdateTeamOperation, new() { ["team"] = "5", ["email"] = "lag@kafe.example.test" });
            Assert.Equal("{\"name\":\"Fika\",\"email\":\"lag@kafe.example.test\"}", Last().Item3); // its name kept
            Assert.True((bool?)Assert.Single(await Run(grafana, GrafanaConnector.DeleteTeamOperation, new() { ["team"] = "5" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/teams/5", (string?)null), Last());

            Assert.Equal(new[] { "ada" }, (await Run(grafana, GrafanaConnector.TeamMembersOperation, new() { ["team"] = "5", ["max"] = "1" })).Select(member => (string?)member.Fields["login"]));
            Assert.Equal($"{Api}/teams/5/members", Last().Item2);
            var added = Assert.Single(await Run(grafana, GrafanaConnector.AddTeamMemberOperation, new() { ["team"] = "5", ["user"] = "3" })).Fields;
            Assert.Equal(("POST", $"{Api}/teams/5/members", (string?)"{\"userId\":3}"), Last());
            Assert.Equal((5L, 3L, true), ((long?)added["team"], (long?)added["user"], (bool?)added["added"]));
            Assert.True((bool?)Assert.Single(await Run(grafana, GrafanaConnector.RemoveTeamMemberOperation, new() { ["team"] = "5", ["user"] = "3" })).Fields["removed"]);
            Assert.Equal(("DELETE", $"{Api}/teams/5/members/3", (string?)null), Last());

            Assert.Equal(new[] { "Admin", "Viewer" }, (await Run(grafana, GrafanaConnector.UsersOperation, new())).Select(user => (string?)user.Fields["role"]));
            Assert.Equal($"{Api}/org/users", Last().Item2);
            Assert.Equal("Admin", (string?)Assert.Single(await Run(grafana, GrafanaConnector.UsersOperation, new() { ["max"] = "1" })).Fields["role"]); // no more than asked
            var joined = Assert.Single(await Run(grafana, GrafanaConnector.AddUserOperation, new() { ["login"] = "cy@kafe.example.test", ["role"] = "editor" })).Fields;
            Assert.Equal(("POST", $"{Api}/org/users", (string?)"{\"loginOrEmail\":\"cy@kafe.example.test\",\"role\":\"Editor\"}"), Last());
            Assert.Equal((12L, "Editor"), ((long?)joined["id"], (string?)joined["role"]));
            await Run(grafana, GrafanaConnector.AddUserOperation, new() { ["login"] = "dee" });
            Assert.Equal("{\"loginOrEmail\":\"dee\",\"role\":\"Viewer\"}", Last().Item3); // Viewer by default
            await Run(grafana, GrafanaConnector.UpdateUserOperation, new() { ["user"] = "12", ["role"] = "ADMIN" });
            Assert.Equal(("PATCH", $"{Api}/org/users/12", (string?)"{\"role\":\"Admin\"}"), Last());
            Assert.True((bool?)Assert.Single(await Run(grafana, GrafanaConnector.RemoveUserOperation, new() { ["user"] = "12" })).Fields["removed"]);
            Assert.Equal(("DELETE", $"{Api}/org/users/12", (string?)null), Last());
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_grafanas_words_on_a_failure()
        {
            var grafana = new Grafana();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(grafana, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Grafana has no operation 'folders' - dashboards, dashboard, create_dashboard,", await Refused("folders", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GrafanaConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the dashboard - its uid or address.", await Refused(GrafanaConnector.DashboardOperation, new()));
            Assert.Equal("'abc/../x' is not a dashboard's uid - letters, digits, '-' and '_' (dashboard).", await Refused(GrafanaConnector.DeleteDashboardOperation, new() { ["dashboard"] = "abc/../x" }));
            Assert.Equal("'https://grafana.kafe.example.test/dashboards' is not a dashboard's address - https://grafana.example.com/d/abc123/sales (dashboard).", await Refused(GrafanaConnector.DashboardOperation, new() { ["dashboard"] = "https://grafana.kafe.example.test/dashboards" }));
            Assert.Equal("Give the dashboard's title.", await Refused(GrafanaConnector.CreateDashboardOperation, new() { ["folder"] = "f1" }));
            Assert.Equal("'f 1' is not a folder's uid - letters, digits, '-' and '_' (folder).", await Refused(GrafanaConnector.CreateDashboardOperation, new() { ["title"] = "Fika", ["folder"] = "f 1" }));
            Assert.Equal("'" + new string('t', 51) + "' is longer than a tag's 50 characters (tags).", await Refused(GrafanaConnector.CreateDashboardOperation, new() { ["title"] = "Fika", ["tags"] = "kafe," + new string('t', 51) }));
            Assert.Equal("'f/2' is not a folder's uid (folders).", await Refused(GrafanaConnector.DashboardsOperation, new() { ["folders"] = "f1,f/2" }));
            Assert.Equal("Give what to change - its title, folder or tags.", await Refused(GrafanaConnector.UpdateDashboardOperation, new() { ["dashboard"] = "abc123" }));
            Assert.Equal("Give the team's id.", await Refused(GrafanaConnector.TeamOperation, new()));
            Assert.Equal("'fika' is not an id (team).", await Refused(GrafanaConnector.DeleteTeamOperation, new() { ["team"] = "fika" }));
            Assert.Equal("Give the team's name.", await Refused(GrafanaConnector.CreateTeamOperation, new() { ["email"] = "fika@kafe.example.test" }));
            Assert.Equal("'fika' is not an e-mail (email).", await Refused(GrafanaConnector.CreateTeamOperation, new() { ["name"] = "Fika", ["email"] = "fika" }));
            Assert.Equal("'fika @kafe' is not an e-mail (email).", await Refused(GrafanaConnector.UpdateTeamOperation, new() { ["team"] = "5", ["email"] = "fika @kafe" }));
            Assert.Equal("Give what to change - its name or e-mail.", await Refused(GrafanaConnector.UpdateTeamOperation, new() { ["team"] = "5" }));
            Assert.Equal("Give the user's id.", await Refused(GrafanaConnector.AddTeamMemberOperation, new() { ["team"] = "5" }));
            Assert.Equal("'ada' is not an id (user).", await Refused(GrafanaConnector.RemoveTeamMemberOperation, new() { ["team"] = "5", ["user"] = "ada" }));
            Assert.Equal("Give the user's login or e-mail.", await Refused(GrafanaConnector.AddUserOperation, new() { ["role"] = "Viewer" }));
            Assert.Equal("'Owner' is not Viewer, Editor or Admin (role).", await Refused(GrafanaConnector.AddUserOperation, new() { ["login"] = "ada", ["role"] = "Owner" }));
            Assert.Equal("Give the role - Viewer, Editor or Admin.", await Refused(GrafanaConnector.UpdateUserOperation, new() { ["user"] = "12" }));
            Assert.Equal("Give the user's id.", await Refused(GrafanaConnector.RemoveUserOperation, new()));
            Assert.Equal("Give the Grafana server's address - https://grafana.example.com.", await Refused(GrafanaConnector.UsersOperation, new(), endpoint: ""));
            Assert.Equal("The Grafana connection has no token.", await Refused(GrafanaConnector.UsersOperation, new(), credentials: new()));
            Assert.Empty(grafana.Requests); // nothing sent
            Assert.Equal("Grafana has no dashboard 'abc123'.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Grafana(), GrafanaConnector.UpdateDashboardOperation, new() { ["dashboard"] = "abc123", ["title"] = "Fika" }))).Message);

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Grafana { Answer = (_, _) => (status, answer) }, GrafanaConnector.CreateTeamOperation, new() { ["name"] = "Fika" }))).Message;
            Assert.Equal("HTTP 409: Team name taken", await Failed(HttpStatusCode.Conflict, "{\"message\":\"Team name taken\"}"));
            Assert.Equal("HTTP 401: Invalid API key", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"Invalid API key\",\"traceID\":\"\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Grafana answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Grafana</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_tokens_organization()
        {
            Task<ConnectionTestResult> Test(Grafana grafana) =>
                new GrafanaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(grafana), Server, null, Credentials));
            var organization = new Grafana { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":1,\"name\":\"Kafé\"}") };
            var tested = await Test(organization);
            Assert.Equal((true, "Grafana: the token is Kafé's."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/org", "GET", "glsa_1"), (organization.Requests[0].Url, organization.Requests[0].Method.Method, organization.Requests[0].Token));
            Assert.Equal("Grafana: the token is an organization's.", (await Test(new Grafana())).Message);
            var refused = await Test(new Grafana { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Invalid API key\"}") });
            Assert.Equal((false, "HTTP 401: Invalid API key"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "grafana"), (new GrafanaConnector().AuthType, new GrafanaConnector().ConnectorType));
            Assert.Equal(17, new GrafanaConnector().Operations.Count);
            Assert.Equal(("token", "Service account token (Administration > Service accounts)"), (Assert.Single(new GrafanaConnector().CredentialLabels!).Key, Assert.Single(new GrafanaConnector().CredentialLabels!).Value)); // in Grafana's words
        }
    }
}
