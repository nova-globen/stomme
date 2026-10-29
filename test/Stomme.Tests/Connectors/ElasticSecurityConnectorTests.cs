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
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.ElasticSecurity;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Elastic Security (n8n's Elastic Security node) without a server: every id, choice, tag, length and text held to
    /// Kibana's shape before anything is sent; the user as Basic and Kibana's kbn-xsrf header on every call, in the connection's
    /// space; cases found by their filters page by page with the page the same size each time; a case made with no external
    /// connector, changed at the version Kibana gave it, its tags added and removed - one already as asked leaving it as it is -
    /// and deleted; its comments listed, added from the Query box, changed and deleted; the status counts from Kibana's find;
    /// Kibana's message on a failure, a 409 saying another change landed first; a test reading the open cases' count.
    /// </summary>
    public sealed class ElasticSecurityConnectorTests
    {
        private const string Kibana = "https://kibana.kafe.example:5601";

        private const string CaseId = "5d801d85-1245-48ab-9999-84e858897359";

        private const string CommentId = "d9d4649f-b0df-4c4a-867f-9a5ae3fa6637";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe-analyst", ["password"] = "kafe-secret" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Xsrf, string? Body);

        private sealed class Elastic : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Elastic(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("kbn-xsrf", out var xsrf) ? xsrf.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static string Case(string tags = "\"phishing\",\"kafe\"", string version = "WzIsMV0=", string status = "open") =>
            $"{{\"id\":\"{CaseId}\",\"version\":\"{version}\",\"title\":\"Kafé phishing wave\",\"description\":\"Mails to the till staff\",\"status\":\"{status}\",\"severity\":\"medium\",\"owner\":\"securitySolution\",\"tags\":[{tags}],\"totalComment\":1,\"created_by\":{{\"username\":\"elastic\"}},\"comments\":[{{\"id\":\"{CommentId}\",\"type\":\"user\",\"comment\":\"Seen in the till logs\",\"version\":\"WzgsMV0=\"}}]}}";

        private const string Comment = "{\"id\":\"" + CommentId + "\",\"type\":\"user\",\"comment\":\"Seen in the till logs\",\"owner\":\"securitySolution\",\"version\":\"WzgsMV0=\"}";

        private static string Found(int from, int count) => "{\"page\":1,\"per_page\":100,\"total\":250,\"cases\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => $"{{\"id\":\"00000000-0000-4000-8000-{n:D12}\",\"title\":\"Case {n}\",\"status\":\"open\"}}")) + "],\"count_open_cases\":7,\"count_in_progress_cases\":2,\"count_closed_cases\":41}";

        private static string Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("kafe-analyst:kafe-secret"));

        private static Task<List<ConnectorRecord>> Run(Elastic elastic, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = Kibana) =>
            Contexts.DrainAsync(new ElasticSecurityConnector(), Contexts.Extract(new StubEgress(elastic), endpoint, operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_elastic_security_node()
        {
            IConnector connector = new ElasticSecurityConnector();
            Assert.Equal(("elasticsecurity", "Elastic Security", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[]
            {
                "cases", "case", "case_create", "case_update", "case_delete", "case_status",
                "case_comments", "case_comment", "case_comment_add", "case_comment_update", "case_comment_delete", "case_tag_add", "case_tag_remove",
            }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_kibanas_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = Kibana)
            {
                var elastic = new Elastic(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elastic, operation, parameters, settings, endpoint));
                Assert.Empty(elastic.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("Elastic Security has no operation 'connector_create' - cases, case,", await Refused("connector_create", new()), StringComparison.Ordinal);
            Assert.Equal("Give a case - its id (case).", await Refused("case", new()));
            Assert.Equal("'42' is not a case's id - 5d801d85-1245-48ab-9999-84e858897359 (case).", await Refused("case_delete", new() { ["case"] = "42" }));
            Assert.Equal("'../x' is not a comment's id - 5d801d85-1245-48ab-9999-84e858897359 (comment_id).", await Refused("case_comment", new() { ["case"] = CaseId, ["comment_id"] = "../x" }));
            Assert.Equal("'pending' is not open, in-progress or closed (status).", await Refused("cases", new() { ["status"] = "pending" }));
            Assert.Equal("'extreme' is not low, medium, high or critical (severity).", await Refused("cases", new() { ["severity"] = "extreme" }));
            Assert.Equal("'owner' is not createdAt, updatedAt, closedAt, title, status, severity or category (sort).", await Refused("cases", new() { ["sort"] = "owner" }));
            Assert.Equal("'up' is not asc or desc (order).", await Refused("cases", new() { ["order"] = "up" }));
            Assert.Equal("A search is at most 500 characters, on one line (search).", await Refused("cases", new() { ["search"] = "till\nlogs" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("cases", new() { ["max"] = "0" }));
            Assert.Equal("Give the case's title (title).", await Refused("case_create", new()));
            Assert.Equal("A case's title is at most 160 characters, on one line (title).", await Refused("case_create", new() { ["title"] = new string('k', 161) }));
            Assert.Equal("Give the description (description) - over several lines in the step's Query box.", await Refused("case_create", new() { ["title"] = "Kassan" })); // Kibana refuses a case without one
            Assert.Equal("A description is at most 30000 characters - this is 30001 (description).", await Refused("case_create", new() { ["title"] = "Kassan", ["description"] = new string('k', 30001) }));
            Assert.Equal("A case has at most 200 tags, each at most 256 characters on one line (tags).", await Refused("case_create", new() { ["title"] = "Kassan", ["description"] = "Kassan", ["tags"] = new string('k', 257) }));
            Assert.Equal("'kafe' is not securitySolution, observability or cases (owner).", await Refused("case_create", new() { ["title"] = "Kassan", ["description"] = "Kassan", ["owner"] = "kafe" }));
            Assert.Equal("A category is at most 50 characters, on one line (category).", await Refused("case_create", new() { ["title"] = "Kassan", ["description"] = "Kassan", ["category"] = new string('k', 51) }));
            Assert.Equal("'ja' is not true or false (sync_alerts).", await Refused("case_create", new() { ["title"] = "Kassan", ["description"] = "Kassan", ["sync_alerts"] = "ja" }));
            Assert.Equal("Give what to change - title, description, status, severity, tags, category or alert sync.", await Refused("case_update", new() { ["case"] = CaseId }));
            Assert.Equal("Give the comment (comment) - over several lines in the step's Query box.", await Refused("case_comment_add", new() { ["case"] = CaseId }));
            Assert.Equal("Give the tag - contained (tag).", await Refused("case_tag_add", new() { ["case"] = CaseId }));
            Assert.Equal("'a,b' is not a tag - at most 256 characters, one line, no comma (tag).", await Refused("case_tag_remove", new() { ["case"] = CaseId, ["tag"] = "a,b" }));
            Assert.Equal("'SOC Team' is not a Kibana space's id - soc, lower-case letters, digits, _ and - (space).", await Refused("cases", new(), new() { ["space"] = "SOC Team" }));
            Assert.Equal("Give Kibana's address - https://kibana.kafe.example:5601.", await Refused("cases", new(), endpoint: ""));
            Assert.Equal("'https://kibana.kafe.example/?space=soc' is not Kibana's address - https://kibana.kafe.example:5601, with no query.", await Refused("cases", new(), endpoint: "https://kibana.kafe.example/?space=soc"));
        }

        [Fact]
        public async Task Cases_are_found_by_their_filters_page_by_page_in_the_connections_space()
        {
            var elastic = new Elastic(sent => (HttpStatusCode.OK, sent.Url.Contains("page=2", StringComparison.Ordinal) ? Found(101, 100) : sent.Url.Contains("page=3", StringComparison.Ordinal) ? Found(201, 30) : Found(1, 100)));
            var cases = await Run(elastic, "cases", new()
            {
                ["status"] = "In-Progress", ["severity"] = "HIGH", ["tags"] = "phishing, kafe", ["search"] = "till logs", ["sort"] = "updatedAt", ["order"] = "DESC", ["max"] = "150",
            }, new() { ["space"] = "soc" });
            Assert.Equal(150, cases.Count);
            const string Filters = "status=in-progress&severity=high&tags=phishing&tags=kafe&search=till%20logs&sortField=updatedAt&sortOrder=desc";
            Assert.Equal(new[] { Kibana + "/s/soc/api/cases/_find?" + Filters + "&page=1&perPage=100", Kibana + "/s/soc/api/cases/_find?" + Filters + "&page=2&perPage=100" }, elastic.Requests.Select(sent => sent.Url));
            Assert.All(elastic.Requests, sent => Assert.Equal((Basic, "true"), (sent.Authorization, sent.Xsrf)));
            Assert.Equal(("00000000-0000-4000-8000-000000000101", "Case 101"), ((string?)cases[100].Fields["id"], (string?)cases[100].Fields["title"]));

            var defaults = new Elastic(_ => (HttpStatusCode.OK, Found(1, 3)));
            Assert.Equal(3, (await Run(defaults, "cases", new(), new() { ["space"] = "default" }, Kibana + "/kibana/")).Count); // a short page is the last
            Assert.Equal(Kibana + "/kibana/api/cases/_find?sortField=createdAt&sortOrder=asc&page=1&perPage=50", Assert.Single(defaults.Requests).Url); // n8n's createdAt, ascending; Kibana under a path, the default space

            var counts = Assert.Single(await Run(defaults, "case_status", new(), null, Kibana + "/kibana")).Fields;
            Assert.Equal((7L, 2L, 41L), ((long?)counts["count_open_cases"], (long?)counts["count_in_progress_cases"], (long?)counts["count_closed_cases"]));
            Assert.Equal(Kibana + "/kibana/api/cases/_find?perPage=1", defaults.Requests.Last().Url); // Kibana 9 has no status endpoint
        }

        [Fact]
        public async Task A_case_is_made_changed_at_its_version_tagged_and_deleted()
        {
            var elastic = new Elastic(sent => sent.Method switch
            {
                "PATCH" => (HttpStatusCode.OK, "[" + Case(version: "WzMsMV0=", status: "in-progress") + "]"),
                "DELETE" => (HttpStatusCode.NoContent, string.Empty),
                _ => (HttpStatusCode.OK, Case()),
            });
            var made = Assert.Single(await Run(elastic, "case_create", new()
            {
                ["title"] = "Kafé phishing wave", ["query"] = "Mails to the till staff\nfrom kassa.example", ["tags"] = "phishing, kafe, phishing", ["severity"] = "Medium", ["category"] = "Phishing", ["sync_alerts"] = "True",
            })).Fields;
            Assert.Equal((CaseId, "WzIsMV0=", "[\"phishing\",\"kafe\"]", "{\"username\":\"elastic\"}"), ((string?)made["id"], (string?)made["version"], (string?)made["tags"], (string?)made["created_by"]));
            Assert.Equal("{\"title\":\"Kafé phishing wave\",\"description\":\"Mails to the till staff\\nfrom kassa.example\",\"tags\":[\"phishing\",\"kafe\"],\"connector\":{\"id\":\"none\",\"name\":\"none\",\"type\":\".none\",\"fields\":null},\"settings\":{\"syncAlerts\":true},\"owner\":\"securitySolution\",\"severity\":\"medium\",\"category\":\"Phishing\"}", elastic.Requests[0].Body);

            elastic.Requests.Clear();
            var changed = Assert.Single(await Run(elastic, "case_update", new() { ["case"] = CaseId.ToUpperInvariant(), ["status"] = "in-progress", ["title"] = "Kafé phishing wave, contained", ["tags"] = "phishing, contained" })).Fields;
            Assert.Equal(("in-progress", "WzMsMV0="), ((string?)changed["status"], (string?)changed["version"]));
            await Run(elastic, "case_tag_add", new() { ["case"] = CaseId, ["tag"] = "contained" });
            await Run(elastic, "case_tag_add", new() { ["case"] = CaseId, ["tag"] = "kafe" }); // already there: as it is
            await Run(elastic, "case_tag_remove", new() { ["case"] = CaseId, ["tag"] = "kafe" });
            await Run(elastic, "case_tag_remove", new() { ["case"] = CaseId, ["tag"] = "gone" }); // not there: as it is
            var deleted = Assert.Single(await Run(elastic, "case_delete", new() { ["case"] = CaseId })).Fields;
            Assert.Equal((CaseId, true), ((string?)deleted["id"], (bool?)deleted["deleted"]));
            Assert.Equal(new[]
            {
                ("GET", $"/api/cases/{CaseId}", (string?)null),
                ("PATCH", "/api/cases", $"{{\"cases\":[{{\"id\":\"{CaseId}\",\"version\":\"WzIsMV0=\",\"title\":\"Kafé phishing wave, contained\",\"status\":\"in-progress\",\"tags\":[\"phishing\",\"contained\"]}}]}}"),
                ("GET", $"/api/cases/{CaseId}", null),
                ("PATCH", "/api/cases", $"{{\"cases\":[{{\"id\":\"{CaseId}\",\"version\":\"WzIsMV0=\",\"tags\":[\"phishing\",\"kafe\",\"contained\"]}}]}}"),
                ("GET", $"/api/cases/{CaseId}", null),
                ("GET", $"/api/cases/{CaseId}", null),
                ("PATCH", "/api/cases", $"{{\"cases\":[{{\"id\":\"{CaseId}\",\"version\":\"WzIsMV0=\",\"tags\":[\"phishing\"]}}]}}"),
                ("GET", $"/api/cases/{CaseId}", null),
                ("DELETE", $"/api/cases?ids=%5B%22{CaseId}%22%5D", null),
            }, elastic.Requests.Select(sent => (sent.Method, sent.Url[Kibana.Length..], sent.Body)));
            Assert.All(elastic.Requests, sent => Assert.Equal("true", sent.Xsrf));
        }

        [Fact]
        public async Task A_cases_comments_are_listed_added_changed_and_deleted()
        {
            var elastic = new Elastic(sent => sent.Method switch
            {
                "POST" or "PATCH" => (HttpStatusCode.OK, Case()),
                "DELETE" => (HttpStatusCode.NoContent, string.Empty),
                _ when sent.Url.Contains("_find", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"page\":1,\"per_page\":50,\"total\":1,\"comments\":[" + Comment + "]}"),
                _ => (HttpStatusCode.OK, Comment),
            });
            Assert.Equal("Seen in the till logs", (string?)Assert.Single(await Run(elastic, "case_comments", new() { ["case"] = CaseId })).Fields["comment"]);
            var added = Assert.Single(await Run(elastic, "case_comment_add", new() { ["case"] = CaseId, ["query"] = "Seen in the till logs\nat 08:00", ["owner"] = "observability" })).Fields;
            Assert.Equal((CommentId, "user"), ((string?)added["id"], (string?)added["type"])); // the comment made, not the case
            await Run(elastic, "case_comment_update", new() { ["case"] = CaseId, ["comment_id"] = CommentId, ["comment"] = "Seen twice" });
            await Run(elastic, "case_comment", new() { ["case"] = CaseId, ["comment_id"] = CommentId });
            var deleted = Assert.Single(await Run(elastic, "case_comment_delete", new() { ["case"] = CaseId, ["comment_id"] = CommentId })).Fields;
            Assert.Equal((CommentId, CaseId, true), ((string?)deleted["id"], (string?)deleted["case"], (bool?)deleted["deleted"]));
            Assert.Equal(new[]
            {
                ("GET", $"/api/cases/{CaseId}/comments/_find?page=1&perPage=50", (string?)null),
                ("POST", $"/api/cases/{CaseId}/comments", "{\"type\":\"user\",\"comment\":\"Seen in the till logs\\nat 08:00\",\"owner\":\"observability\"}"),
                ("GET", $"/api/cases/{CaseId}/comments/{CommentId}", null),
                ("PATCH", $"/api/cases/{CaseId}/comments", $"{{\"type\":\"user\",\"comment\":\"Seen twice\",\"id\":\"{CommentId}\",\"version\":\"WzgsMV0=\",\"owner\":\"securitySolution\"}}"),
                ("GET", $"/api/cases/{CaseId}/comments/{CommentId}", null),
                ("GET", $"/api/cases/{CaseId}/comments/{CommentId}", null),
                ("DELETE", $"/api/cases/{CaseId}/comments/{CommentId}", null),
            }, elastic.Requests.Select(sent => (sent.Method, sent.Url[Kibana.Length..], sent.Body)));
        }

        [Fact]
        public async Task A_failure_is_kibanas_message()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "case", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Elastic(_ => (status, body)), operation, parameters ?? new() { ["case"] = CaseId }))).Message;

            Assert.Equal("Elastic Security answered HTTP 401: [security_exception Root causes: security_exception: unable to authenticate user [elastic]. - check the user name and password.",
                await Failed(HttpStatusCode.Unauthorized, "{\"statusCode\":401,\"error\":\"Unauthorized\",\"message\":\"[security_exception\\n\\tRoot causes:\\n\\t\\tsecurity_exception: unable to authenticate user [elastic]\"}"));
            Assert.Equal("Elastic Security answered HTTP 403: Unauthorized to create case with owners: \"kafe\". - check the user's Kibana privileges for cases.", await Failed(HttpStatusCode.Forbidden, "{\"statusCode\":403,\"error\":\"Forbidden\",\"message\":\"Unauthorized to create case with owners: \\\"kafe\\\"\"}"));
            Assert.Equal("Elastic Security answered HTTP 404: Saved object [cases/" + CaseId + "] not found.", await Failed(HttpStatusCode.NotFound, "{\"statusCode\":404,\"error\":\"Not Found\",\"message\":\"Saved object [cases/" + CaseId + "] not found\"}"));
            Assert.Equal("Elastic Security answered HTTP 409: These cases " + CaseId + " have been updated. Please refresh before saving additional updates. - another change landed first; run the step again.",
                await Failed(HttpStatusCode.Conflict, "{\"statusCode\":409,\"error\":\"Conflict\",\"message\":\"These cases " + CaseId + " have been updated. Please refresh before saving additional updates.\"}"));
            Assert.Equal("Elastic Security answered HTTP 500.", await Failed(HttpStatusCode.InternalServerError, "<html>fel</html>"));
            Assert.Equal("Kibana answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Log in</body></html>"));
            Assert.Equal("Kibana answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]"));
            Assert.Equal("Kibana answered without its list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{}", "cases", new()));
            Assert.Equal("Kibana answered with what is not the case changed - check the address: a sign-in page or a proxy answers so.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Elastic(sent => (HttpStatusCode.OK, sent.Method == "PATCH" ? "{}" : Case())), "case_update", new() { ["case"] = CaseId, ["status"] = "closed" }))).Message);
        }

        [Fact]
        public async Task A_test_reads_the_open_cases_count()
        {
            var elastic = new Elastic(_ => (HttpStatusCode.OK, Found(1, 1)));
            var tested = await new ElasticSecurityConnector().TestConnectionAsync(Contexts.Test(new StubEgress(elastic), Kibana + "/", new Dictionary<string, string> { ["space"] = "soc" }, Credentials));
            Assert.Equal((true, "Kibana answers - 7 open cases."), (tested.Success, tested.Message));
            Assert.Equal(("GET", Kibana + "/s/soc/api/cases/_find?perPage=1", Basic, "true"), (elastic.Requests.Single().Method, elastic.Requests.Single().Url, elastic.Requests.Single().Authorization, elastic.Requests.Single().Xsrf));
            async Task<ConnectionTestResult> Test(HttpStatusCode status, string body) =>
                await new ElasticSecurityConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Elastic(_ => (status, body))), Kibana, new Dictionary<string, string>(), Credentials));

            var nothing = await Test(HttpStatusCode.OK, "{}");
            Assert.Equal((false, "Kibana answered without its cases - check the address: a sign-in page or a proxy answers so."), (nothing.Success, nothing.Message));
            Assert.False((await Test(HttpStatusCode.OK, "<html>Log in</html>")).Success);
            Assert.False((await Test(HttpStatusCode.Unauthorized, "{\"message\":\"unable to authenticate\"}")).Success);
        }
    }

    /// <summary>
    /// Elastic Security LIVE, against this machine's throwaway Kibana 9.5 over its own Elasticsearch
    /// (scripts/kibana-test-server.ps1) - fail-not-skip without it: a test signing in and refusing a wrong password with
    /// Kibana's words; a case made, found by its tag, changed, tagged and untagged, commented on, its comment changed and removed,
    /// a change at a stale version refused with Kibana's 409, the status counts read, and the case deleted, then gone.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class ElasticSecurityConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["username"] = KibanaTestEndpoint.User, ["password"] = KibanaTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { new Uri(KibanaTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new ElasticSecurityConnector(), Contexts.Extract(Egress, KibanaTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Credentials, timeoutSeconds: 60));

        [Fact]
        public async Task A_test_signs_in_and_refuses_a_wrong_password()
        {
            var tested = await new ElasticSecurityConnector().TestConnectionAsync(Contexts.Test(Egress, KibanaTestEndpoint.Endpoint, credentials: Credentials, timeoutSeconds: 60));
            Assert.True(tested.Success, tested.Message);
            var wrong = await new ElasticSecurityConnector().TestConnectionAsync(Contexts.Test(Egress, KibanaTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["username"] = KibanaTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 60));
            Assert.False(wrong.Success);
            Assert.StartsWith("Elastic Security answered HTTP 401: [security_exception Root causes:", wrong.Message, StringComparison.Ordinal);
            Assert.EndsWith(" - check the user name and password.", wrong.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_case_is_made_found_changed_tagged_commented_and_deleted()
        {
            var marker = "kafe-" + Guid.NewGuid().ToString("N")[..10];
            var made = Assert.Single(await Run("case_create", new() { ["title"] = $"Phishing {marker}", ["query"] = "Mails to the till staff\nfrom kassa.example", ["tags"] = $"{marker}, phishing", ["severity"] = "high", ["category"] = "Phishing" })).Fields;
            var caseId = (string)made["id"]!;
            output.WriteLine($"case {caseId}");
            try
            {
                Assert.Equal(("open", "high", "Mails to the till staff\nfrom kassa.example"), ((string?)made["status"], (string?)made["severity"], (string?)made["description"]));
                Assert.Equal(caseId, (string?)Assert.Single(await Run("cases", new() { ["tags"] = marker, ["status"] = "open" })).Fields["id"]);
                var stale = (string)made["version"]!;
                var changed = Assert.Single(await Run("case_update", new() { ["case"] = caseId, ["status"] = "in-progress", ["title"] = $"Phishing {marker}, contained" })).Fields;
                Assert.Equal(("in-progress", $"Phishing {marker}, contained"), ((string?)changed["status"], (string?)changed["title"]));
                Assert.NotEqual(stale, (string?)changed["version"]);
                Assert.Contains("contained", (string?)Assert.Single(await Run("case_tag_add", new() { ["case"] = caseId, ["tag"] = "contained" })).Fields["tags"], StringComparison.Ordinal);
                Assert.Equal($"[\"{marker}\",\"contained\"]", (string?)Assert.Single(await Run("case_tag_remove", new() { ["case"] = caseId, ["tag"] = "phishing" })).Fields["tags"]);

                var comment = Assert.Single(await Run("case_comment_add", new() { ["case"] = caseId, ["query"] = "Seen in the till logs\nat 08:00" })).Fields;
                var commentId = (string)comment["id"]!;
                Assert.Equal("Seen in the till logs\nat 08:00", (string?)comment["comment"]);
                Assert.Equal(commentId, (string?)Assert.Single(await Run("case_comments", new() { ["case"] = caseId })).Fields["id"]);
                Assert.Equal("Seen twice", (string?)Assert.Single(await Run("case_comment_update", new() { ["case"] = caseId, ["comment_id"] = commentId, ["comment"] = "Seen twice" })).Fields["comment"]);
                Assert.Equal(true, (bool?)Assert.Single(await Run("case_comment_delete", new() { ["case"] = caseId, ["comment_id"] = commentId })).Fields["deleted"]);
                Assert.Empty(await Run("case_comments", new() { ["case"] = caseId }));

                var counts = Assert.Single(await Run("case_status", new())).Fields;
                Assert.True((long?)counts["count_in_progress_cases"] >= 1, string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value)));
            }
            finally
            {
                Assert.Equal(true, (bool?)Assert.Single(await Run("case_delete", new() { ["case"] = caseId })).Fields["deleted"]);
            }

            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("case", new() { ["case"] = caseId }));
            Assert.Equal($"Elastic Security answered HTTP 404: Saved object [cases/{caseId}] not found.", gone.Message);
        }
    }
}
