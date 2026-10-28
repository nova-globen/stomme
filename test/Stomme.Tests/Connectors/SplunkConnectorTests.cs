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
using Stomme.Modules.Connectors.Splunk;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Splunk (n8n's Splunk node: its searches, reports, fired alerts and users): every call the server's REST API under
    /// /services with output_mode=json and the token as a bearer token; lists read page by page by count and offset, the size
    /// fixed and no bigger than asked, a total, a short page or an empty one the end; a search run blocking with its times and
    /// settings and read back, its results matched by a quoted value, a job not finished a failure that says so; reports made
    /// from a job or a search, scheduled or not; alerts; users read, updated with roles and deleted - never created, never a
    /// password; what could go wrong refused before anything is sent; Splunk's messages on a failure; a test naming whose the
    /// token is.
    /// </summary>
    public sealed class SplunkConnectorTests
    {
        private const string Server = "https://splunk.kafe.example.test:8089";

        private const string Api = Server + "/services";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "eyJraWQiOiJzcGx1bmsi" };

        private sealed class Splunk : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"entry\":[]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Splunk splunk, string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new SplunkConnector(), Contexts.Extract(new StubEgress(splunk), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Entries(string kind, int first, int count, int total) =>
            "{\"entry\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"name\":\"" + kind + " " + i + "\",\"id\":\"https://splunk.kafe.example.test:8089/services/" + kind + "/" + kind + "%20" + i + "\",\"author\":\"admin\",\"links\":{\"alternate\":\"/x\"},\"acl\":{\"app\":\"search\"},\"content\":{\"disabled\":false,\"count\":" + i + ",\"roles\":[\"user\"]}}")) + "],\"paging\":{\"total\":" + total + ",\"perPage\":100,\"offset\":" + (first - 1) + "}}";

        private const string Job = "{\"entry\":[{\"name\":\"search index=main error\",\"id\":\"https://splunk.kafe.example.test:8089/services/search/jobs/1697123456.42\",\"content\":{\"sid\":\"1697123456.42\",\"dispatchState\":\"DONE\",\"isDone\":true,\"resultCount\":3,\"request\":{\"search\":\"search index=main error | head 50\",\"earliest_time\":\"-24h@h\",\"latest_time\":\"now\"}}}]}";

        [Fact]
        public async Task Lists_are_read_page_by_page_by_count_and_offset_no_more_than_asked()
        {
            var splunk = new Splunk
            {
                Answer = (_, url) => url.Contains("offset=200", StringComparison.Ordinal) ? (HttpStatusCode.OK, Entries("saved/searches", 201, 30, 230))
                    : url.Contains("offset=100", StringComparison.Ordinal) ? (HttpStatusCode.OK, Entries("saved/searches", 101, 100, 230))
                    : (HttpStatusCode.OK, Entries("saved/searches", 1, 100, 230)),
            };

            var read = await Run(splunk, SplunkConnector.ReportsOperation, new() { ["max"] = "150" });
            Assert.Equal(150, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/saved/searches?output_mode=json&count=100&offset=0", $"{Api}/saved/searches?output_mode=json&count=100&offset=100" }, splunk.Requests.Select(request => request.Url));
            Assert.All(splunk.Requests, request => Assert.Equal(("GET", (string?)"Bearer eyJraWQiOiJzcGx1bmsi", (string?)null), (request.Method.Method, request.Authorization, request.Body)));
            var first = read[0].Fields;
            Assert.Equal(("saved/searches 1", "searches 1", "https://splunk.kafe.example.test:8089/services/saved/searches/saved/searches%201", 1L, "[\"user\"]", "{\"app\":\"search\"}"),
                ((string?)first["name"], (string?)first["id"], (string?)first["entryUrl"], (long?)first["count"], (string?)first["roles"], (string?)first["acl"])); // its id the last part of its address, its content beside
            Assert.False(first.ContainsKey("links") || first.ContainsKey("content"));
            splunk.Requests.Clear();
            Assert.Equal(230, (await Run(splunk, SplunkConnector.ReportsOperation, new())).Count); // its total the end
            Assert.Equal(3, splunk.Requests.Count);
            var exact = new Splunk { Answer = (_, _) => (HttpStatusCode.OK, Entries("x", 1, 100, 200)) };
            Assert.Equal(200, (await Run(exact, SplunkConnector.UsersOperation, new())).Count); // full pages: its total the end
            Assert.Equal(2, exact.Requests.Count);
            var shortPage = new Splunk { Answer = (_, _) => (HttpStatusCode.OK, "{\"entry\":[{\"name\":\"a\"},{\"name\":\"b\"}]}") };
            Assert.Equal(2, (await Run(shortPage, SplunkConnector.UsersOperation, new())).Count); // no total: a short page the end
            Assert.Single(shortPage.Requests);
            var empty = new Splunk { Answer = (_, _) => (HttpStatusCode.OK, "{\"entry\":[],\"paging\":{\"total\":9}}") };
            Assert.Empty(await Run(empty, SplunkConnector.FiredAlertsOperation, new()));
            Assert.Single(empty.Requests);
            var endless = new Splunk { Answer = (_, url) => (HttpStatusCode.OK, Entries("x", 1, 100, 100000)) };
            Assert.Equal(1000, (await Run(endless, SplunkConnector.SearchJobsOperation, new())).Count); // 1,000 at most when no max is given

            await Run(splunk, null!, new() { ["sort_key"] = "diskUsage", ["sort_dir"] = "DESC", ["sort_mode"] = "num", ["max"] = "5" });
            Assert.Equal($"{Api}/search/jobs?output_mode=json&sort_key=diskUsage&sort_dir=desc&sort_mode=num&count=5&offset=0", splunk.Requests[^1].Url); // no operation: the search jobs
            await Run(splunk, SplunkConnector.ReportsOperation, new() { ["add_orphan_field"] = "True", ["list_default_action_args"] = "false", ["max"] = "5" });
            Assert.Equal($"{Api}/saved/searches?output_mode=json&add_orphan_field=true&listDefaultActionArgs=false&count=5&offset=0", splunk.Requests[^1].Url);
            await Run(splunk, SplunkConnector.FiredAlertsOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/alerts/fired_alerts?output_mode=json&count=5&offset=0", splunk.Requests[^1].Url);
            await Run(splunk, SplunkConnector.MetricAlertsOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/alerts/metric_alerts?output_mode=json&count=5&offset=0", splunk.Requests[^1].Url);
            await Run(splunk, SplunkConnector.UsersOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/authentication/users?output_mode=json&count=5&offset=0", splunk.Requests[^1].Url);
        }

        [Fact]
        public async Task A_search_runs_and_its_results_are_read_matched_by_a_quoted_value()
        {
            var splunk = new Splunk
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("POST", _) => (HttpStatusCode.Created, "{\"sid\":\"1697123456.42\"}"),
                    ("GET", var u) when u.Contains("/results?", StringComparison.Ordinal) && u.Contains("offset=0", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"preview\":false,\"results\":[{\"host\":\"web-01\",\"count\":\"3\",\"tags\":[\"a\",\"b\"]},{\"host\":\"web-02\",\"count\":\"1\"}]}"),
                    ("GET", var u) when u.Contains("/results?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"results\":[]}"),
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"messages\":[{\"type\":\"INFO\",\"text\":\"Search job cancelled.\"}]}"),
                    _ => (HttpStatusCode.OK, Job),
                },
            };
            (string, string, string?) Last() => (splunk.Requests[^1].Method.Method, splunk.Requests[^1].Url, splunk.Requests[^1].Body);

            var job = Assert.Single(await Run(splunk, SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main error | stats count by host", ["earliest_time"] = "-24h@h", ["latest_time"] = "2026-10-13T08:00:00+02:00", ["index_earliest"] = "1760335200", ["adhoc_search_level"] = "Smart", ["max_time"] = "60", ["namespace"] = "search", ["rf"] = "host,count" })).Fields;
            Assert.Equal(("POST", $"{Api}/search/jobs?output_mode=json", (string?)"search=search+index%3Dmain+error+%7C+stats+count+by+host&exec_mode=blocking&earliest_time=-24h%40h&latest_time=1791871200&index_earliest=1760335200&adhoc_search_level=smart&max_time=60&namespace=search&rf=host%2Ccount"), (splunk.Requests[0].Method.Method, splunk.Requests[0].Url, splunk.Requests[0].Body)); // search put before it, a moment as seconds
            Assert.Equal(("GET", $"{Api}/search/jobs/1697123456.42?output_mode=json", (string?)null), Last()); // read back
            Assert.Equal(("1697123456.42", "DONE", 3L), ((string?)job["id"], (string?)job["dispatchState"], (long?)job["resultCount"]));
            await Run(splunk, SplunkConnector.CreateSearchOperation, new() { ["search"] = "| tstats count where index=main", ["exec_mode"] = "normal" });
            Assert.Equal("search=%7C+tstats+count+where+index%3Dmain&exec_mode=normal", splunk.Requests[^2].Body); // a pipe kept as it is
            await Run(splunk, SplunkConnector.CreateSearchOperation, new() { ["search"] = "Search index=main" });
            Assert.Equal("search=Search+index%3Dmain&exec_mode=blocking", splunk.Requests[^2].Body); // search kept as it is

            Assert.Equal("1697123456.42", (string?)Assert.Single(await Run(splunk, SplunkConnector.SearchJobOperation, new() { ["job"] = "1697123456.42" })).Fields["sid"]);
            Assert.Equal(("GET", $"{Api}/search/jobs/1697123456.42?output_mode=json", (string?)null), Last());
            var results = await Run(splunk, SplunkConnector.SearchResultsOperation, new() { ["job"] = "scheduler__admin__search__RMD5_at_1697_1", ["field"] = "host", ["value"] = "web \"01\"", ["summary"] = "true", ["max"] = "2" });
            Assert.Equal(("GET", $"{Api}/search/jobs/scheduler__admin__search__RMD5_at_1697_1/results?output_mode=json&search=search%20host%3D%22web%20%5C%2201%5C%22%22&add_summary_to_metadata=true&count=2&offset=0", (string?)null), Last()); // quoted, never spliced
            Assert.Equal(("web-01", "3", "[\"a\",\"b\"]"), ((string?)results[0].Fields["host"], (string?)results[0].Fields["count"], (string?)results[0].Fields["tags"]));
            Assert.Equal(2, results.Count);
            Assert.Equal(2, (await Run(splunk, SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697123456.42" })).Count); // a short page the end
            var deleted = Assert.Single(await Run(splunk, SplunkConnector.DeleteSearchJobOperation, new() { ["job"] = "1697123456.42" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/search/jobs/1697123456.42?output_mode=json", (string?)null), Last());
            Assert.Equal(("1697123456.42", true), ((string?)deleted["job"], (bool?)deleted["deleted"]));

            var running = new Splunk { Answer = (_, _) => (HttpStatusCode.NoContent, "") };
            Assert.Equal("Splunk has no results yet - the search job has not finished: run it blocking (exec_mode), or read it again later.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(running, SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697123456.42" }))).Message);
            var unnamed = new Splunk { Answer = (_, _) => (HttpStatusCode.Created, "{\"messages\":[]}") };
            Assert.Equal("Splunk made no search job - it named none.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unnamed, SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main" }))).Message);
            Assert.Single(unnamed.Requests); // nothing read back
            var stray = new Splunk { Answer = (_, _) => (HttpStatusCode.Created, "{\"sid\":\"1697/../../authentication/users\"}") };
            Assert.Equal("Splunk made no search job - it named none.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stray, SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main" }))).Message);
            Assert.Single(stray.Requests); // an id not of a job's shape never put in an address
        }

        [Fact]
        public async Task Reports_and_users_are_kept()
        {
            var splunk = new Splunk
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/search/jobs/", StringComparison.Ordinal) => (HttpStatusCode.OK, Job),
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"entry\":[]}"),
                    (_, var u) when u.Contains("/authentication/users", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"entry\":[{\"name\":\"ada\",\"id\":\"https://splunk.kafe.example.test:8089/services/authentication/users/ada\",\"content\":{\"realname\":\"Ada Lovelace\",\"roles\":[\"power\",\"user\"]}}]}"),
                    _ => (HttpStatusCode.Created, "{\"entry\":[{\"name\":\"Errors at the till\",\"id\":\"https://splunk.kafe.example.test:8089/servicesNS/nobody/search/saved/searches/Errors%20at%20the%20till\",\"content\":{\"search\":\"search index=main error\",\"is_scheduled\":true}}]}"),
                },
            };
            (string, string, string?) Last() => (splunk.Requests[^1].Method.Method, splunk.Requests[^1].Url, splunk.Requests[^1].Body);

            var report = Assert.Single(await Run(splunk, SplunkConnector.ReportOperation, new() { ["report"] = "Errors at the till" })).Fields;
            Assert.Equal(("GET", $"{Api}/saved/searches/Errors%20at%20the%20till?output_mode=json", (string?)null), Last()); // escaped
            Assert.Equal(("Errors at the till", true), ((string?)report["id"], (bool?)report["is_scheduled"]));
            await Run(splunk, SplunkConnector.ReportOperation, new() { ["report"] = "Kassa/fel #2?" });
            Assert.Equal($"{Api}/saved/searches/Kassa%2Ffel%20%232%3F?output_mode=json", Last().Item2); // escaped: a name cannot leave its path
            await Run(splunk, SplunkConnector.CreateReportOperation, new() { ["name"] = "Fika & bröd", ["job"] = "1697123456.42", ["latest_time"] = "-1h", ["cron_schedule"] = "0 6 * * 1" });
            Assert.Equal(("GET", $"{Api}/search/jobs/1697123456.42?output_mode=json", (string?)null), (splunk.Requests[^2].Method.Method, splunk.Requests[^2].Url, splunk.Requests[^2].Body)); // the job read first
            Assert.Equal(("POST", $"{Api}/saved/searches?output_mode=json", (string?)"name=Fika+%26+br%C3%B6d&search=search+index%3Dmain+error+%7C+head+50&dispatch.latest_time=-1h&cron_schedule=0+6+%2A+%2A+1&is_scheduled=1&dispatch.earliest_time=-24h%40h"), Last()); // its search and times, the time given winning
            await Run(splunk, SplunkConnector.CreateReportOperation, new() { ["name"] = "Errors", ["search"] = "search index=main error", ["description"] = "Kafé", ["earliest_time"] = "-7d@d" });
            Assert.Equal(("POST", $"{Api}/saved/searches?output_mode=json", (string?)"name=Errors&search=search+index%3Dmain+error&description=Kaf%C3%A9&dispatch.earliest_time=-7d%40d"), Last());
            Assert.True((bool?)Assert.Single(await Run(splunk, SplunkConnector.DeleteReportOperation, new() { ["report"] = "Errors at the till" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/saved/searches/Errors%20at%20the%20till?output_mode=json", (string?)null), Last());

            var user = Assert.Single(await Run(splunk, SplunkConnector.UserOperation, new() { ["user"] = "ada" })).Fields;
            Assert.Equal(("GET", $"{Api}/authentication/users/ada?output_mode=json", (string?)null), Last());
            Assert.Equal(("ada", "Ada Lovelace", "[\"power\",\"user\"]"), ((string?)user["id"], (string?)user["realname"], (string?)user["roles"]));
            await Run(splunk, SplunkConnector.UserOperation, new() { ["user"] = "ada?x=1" });
            Assert.Equal($"{Api}/authentication/users/ada%3Fx%3D1?output_mode=json", Last().Item2); // escaped: a name cannot become a query
            await Run(splunk, SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["email"] = "ada@kafe.example.test", ["realname"] = "Ada Lovelace", ["roles"] = "user, power" });
            Assert.Equal(("POST", $"{Api}/authentication/users/ada?output_mode=json", (string?)"email=ada%40kafe.example.test&realname=Ada+Lovelace&roles=user&roles=power"), Last()); // each role its own
            await Run(splunk, SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["realname"] = "Ada" });
            Assert.Equal("realname=Ada", Last().Item3);
            var gone = Assert.Single(await Run(splunk, SplunkConnector.DeleteUserOperation, new() { ["user"] = "ada" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/authentication/users/ada?output_mode=json", (string?)null), Last());
            Assert.Equal(("ada", true), ((string?)gone["user"], (bool?)gone["deleted"]));
            Assert.DoesNotContain(new SplunkConnector().Operations, operation => operation.Name.Contains("create_user", StringComparison.Ordinal)); // never created: Splunk needs a password
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_splunks_messages_on_a_failure()
        {
            var splunk = new Splunk();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Server, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(splunk, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Splunk has no operation 'create_user' - create_search, search_job,", await Refused("create_user", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(SplunkConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the search (search).", await Refused(SplunkConnector.CreateSearchOperation, new()));
            Assert.Equal("'oneshot' is not blocking or normal (exec_mode).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["exec_mode"] = "oneshot" }));
            Assert.Equal("'yesterday' is not a time - -24h@h, now, 2026-10-13T08:00:00Z or seconds since 1970 (earliest_time).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["earliest_time"] = "yesterday" }));
            Assert.Equal("'2026-10-13T08:00:00' is not a time - -24h@h, now, 2026-10-13T08:00:00Z or seconds since 1970 (latest_time).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["latest_time"] = "2026-10-13T08:00:00" })); // a moment without its offset
            Assert.Equal("'-24h; rm' is not a time - -24h@h, now, 2026-10-13T08:00:00Z or seconds since 1970 (index_latest).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["index_latest"] = "-24h; rm" }));
            Assert.Equal("'slow' is not fast, smart or verbose (adhoc_search_level).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["adhoc_search_level"] = "slow" }));
            Assert.Equal("'live' is not normal or realtime (search_mode).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["search_mode"] = "live" }));
            Assert.Equal("'-1' is not a whole number, 0 or more (timeout).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["timeout"] = "-1" }));
            Assert.Equal("'my app' is not an app's name (namespace).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["namespace"] = "my app" }));
            Assert.Equal("'idx-1;idx-2' is not a comma-separated list of search peers (remote_server_list).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["remote_server_list"] = "idx-1;idx-2" }));
            Assert.Equal("'host count' is not a comma-separated list of fields (rf).", await Refused(SplunkConnector.CreateSearchOperation, new() { ["search"] = "index=main", ["rf"] = "host count" }));
            Assert.Equal("Give the search job's id (job).", await Refused(SplunkConnector.SearchResultsOperation, new()));
            Assert.Equal("'1697/../users' is not a search job's id - letters, digits, '.', '_' and '-' (job).", await Refused(SplunkConnector.DeleteSearchJobOperation, new() { ["job"] = "1697/../users" }));
            Assert.Equal("Give a field and its value together (field, value).", await Refused(SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697.1", ["field"] = "host" }));
            Assert.Equal("Give a field and its value together (field, value).", await Refused(SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697.1", ["value"] = "web-01" }));
            Assert.Equal("'host name' is not a field's name (field).", await Refused(SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697.1", ["field"] = "host name", ["value"] = "x" }));
            Assert.Equal("'yes' is not true or false (summary).", await Refused(SplunkConnector.SearchResultsOperation, new() { ["job"] = "1697.1", ["summary"] = "yes" }));
            Assert.Equal("'size' is not asc or desc (sort_dir).", await Refused(SplunkConnector.SearchJobsOperation, new() { ["sort_dir"] = "size" }));
            Assert.Equal("'fuzzy' is not auto, alpha, alpha_case or num (sort_mode).", await Refused(SplunkConnector.SearchJobsOperation, new() { ["sort_mode"] = "fuzzy" }));
            Assert.Equal("'disk usage' is not a field's name (sort_key).", await Refused(SplunkConnector.SearchJobsOperation, new() { ["sort_key"] = "disk usage" }));
            Assert.Equal("'maybe' is not true or false (add_orphan_field).", await Refused(SplunkConnector.ReportsOperation, new() { ["add_orphan_field"] = "maybe" }));
            Assert.Equal("Give the report's name (report).", await Refused(SplunkConnector.ReportOperation, new()));
            Assert.Equal("'" + new string('r', 256) + "' is not a report's name - 255 characters at most, no control characters (report).", await Refused(SplunkConnector.DeleteReportOperation, new() { ["report"] = new string('r', 256) }));
            Assert.Equal("'Fika\u0001' is not a report's name - 255 characters at most, no control characters (name).", await Refused(SplunkConnector.CreateReportOperation, new() { ["name"] = "Fika\u0001", ["search"] = "index=main" }));
            Assert.Equal("Give the report's name (name).", await Refused(SplunkConnector.CreateReportOperation, new() { ["search"] = "index=main" }));
            Assert.Equal("Give a search job or a search - one of them (job, search).", await Refused(SplunkConnector.CreateReportOperation, new() { ["name"] = "Errors" }));
            Assert.Equal("Give a search job or a search - one of them (job, search).", await Refused(SplunkConnector.CreateReportOperation, new() { ["name"] = "Errors", ["job"] = "1697.1", ["search"] = "index=main" }));
            Assert.Equal("'every monday' is not a cron - 0 6 * * 1 (cron_schedule).", await Refused(SplunkConnector.CreateReportOperation, new() { ["name"] = "Errors", ["search"] = "index=main", ["cron_schedule"] = "every monday" }));
            Assert.Equal("Give the user's name (user).", await Refused(SplunkConnector.UserOperation, new()));
            Assert.Equal("'ada lovelace' is not a user's name - no spaces, ':', '/' or '\\\\' (user).", await Refused(SplunkConnector.DeleteUserOperation, new() { ["user"] = "ada lovelace" }));
            Assert.Equal("'ada/../admin' is not a user's name - no spaces, ':', '/' or '\\\\' (user).", await Refused(SplunkConnector.UserOperation, new() { ["user"] = "ada/../admin" }));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["email"] = "ada@kafe" }));
            Assert.Equal("'ada,bo@kafe.example.test' is not an e-mail (email).", await Refused(SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["email"] = "ada,bo@kafe.example.test" }));
            Assert.Equal("'power user' is not a role's name - letters, digits, '_' and '-' (roles).", await Refused(SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["roles"] = "user, power user" }));
            Assert.Equal("Give what to change - an e-mail, a full name or roles.", await Refused(SplunkConnector.UpdateUserOperation, new() { ["user"] = "ada", ["roles"] = " , " }));
            Assert.Equal("Give the Splunk server's management address - https://splunk.example.com:8089.", await Refused(SplunkConnector.UsersOperation, new(), endpoint: ""));
            Assert.Equal("The Splunk connection has no authentication token.", await Refused(SplunkConnector.UsersOperation, new(), credentials: new()));
            Assert.Empty(splunk.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Splunk { Answer = (_, _) => (status, answer) }, SplunkConnector.UserOperation, new() { ["user"] = "ada" }))).Message;
            Assert.Equal("HTTP 404: Could not find object id=ada", await Failed(HttpStatusCode.NotFound, "{\"messages\":[{\"type\":\"ERROR\",\"text\":\"Could not find object id=ada\"}]}"));
            Assert.Equal("HTTP 400: In handler 'users': Bad role; Roles must exist", await Failed(HttpStatusCode.BadRequest, "{\"messages\":[{\"type\":\"ERROR\",\"text\":\"In handler 'users': Bad role\"},{\"type\":\"ERROR\",\"text\":\"Roles must exist\"}]}")); // every message
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"messages\":[{\"type\":\"ERROR\",\"text\":\"" + new string('e', 400) + "\"}]}")); // a long one cut
            Assert.Equal("Splunk answered HTTP 200 with what is not JSON - check the address: its management port, not its web one; a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Splunk login</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("Splunk answered with no entry.", await Failed(HttpStatusCode.OK, "{\"entry\":[]}"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Splunk splunk, string endpoint = Server) =>
                new SplunkConnector().TestConnectionAsync(Contexts.Test(new StubEgress(splunk), endpoint, null, Credentials));
            var me = new Splunk { Answer = (_, _) => (HttpStatusCode.OK, "{\"entry\":[{\"name\":\"tokens\",\"content\":{\"username\":\"ada\",\"roles\":[\"admin\"]}}]}") };
            var tested = await Test(me, Server + "/services/");
            Assert.Equal((true, "Splunk: the token is ada's."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/authentication/current-context?output_mode=json", "GET", "Bearer eyJraWQiOiJzcGx1bmsi"), (me.Requests[0].Url, me.Requests[0].Method.Method, me.Requests[0].Authorization)); // its path given once
            Assert.Equal("Splunk: the token is a user's.", (await Test(new Splunk())).Message);
            var refused = await Test(new Splunk { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"messages\":[{\"type\":\"WARN\",\"text\":\"call not properly authenticated\"}]}") });
            Assert.Equal((false, "HTTP 401: call not properly authenticated"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "splunk"), (new SplunkConnector().AuthType, new SplunkConnector().ConnectorType));
            Assert.Equal(15, new SplunkConnector().Operations.Count);
            Assert.Equal(("api-key", "Authentication token (Settings > Tokens)"), (Assert.Single(new SplunkConnector().CredentialLabels!).Key, Assert.Single(new SplunkConnector().CredentialLabels!).Value)); // in Splunk's words
        }
    }
}
