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
using Stomme.Modules.Connectors.Sentry;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Sentry (n8n's Sentry.io node: its events, issues, organizations, projects, releases and teams): every call
    /// Sentry's web API with the token as a bearer token; a list read page by page by the cursor Sentry's Link header names
    /// - never its address - as long as Sentry says there are results, no more than asked; an issue got, updated and deleted;
    /// organizations, projects, releases and teams read, got, created, updated and deleted; what could go wrong refused
    /// before anything is sent; Sentry's words on a failure; a test naming the token's organizations.
    /// </summary>
    public sealed class SentryConnectorTests
    {
        private const string Api = "https://sentry.io/api/0";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "sntryu_1" };

        private sealed class Sentry : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string, string?)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null, body));
                var (status, answer, link) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (link is not null)
                {
                    response.Headers.TryAddWithoutValidation("Link", link);
                }

                return response;
            }
        }

        private static Task<List<ConnectorRecord>> Run(Sentry sentry, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new SentryConnector(), Contexts.Extract(new StubEgress(sentry), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Issues(int first, int count) =>
            "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (4000 + i) + "\",\"shortId\":\"KAFE-" + i + "\",\"title\":\"Fika failed " + i + "\",\"count\":\"" + i + "\",\"userCount\":" + i + ",\"isBookmarked\":false,\"project\":{\"slug\":\"kassa\"}}")) + "]";

        private static string Links(string next, bool results) =>
            "<https://sentry.io/api/0/projects/kafe/kassa/issues/?cursor=0:0:1>; rel=\"previous\"; results=\"false\"; cursor=\"0:0:1\", "
            + "<https://elsewhere.example.test/api/0/projects/kafe/kassa/issues/?cursor=" + next + ">; rel=\"next\"; results=\"" + (results ? "true" : "false") + "\"; cursor=\"" + next + "\"";

        [Fact]
        public async Task Lists_are_read_page_by_page_by_sentrys_cursor_no_more_than_asked()
        {
            var sentry = new Sentry
            {
                Answer = (_, url) => url.Contains("cursor=0%3A200%3A0", StringComparison.Ordinal) ? (HttpStatusCode.OK, Issues(201, 0), Links("0:300:0", true))
                    : url.Contains("cursor=0%3A100%3A0", StringComparison.Ordinal) ? (HttpStatusCode.OK, Issues(101, 80), Links("0:200:0", true))
                    : (HttpStatusCode.OK, Issues(1, 100), Links("0:100:0", true)),
            };

            var read = await Run(sentry, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["max"] = "120" });
            Assert.Equal(120, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/projects/kafe/kassa/issues/?per_page=100", $"{Api}/projects/kafe/kassa/issues/?per_page=100&cursor=0%3A100%3A0" }, sentry.Requests.Select(request => request.Url)); // the cursor, never the other host
            Assert.All(sentry.Requests, request => Assert.Equal("sntryu_1", request.Token));
            var first = read[0].Fields;
            Assert.Equal(("4001", "KAFE-1", "Fika failed 1", 1L, false, "{\"slug\":\"kassa\"}"), ((string?)first["id"], (string?)first["shortId"], (string?)first["title"], (long?)first["userCount"], (bool?)first["isBookmarked"], (string?)first["project"]));
            sentry.Requests.Clear();
            Assert.Equal(180, (await Run(sentry, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" })).Count); // an empty page the end
            Assert.Equal(3, sentry.Requests.Count);
            sentry.Requests.Clear();

            var endless = new Sentry { Answer = (_, _) => (HttpStatusCode.OK, Issues(1, 100), Links("0:" + Guid.NewGuid().ToString("N") + ":0", true)) };
            Assert.Equal(1000, (await Run(endless, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" })).Count); // 1,000 at most when no max is given
            Assert.Equal(10, endless.Requests.Count);

            var done = new Sentry { Answer = (_, _) => (HttpStatusCode.OK, Issues(1, 3), Links("0:3:0", false)) };
            Assert.Equal(3, (await Run(done, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" })).Count); // results="false" the end
            Assert.Single(done.Requests);
            var unlinked = new Sentry { Answer = (_, _) => (HttpStatusCode.OK, Issues(1, 3), null) };
            Assert.Equal(3, (await Run(unlinked, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" })).Count); // no Link the end
            Assert.Single(unlinked.Requests);

            await Run(sentry, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["query"] = "is:unresolved fika & bröd", ["stats_period"] = "24h", ["short_id_lookup"] = "True", ["max"] = "5" });
            Assert.Equal($"{Api}/projects/kafe/kassa/issues/?query=is%3Aunresolved%20fika%20%26%20br%C3%B6d&statsPeriod=24h&shortIdLookup=1&per_page=5", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.IssuesOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["stats_period"] = "14d", ["short_id_lookup"] = "false", ["max"] = "1" });
            Assert.Equal($"{Api}/projects/kafe/kassa/issues/?statsPeriod=14d&shortIdLookup=0&per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.EventsOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["full"] = "true", ["max"] = "1" });
            Assert.Equal($"{Api}/projects/kafe/kassa/events/?full=true&per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.OrganizationsOperation, new() { ["member"] = "true", ["owner"] = "false", ["max"] = "1" });
            Assert.Equal($"{Api}/organizations/?member=true&owner=false&per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.ProjectsOperation, new() { ["max"] = "1" });
            Assert.Equal($"{Api}/projects/?per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.ReleasesOperation, new() { ["organization"] = "kafe", ["query"] = "kassa@1.", ["max"] = "1" });
            Assert.Equal($"{Api}/organizations/kafe/releases/?query=kassa%401.&per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, SentryConnector.TeamsOperation, new() { ["organization"] = "kafe", ["max"] = "1" });
            Assert.Equal($"{Api}/organizations/kafe/teams/?per_page=1", sentry.Requests[^1].Url);
            await Run(sentry, null!, new() { ["organization"] = "kafe", ["project"] = "kassa", ["max"] = "1" });
            Assert.Equal($"{Api}/projects/kafe/kassa/issues/?per_page=1", sentry.Requests[^1].Url); // no operation: the issues
        }

        [Fact]
        public async Task Issues_events_organizations_projects_releases_and_teams_are_kept()
        {
            var sentry = new Sentry
            {
                Answer = (method, url) => method.Method == "DELETE" ? (HttpStatusCode.Accepted, "", null)
                    : url.Contains("/events/", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"eventID\":\"9fac2ceed9344f2bbfdd1fdacb0ed9b1\",\"title\":\"Fika failed\",\"tags\":[{\"key\":\"level\",\"value\":\"error\"}]}", null)
                    : url.Contains("/releases/", StringComparison.Ordinal) ? (method.Method == "POST" ? HttpStatusCode.Created : HttpStatusCode.OK, "{\"version\":\"kassa@1.0.0+fika\",\"url\":\"https://git.kafe.example.test/kassa\"}", null)
                    : (HttpStatusCode.OK, "{\"id\":\"4001\",\"slug\":\"kassa\",\"name\":\"Kassa\",\"status\":\"resolved\"}", null),
            };
            (string, string, string?) Last() => (sentry.Requests[^1].Method.Method, sentry.Requests[^1].Url, sentry.Requests[^1].Body);
            const string Kafe = "https://sentry.kafe.example.test";

            var gotten = Assert.Single(await Run(sentry, SentryConnector.EventOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["event"] = "9fac2ceed9344f2bbfdd1fdacb0ed9b1" }, endpoint: Kafe + "/api/0/")).Fields;
            Assert.Equal(("GET", $"{Kafe}/api/0/projects/kafe/kassa/events/9fac2ceed9344f2bbfdd1fdacb0ed9b1/", (string?)null), Last()); // a self-hosted server, its API's path given once
            Assert.Equal(("Fika failed", "[{\"key\":\"level\",\"value\":\"error\"}]"), ((string?)gotten["title"], (string?)gotten["tags"]));

            Assert.Equal("4001", (string?)Assert.Single(await Run(sentry, SentryConnector.IssueOperation, new() { ["issue"] = "4001" }, endpoint: Kafe)).Fields["id"]);
            Assert.Equal(("GET", $"{Kafe}/api/0/issues/4001/", (string?)null), Last());
            await Run(sentry, SentryConnector.UpdateIssueOperation, new() { ["issue"] = "4001", ["status"] = "ResolvedInNextRelease", ["assigned_to"] = "ada@kafe.example.test", ["has_seen"] = "true", ["is_bookmarked"] = "False", ["is_subscribed"] = "true", ["is_public"] = "false" });
            Assert.Equal(("PUT", $"{Api}/issues/4001/", (string?)"{\"status\":\"resolvedInNextRelease\",\"assignedTo\":\"ada@kafe.example.test\",\"hasSeen\":true,\"isBookmarked\":false,\"isSubscribed\":true,\"isPublic\":false}"), Last());
            await Run(sentry, SentryConnector.UpdateIssueOperation, new() { ["issue"] = "4001", ["assigned_to"] = "" });
            Assert.Equal("{\"assignedTo\":\"\"}", Last().Item3); // unassigned
            var deleted = Assert.Single(await Run(sentry, SentryConnector.DeleteIssueOperation, new() { ["issue"] = "4001" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/issues/4001/", (string?)null), Last());
            Assert.Equal(("4001", true), ((string?)deleted["id"], (bool?)deleted["deleted"]));

            await Run(sentry, SentryConnector.OrganizationOperation, new() { ["organization"] = "kafe" });
            Assert.Equal(("GET", $"{Api}/organizations/kafe/", (string?)null), Last());
            await Run(sentry, SentryConnector.UpdateOrganizationOperation, new() { ["organization"] = "kafe", ["name"] = "Kafé & bröd", ["slug"] = "kafe-brod" });
            Assert.Equal(("PUT", $"{Api}/organizations/kafe/", (string?)"{\"name\":\"Kafé & bröd\",\"slug\":\"kafe-brod\"}"), Last());

            await Run(sentry, SentryConnector.ProjectOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" });
            Assert.Equal(("GET", $"{Api}/projects/kafe/kassa/", (string?)null), Last());
            Assert.Equal("kassa", (string?)Assert.Single(await Run(sentry, SentryConnector.CreateProjectOperation, new() { ["organization"] = "kafe", ["team"] = "fika", ["name"] = "Kassa", ["slug"] = "kassa", ["platform"] = "javascript-react" })).Fields["slug"]);
            Assert.Equal(("POST", $"{Api}/teams/kafe/fika/projects/", (string?)"{\"name\":\"Kassa\",\"slug\":\"kassa\",\"platform\":\"javascript-react\"}"), Last());
            await Run(sentry, SentryConnector.UpdateProjectOperation, new() { ["organization"] = "kafe", ["project"] = "kassa", ["name"] = "Kassan", ["slug"] = "kassan", ["platform"] = "python", ["is_bookmarked"] = "true", ["digests_min_delay"] = "60", ["digests_max_delay"] = "1800" });
            Assert.Equal(("PUT", $"{Api}/projects/kafe/kassa/", (string?)"{\"name\":\"Kassan\",\"slug\":\"kassan\",\"platform\":\"python\",\"isBookmarked\":true,\"digestsMinDelay\":60,\"digestsMaxDelay\":1800}"), Last());
            var gone = Assert.Single(await Run(sentry, SentryConnector.DeleteProjectOperation, new() { ["organization"] = "kafe", ["project"] = "kassa" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/projects/kafe/kassa/", (string?)null), Last());
            Assert.Equal(("kafe", "kassa", true), ((string?)gone["organization"], (string?)gone["slug"], (bool?)gone["deleted"]));

            Assert.Equal("kassa@1.0.0+fika", (string?)Assert.Single(await Run(sentry, SentryConnector.ReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1.0.0+fika" })).Fields["version"]);
            Assert.Equal(("GET", $"{Api}/organizations/kafe/releases/kassa%401.0.0%2Bfika/", (string?)null), Last()); // the version escaped
            await Run(sentry, SentryConnector.CreateReleaseOperation, new()
            {
                ["organization"] = "kafe", ["version"] = "kassa@1.0.0+fika", ["projects"] = "kassa, kok, kassa", ["url"] = "https://git.kafe.example.test/kassa", ["date_released"] = "2026-10-13T12:00:00+02:00",
                ["commits"] = "[{\"id\": \"a1b2c3\", \"repository\": \"kafe/kassa\", \"message\": \"Fika för alla\"}]", ["refs"] = "[{\"repository\": \"kafe/kassa\", \"commit\": \"a1b2c3\", \"previousCommit\": \"9f8e7d\"}]",
            });
            Assert.Equal(("POST", $"{Api}/organizations/kafe/releases/", (string?)"{\"version\":\"kassa@1.0.0+fika\",\"projects\":[\"kassa\",\"kok\"],\"url\":\"https://git.kafe.example.test/kassa\",\"dateReleased\":\"2026-10-13T10:00:00Z\",\"commits\":[{\"id\":\"a1b2c3\",\"repository\":\"kafe/kassa\",\"message\":\"Fika för alla\"}],\"refs\":[{\"repository\":\"kafe/kassa\",\"commit\":\"a1b2c3\",\"previousCommit\":\"9f8e7d\"}]}"), Last());
            await Run(sentry, SentryConnector.CreateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1.0.1", ["projects"] = "kassa" });
            Assert.Equal("{\"version\":\"kassa@1.0.1\",\"projects\":[\"kassa\"]}", Last().Item3);
            await Run(sentry, SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1.0.1", ["ref"] = "v1.0.1", ["date_released"] = "2026-10-13T10:00:00Z" });
            Assert.Equal(("PUT", $"{Api}/organizations/kafe/releases/kassa%401.0.1/", (string?)"{\"dateReleased\":\"2026-10-13T10:00:00Z\",\"ref\":\"v1.0.1\"}"), Last());
            var dropped = Assert.Single(await Run(sentry, SentryConnector.DeleteReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1.0.1" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/organizations/kafe/releases/kassa%401.0.1/", (string?)null), Last());
            Assert.Equal(("kassa@1.0.1", true), ((string?)dropped["version"], (bool?)dropped["deleted"]));

            await Run(sentry, SentryConnector.TeamOperation, new() { ["organization"] = "kafe", ["team"] = "fika" });
            Assert.Equal(("GET", $"{Api}/teams/kafe/fika/", (string?)null), Last());
            await Run(sentry, SentryConnector.CreateTeamOperation, new() { ["organization"] = "kafe", ["name"] = "Fika & bröd" });
            Assert.Equal(("POST", $"{Api}/organizations/kafe/teams/", (string?)"{\"name\":\"Fika & bröd\"}"), Last());
            await Run(sentry, SentryConnector.UpdateTeamOperation, new() { ["organization"] = "kafe", ["team"] = "fika", ["slug"] = "fika-2" });
            Assert.Equal(("PUT", $"{Api}/teams/kafe/fika/", (string?)"{\"slug\":\"fika-2\"}"), Last());
            var disbanded = Assert.Single(await Run(sentry, SentryConnector.DeleteTeamOperation, new() { ["organization"] = "kafe", ["team"] = "fika" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/teams/kafe/fika/", (string?)null), Last());
            Assert.Equal(("fika", true), ((string?)disbanded["slug"], (bool?)disbanded["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_sentrys_words_on_a_failure()
        {
            var sentry = new Sentry();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(sentry, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> Kassa(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["organization"] = "kafe", ["project"] = "kassa" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("Sentry has no operation 'alerts' - events, event, issues, issue,", await Refused("alerts", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(SentryConnector.ProjectsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the organization's slug.", await Refused(SentryConnector.TeamsOperation, new()));
            Assert.Equal("'kafe/x' is not a slug (organization).", await Refused(SentryConnector.TeamsOperation, new() { ["organization"] = "kafe/x" }));
            Assert.Equal("Give the project's slug.", await Refused(SentryConnector.EventsOperation, new() { ["organization"] = "kafe" }));
            Assert.Equal("'-kassa' is not a slug (project).", await Refused(SentryConnector.ProjectOperation, Kassa(("project", "-kassa"))));
            Assert.Equal("Give the team's slug.", await Refused(SentryConnector.CreateProjectOperation, new() { ["organization"] = "kafe", ["name"] = "Kassa" }));
            Assert.Equal("'fika lag' is not a slug (team).", await Refused(SentryConnector.DeleteTeamOperation, new() { ["organization"] = "kafe", ["team"] = "fika lag" }));
            Assert.Equal("Give the issue's id.", await Refused(SentryConnector.DeleteIssueOperation, new()));
            Assert.Equal("'KAFE-1' is not an issue's id - 1234 (issue).", await Refused(SentryConnector.IssueOperation, new() { ["issue"] = "KAFE-1" }));
            Assert.Equal("Give the event's id.", await Refused(SentryConnector.EventOperation, Kassa()));
            Assert.Equal("'../9fac' is not an event's id - its 32 hexadecimal digits (event).", await Refused(SentryConnector.EventOperation, Kassa(("event", "../9fac"))));
            Assert.Equal("Give the release's version - my-app@1.0.0.", await Refused(SentryConnector.ReleaseOperation, new() { ["organization"] = "kafe" }));
            foreach (var version in new[] { "kassa/1.0", "kassa 1.0", "latest", "..", new string('v', 201) })
            {
                Assert.Equal($"'{version}' is not a release's version - no slash, space or line, not '.', '..' or 'latest' (version).", await Refused(SentryConnector.DeleteReleaseOperation, new() { ["organization"] = "kafe", ["version"] = version }));
            }

            Assert.Equal("'24 h' is not 24h or 14d (stats_period).", await Refused(SentryConnector.IssuesOperation, Kassa(("stats_period", "24 h"))));
            Assert.Equal("'yes' is not true or false (full).", await Refused(SentryConnector.EventsOperation, Kassa(("full", "yes"))));
            Assert.Equal("'closed' is not resolved, resolvedInNextRelease, unresolved or ignored (status).", await Refused(SentryConnector.UpdateIssueOperation, new() { ["issue"] = "4001", ["status"] = "closed" }));
            Assert.Equal("Give what to change - its status, assignee or a flag.", await Refused(SentryConnector.UpdateIssueOperation, new() { ["issue"] = "4001" }));
            Assert.Equal("Give what to change - its name or slug.", await Refused(SentryConnector.UpdateTeamOperation, new() { ["organization"] = "kafe", ["team"] = "fika" }));
            Assert.Equal("'Kafé brod' is not a slug (slug).", await Refused(SentryConnector.UpdateOrganizationOperation, new() { ["organization"] = "kafe", ["slug"] = "Kafé brod" }));
            Assert.Equal("Give its name.", await Refused(SentryConnector.CreateTeamOperation, new() { ["organization"] = "kafe", ["slug"] = "fika" }));
            Assert.Equal("'React' is not a platform - javascript-react (platform).", await Refused(SentryConnector.CreateProjectOperation, new() { ["organization"] = "kafe", ["team"] = "fika", ["name"] = "Kassa", ["platform"] = "React" }));
            Assert.Equal("'0' is not a number of seconds above 0 (digests_min_delay).", await Refused(SentryConnector.UpdateProjectOperation, Kassa(("digests_min_delay", "0"))));
            Assert.Equal("Give what to change - its name, slug, platform, bookmark or digests' delays.", await Refused(SentryConnector.UpdateProjectOperation, Kassa()));
            Assert.Equal("Give its projects - comma-separated slugs.", await Refused(SentryConnector.CreateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1" }));
            Assert.Equal("'kok/x' is not a slug (projects).", await Refused(SentryConnector.CreateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["projects"] = "kassa,kok/x" }));
            Assert.Equal("'ftp://git.kafe.example.test' is not an http(s) address (url).", await Refused(SentryConnector.CreateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["projects"] = "kassa", ["url"] = "ftp://git.kafe.example.test" }));
            Assert.Equal("'2026-10-13' is not a time - 2026-10-13T10:00:00Z (date_released).", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["date_released"] = "2026-10-13" }));
            Assert.Equal("'yesterdayT' is not a time - 2026-10-13T10:00:00Z (date_released).", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["date_released"] = "yesterdayT" }));
            Assert.Equal("The commits are not a JSON list of objects - [{\"id\": \"...\"}].", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["commits"] = "{\"id\": \"a1\"}" }));
            Assert.Equal("The commits are not a JSON list of objects - [{\"id\": \"...\"}].", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["commits"] = "[\"a1\"]" }));
            Assert.Equal("The refs are not a JSON list of objects - [{\"repository\": \"...\"}].", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["refs"] = "a1" }));
            Assert.Equal("Each of the commits needs its id.", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["commits"] = "[{\"message\": \"Fix\"}]" }));
            Assert.Equal("Each of the refs needs its commit.", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1", ["refs"] = "[{\"repository\": \"kafe/kassa\"}]" }));
            Assert.Equal("Give what to change - its address, date, ref, commits or refs.", await Refused(SentryConnector.UpdateReleaseOperation, new() { ["organization"] = "kafe", ["version"] = "kassa@1" }));
            Assert.Equal("The endpoint 'sentry.kafe.example.test' is not an absolute http(s) URL.", await Refused(SentryConnector.ProjectsOperation, new(), endpoint: "sentry.kafe.example.test"));
            Assert.Equal("The Sentry connection has no auth token.", await Refused(SentryConnector.ProjectsOperation, new(), credentials: new()));
            Assert.Empty(sentry.Requests); // nothing sent
            Assert.Single(await Run(sentry, SentryConnector.DeleteReleaseOperation, new() { ["organization"] = "kafe", ["version"] = new string('v', 200) })); // 200 is a version

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Sentry { Answer = (_, _) => (status, answer, null) }, SentryConnector.CreateTeamOperation, new() { ["organization"] = "kafe", ["name"] = "Fika" }))).Message;
            Assert.Equal("HTTP 403: You do not have permission to perform this action.", await Failed(HttpStatusCode.Forbidden, "{\"detail\":\"You do not have permission to perform this action.\"}"));
            Assert.Equal("HTTP 400: slug: The slug \"fika\" is already in use.; name: This field may not be blank.", await Failed(HttpStatusCode.BadRequest, "{\"slug\":[\"The slug \\\"fika\\\" is already in use.\"],\"name\":[\"This field may not be blank.\"]}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"detail\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Sentry answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_tokens_organizations()
        {
            Task<ConnectionTestResult> Test(Sentry sentry) =>
                new SentryConnector().TestConnectionAsync(Contexts.Test(new StubEgress(sentry), "", null, Credentials));
            var organizations = new Sentry { Answer = (_, _) => (HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(1, 7).Select(i => "{\"slug\":\"kafe-" + i + "\"}")) + "]", null) };
            var tested = await Test(organizations);
            Assert.Equal((true, "Sentry: the token is in kafe-1, kafe-2, kafe-3, kafe-4, kafe-5 and 2 more."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/organizations/?member=true", "GET", "sntryu_1"), (organizations.Requests[0].Url, organizations.Requests[0].Method.Method, organizations.Requests[0].Token));
            Assert.Equal("Sentry: the token is in kafe-1.", (await Test(new Sentry { Answer = (_, _) => (HttpStatusCode.OK, "[{\"slug\":\"kafe-1\"}]", null) })).Message);
            Assert.Equal("Sentry: the token is in no organization.", (await Test(new Sentry { Answer = (_, _) => (HttpStatusCode.OK, "[]", null) })).Message);
            var refused = await Test(new Sentry { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"detail\":\"Invalid token\"}", null) });
            Assert.Equal((false, "HTTP 401: Invalid token"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "sentry"), (new SentryConnector().AuthType, new SentryConnector().ConnectorType));
            Assert.Equal(24, new SentryConnector().Operations.Count);
            Assert.Equal(("token", "Auth token (User Settings > Personal Tokens)"), (Assert.Single(new SentryConnector().CredentialLabels!).Key, Assert.Single(new SentryConnector().CredentialLabels!).Value)); // in Sentry's words
        }
    }
}
