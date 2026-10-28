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
using Stomme.Modules.Connectors.Jenkins;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Jenkins (n8n's Jenkins node: a job built, copied and created, the builds, the instance quieted and restarted):
    /// every call a server's remote access API with the user's name and API token; jobs read at the top and in a folder, a
    /// job read by its path; a build queued with parameters or without, Jenkins's queue item its proof; a job's builds newest
    /// first no more than asked, a build got by number or as the last, its log - its end when long - and stopped; a job
    /// copied and created, each read back; the instance quieted, back again, restarted and shut down, a 503 Jenkins going
    /// away; what is not JSON taken for Jenkins's only with Jenkins's header; what could go wrong refused before anything is
    /// sent; Jenkins's word on a failure; a test naming whose the token is.
    /// </summary>
    public sealed class JenkinsConnectorTests
    {
        private const string Site = "https://jenkins.example.test";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe-ci", ["password"] = "jenkins-token-1" };

        private sealed class Jenkins : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? ContentType, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body, string? Location, bool Header, string? Error)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null, true, null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));
                var (status, answer, location, header, error) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, answer.StartsWith('{') ? "application/json" : "text/html") };
                if (header)
                {
                    response.Headers.Add("X-Jenkins", "2.479.1");
                }

                if (location is not null)
                {
                    response.Headers.Location = new Uri(location);
                }

                if (error is not null)
                {
                    response.Headers.Add("X-Error", error);
                }

                return response;
            }
        }

        private static Task<List<ConnectorRecord>> Run(Jenkins jenkins, string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new JenkinsConnector(), Contexts.Extract(new StubEgress(jenkins), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private const string Job = "{\"_class\":\"hudson.model.FreeStyleProject\",\"name\":\"build\",\"fullName\":\"shop/build\",\"color\":\"blue\",\"buildable\":true,\"nextBuildNumber\":43,\"lastBuild\":{\"number\":42,\"result\":\"SUCCESS\"},\"healthReport\":[{\"score\":100,\"description\":\"Kafé builds\"}]}";

        [Fact]
        public async Task Jobs_are_read_built_and_their_builds_read_got_logged_and_stopped()
        {
            var jenkins = new Jenkins
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("tree=jobs", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"jobs\":[{\"name\":\"build\",\"fullName\":\"shop/build\",\"color\":\"blue\",\"buildable\":true},{\"name\":\"deploy\",\"fullName\":\"shop/deploy\",\"color\":\"red\"}]}", null, true, null),
                    ("GET", var u) when u.Contains("allBuilds", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"allBuilds\":[" + string.Join(",", Enumerable.Range(1, 5).Select(i => "{\"number\":" + (46 - i) + ",\"result\":\"SUCCESS\",\"building\":false,\"duration\":1200}")) + "]}", null, true, null),
                    ("GET", var u) when u.EndsWith("/consoleText", StringComparison.Ordinal) => (HttpStatusCode.OK, "Started by user Ada\nKaffe på café\nFinished: SUCCESS\n", null, true, null),
                    ("GET", var u) when u.Contains("/42/api/json", StringComparison.Ordinal) || u.Contains("/lastBuild/api/json", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"number\":42,\"result\":\"SUCCESS\",\"building\":false,\"actions\":[{\"causes\":[{\"shortDescription\":\"Started by user Ada\"}]}]}", null, true, null),
                    ("GET", _) => (HttpStatusCode.OK, Job, null, true, null),
                    ("POST", var u) when u.Contains("/build", StringComparison.Ordinal) => (HttpStatusCode.Created, "", "https://jenkins.example.test/queue/item/314/", true, null),
                    _ => (HttpStatusCode.OK, "", null, true, null),
                },
            };
            (string, string, string?) Last() => (jenkins.Requests[^1].Method.Method, jenkins.Requests[^1].Url, jenkins.Requests[^1].Body);

            var jobs = await Run(jenkins, JenkinsConnector.JobsOperation, new());
            Assert.Equal(("GET", $"{Site}/api/json?tree=jobs%5Bname%2CfullName%2Curl%2Ccolor%2Cbuildable%2Cdescription%5D", (string?)null), Last());
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("kafe-ci:jenkins-token-1")), jenkins.Requests[^1].Authorization);
            Assert.Equal(new[] { ("shop/build", "blue"), ("shop/deploy", "red") }, jobs.Select(job => ((string)job.Fields["fullName"]!, (string)job.Fields["color"]!)));
            Assert.Single(await Run(jenkins, JenkinsConnector.JobsOperation, new() { ["folder"] = "/shop/fika bröd/", ["max"] = "1" }, endpoint: Site + "/"));
            Assert.StartsWith($"{Site}/job/shop/job/fika%20br%C3%B6d/api/json?tree=jobs", Last().Item2); // a folder's, escaped; no more than asked

            await Run(jenkins, null!, new());
            Assert.StartsWith($"{Site}/api/json?tree=jobs", Last().Item2); // no operation: the jobs
            var job = Assert.Single(await Run(jenkins, JenkinsConnector.JobOperation, new() { ["job"] = "shop/build" })).Fields;
            Assert.Equal($"{Site}/job/shop/job/build/api/json?tree=" + Uri.EscapeDataString("name,fullName,url,description,color,buildable,inQueue,nextBuildNumber,lastBuild[number,url,result,timestamp,duration],lastCompletedBuild[number,result],lastSuccessfulBuild[number],lastFailedBuild[number],healthReport[score,description]"), Last().Item2);
            Assert.Equal(("shop/build", 43L, "{\"number\":42,\"result\":\"SUCCESS\"}", "[{\"score\":100,\"description\":\"Kafé builds\"}]"), ((string?)job["fullName"], (long?)job["nextBuildNumber"], (string?)job["lastBuild"], (string?)job["healthReport"]));

            var queued = Assert.Single(await Run(jenkins, JenkinsConnector.BuildOperation, new() { ["job"] = "shop/build" })).Fields;
            Assert.Equal(("POST", $"{Site}/job/shop/job/build/build", (string?)null), Last());
            Assert.Equal(("shop/build", true, 314L), ((string?)queued["job"], (bool?)queued["queued"], (long?)queued["queue_item"])); // Jenkins's queue item
            await Run(jenkins, JenkinsConnector.BuildOperation, new() { ["job"] = "shop/build", ["parameters"] = "{\"BRANCH\": \"main\", \"DRY_RUN\": true, \"COPIES\": 2, \"NOTE\": \"Kaffe & bröd\"}" });
            Assert.Equal(("POST", $"{Site}/job/shop/job/build/buildWithParameters", (string?)"BRANCH=main&DRY_RUN=true&COPIES=2&NOTE=Kaffe+%26+br%C3%B6d"), Last());
            await Run(jenkins, JenkinsConnector.BuildOperation, new() { ["job"] = "c++ build", ["parameters"] = "{\"DRY_RUN\": false}" });
            Assert.Equal(("POST", $"{Site}/job/c%2B%2B%20build/buildWithParameters", (string?)"DRY_RUN=false"), Last()); // a name escaped whole, a plus too
            Assert.Equal("application/x-www-form-urlencoded", jenkins.Requests[^1].ContentType);

            var builds = await Run(jenkins, JenkinsConnector.BuildsOperation, new() { ["job"] = "shop/build", ["max"] = "3" });
            Assert.Equal($"{Site}/job/shop/job/build/api/json?tree=" + Uri.EscapeDataString("allBuilds[number,url,displayName,description,result,building,timestamp,duration]{0,3}"), Last().Item2); // newest first, no more than asked
            Assert.Equal(new[] { 45L, 44L, 43L }, builds.Select(build => (long)build.Fields["number"]!));
            Assert.Equal(5, (await Run(jenkins, JenkinsConnector.BuildsOperation, new() { ["job"] = "build" })).Count);
            Assert.EndsWith(Uri.EscapeDataString("{0,1000}"), Last().Item2);

            var build = Assert.Single(await Run(jenkins, JenkinsConnector.BuildInfoOperation, new() { ["job"] = "shop/build", ["number"] = "42" })).Fields;
            Assert.Equal(("GET", $"{Site}/job/shop/job/build/42/api/json", (string?)null), Last());
            Assert.Equal(("SUCCESS", "[{\"causes\":[{\"shortDescription\":\"Started by user Ada\"}]}]"), ((string?)build["result"], (string?)build["actions"]));
            await Run(jenkins, JenkinsConnector.BuildInfoOperation, new() { ["job"] = "shop/build", ["number"] = "lastBuild" });
            Assert.Equal($"{Site}/job/shop/job/build/lastBuild/api/json", Last().Item2);

            var log = Assert.Single(await Run(jenkins, JenkinsConnector.ConsoleOperation, new() { ["job"] = "shop/build", ["number"] = "42" })).Fields;
            Assert.Equal(("GET", $"{Site}/job/shop/job/build/42/consoleText", (string?)null), Last());
            Assert.Equal(("shop/build", "42", "Started by user Ada\nKaffe på café\nFinished: SUCCESS\n", false), ((string?)log["job"], (string?)log["number"], (string?)log["text"], (bool?)log["truncated"]));
            var longLog = "x" + new string('a', 99_999) + "Finished: SUCCESS";
            jenkins.Answer = (_, _) => (HttpStatusCode.OK, longLog, null, true, null);
            var tail = Assert.Single(await Run(jenkins, JenkinsConnector.ConsoleOperation, new() { ["job"] = "build", ["number"] = "42" })).Fields;
            Assert.Equal((100_000, true), (((string)tail["text"]!).Length, (bool?)tail["truncated"])); // its last 100,000 characters
            Assert.EndsWith("Finished: SUCCESS", (string?)tail["text"]); // its end kept
            Assert.StartsWith("aaa", (string?)tail["text"]);

            jenkins.Answer = (_, _) => (HttpStatusCode.Found, "", "https://jenkins.example.test/job/shop/job/build/42/", true, null);
            var stopped = Assert.Single(await Run(jenkins, JenkinsConnector.StopBuildOperation, new() { ["job"] = "shop/build", ["number"] = "42" })).Fields;
            Assert.Equal(("POST", $"{Site}/job/shop/job/build/42/stop", (string?)null), Last()); // a redirect, Jenkins's own
            Assert.Equal(("shop/build", "42", true), ((string?)stopped["job"], (string?)stopped["number"], (bool?)stopped["stopped"]));
        }

        [Fact]
        public async Task Jobs_are_copied_and_created_and_the_instance_quieted_restarted_and_shut_down()
        {
            var jenkins = new Jenkins
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("POST", var u) when u.Contains("/createItem", StringComparison.Ordinal) || u.Contains("uiet", StringComparison.Ordinal) => (HttpStatusCode.Found, "", "https://jenkins.example.test/", true, null),
                    ("POST", var u) when u.EndsWith("/restart", StringComparison.Ordinal) => (HttpStatusCode.ServiceUnavailable, "<html>Jenkins is restarting</html>", null, false, null),
                    ("POST", _) => (HttpStatusCode.OK, "", null, true, null),
                    ("GET", var u) when u.Contains("quietingDown", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"mode\":\"NORMAL\",\"nodeName\":\"\",\"quietingDown\":true,\"useSecurity\":true}", null, true, null),
                    _ => (HttpStatusCode.OK, Job.Replace("\"build\"", "\"build-copy\"").Replace("shop/build", "shop/build-copy"), null, true, null),
                },
            };

            var copied = Assert.Single(await Run(jenkins, JenkinsConnector.CopyJobOperation, new() { ["job"] = "shop/build", ["new_job"] = "build-copy" })).Fields;
            Assert.Equal(new[] { ("POST", $"{Site}/job/shop/createItem?name=build-copy&mode=copy&from=shop%2Fbuild"), ("GET", $"{Site}/job/shop/job/build-copy/api/json?tree=" + Uri.EscapeDataString("name,fullName,url,description,color,buildable,inQueue,nextBuildNumber,lastBuild[number,url,result,timestamp,duration],lastCompletedBuild[number,result],lastSuccessfulBuild[number],lastFailedBuild[number],healthReport[score,description]")) }, jenkins.Requests.Select(request => (request.Method.Method, request.Url))); // beside its job, then read back
            Assert.Equal("shop/build-copy", (string?)copied["fullName"]);
            jenkins.Requests.Clear();
            await Run(jenkins, JenkinsConnector.CopyJobOperation, new() { ["job"] = "build", ["new_job"] = "build 2" });
            Assert.Equal($"{Site}/createItem?name=build%202&mode=copy&from=build", jenkins.Requests[0].Url); // at the top

            jenkins.Requests.Clear();
            const string Config = "<project><description>Kafé</description><builders/></project>";
            Assert.Single(await Run(jenkins, JenkinsConnector.CreateJobOperation, new() { ["new_job"] = "shop/fika", ["config"] = Config }));
            Assert.Equal(("POST", $"{Site}/job/shop/createItem?name=fika", "application/xml", (string?)Config), (jenkins.Requests[0].Method.Method, jenkins.Requests[0].Url, jenkins.Requests[0].ContentType, jenkins.Requests[0].Body));
            Assert.StartsWith($"{Site}/job/shop/job/fika/api/json?tree=", jenkins.Requests[1].Url);

            jenkins.Requests.Clear();
            var quiet = Assert.Single(await Run(jenkins, JenkinsConnector.QuietDownOperation, new() { ["reason"] = "Fika & bröd" })).Fields;
            Assert.Equal(new[] { $"{Site}/quietDown?reason=Fika%20%26%20br%C3%B6d", $"{Site}/api/json?tree=mode%2CnodeName%2CquietingDown%2CuseSecurity" }, jenkins.Requests.Select(request => request.Url)); // then read back
            Assert.Equal((true, "NORMAL"), ((bool?)quiet["quietingDown"], (string?)quiet["mode"]));
            jenkins.Requests.Clear();
            await Run(jenkins, JenkinsConnector.QuietDownOperation, new());
            Assert.Equal($"{Site}/quietDown", jenkins.Requests[0].Url);
            jenkins.Requests.Clear();
            await Run(jenkins, JenkinsConnector.CancelQuietDownOperation, new());
            Assert.Equal(("POST", $"{Site}/cancelQuietDown"), (jenkins.Requests[0].Method.Method, jenkins.Requests[0].Url));

            foreach (var (operation, path) in new[] { (JenkinsConnector.SafeRestartOperation, "/safeRestart"), (JenkinsConnector.RestartOperation, "/restart"), (JenkinsConnector.SafeExitOperation, "/safeExit"), (JenkinsConnector.ExitOperation, "/exit") })
            {
                var done = Assert.Single(await Run(jenkins, operation, new())).Fields;
                Assert.Equal(("POST", Site + path), (jenkins.Requests[^1].Method.Method, jenkins.Requests[^1].Url));
                Assert.Equal((operation, true), ((string?)done["operation"], (bool?)done["requested"])); // a restart's 503 Jenkins going away
            }
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_jenkinss_word_on_a_failure()
        {
            var jenkins = new Jenkins();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Site, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(jenkins, operation, parameters, endpoint, credentials))).Message;

            Assert.StartsWith("Jenkins has no operation 'reload' - jobs, job, build,", await Refused("reload", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(JenkinsConnector.BuildsOperation, new() { ["job"] = "build", ["max"] = "0" }));
            Assert.Equal("Give the job - its name or path.", await Refused(JenkinsConnector.BuildOperation, new()));
            Assert.Equal("'shop/../admin' is not a job's name or path - shop/build (job).", await Refused(JenkinsConnector.JobOperation, new() { ["job"] = "shop/../admin" }));
            Assert.Equal("'shop//build' is not a job's name or path - shop/build (job).", await Refused(JenkinsConnector.JobOperation, new() { ["job"] = "shop//build" }));
            Assert.Equal("'build?x=1' is not a job's name or path - shop/build (job).", await Refused(JenkinsConnector.StopBuildOperation, new() { ["job"] = "build?x=1", ["number"] = "1" }));
            Assert.Equal("'shop/.' is not a job's name or path - shop/build (folder).", await Refused(JenkinsConnector.JobsOperation, new() { ["folder"] = "shop/." }));
            Assert.Equal("Give the build - its number or lastBuild.", await Refused(JenkinsConnector.ConsoleOperation, new() { ["job"] = "build" }));
            Assert.Equal("'0' is not a build's number or lastBuild, lastSuccessfulBuild, lastFailedBuild, lastCompletedBuild or lastStableBuild (number).", await Refused(JenkinsConnector.BuildInfoOperation, new() { ["job"] = "build", ["number"] = "0" }));
            Assert.Equal("'latest' is not a build's number or lastBuild, lastSuccessfulBuild, lastFailedBuild, lastCompletedBuild or lastStableBuild (number).", await Refused(JenkinsConnector.StopBuildOperation, new() { ["job"] = "build", ["number"] = "latest" }));
            Assert.Equal("Give the new job's name.", await Refused(JenkinsConnector.CopyJobOperation, new() { ["job"] = "build" }));
            Assert.Equal("'shop/copy' is a path - a copy is made beside its job: give its name alone (new_job).", await Refused(JenkinsConnector.CopyJobOperation, new() { ["job"] = "build", ["new_job"] = "shop/copy" }));
            Assert.Equal("Give the job's config.xml.", await Refused(JenkinsConnector.CreateJobOperation, new() { ["new_job"] = "fika" }));
            Assert.Equal("The config is not XML - <project>...</project>.", await Refused(JenkinsConnector.CreateJobOperation, new() { ["new_job"] = "fika", ["config"] = "project" }));
            Assert.Equal("The parameters are not a JSON object - {\"BRANCH\": \"main\"}.", await Refused(JenkinsConnector.BuildOperation, new() { ["job"] = "build", ["parameters"] = "BRANCH=main" }));
            Assert.Equal("'BRANCH NAME' is not a parameter's name (parameters).", await Refused(JenkinsConnector.BuildOperation, new() { ["job"] = "build", ["parameters"] = "{\"BRANCH NAME\": \"main\"}" }));
            Assert.Equal("'TAGS' is not a text, a number or true or false (parameters).", await Refused(JenkinsConnector.BuildOperation, new() { ["job"] = "build", ["parameters"] = "{\"TAGS\": [\"a\"]}" }));
            Assert.Equal("'NOTE' is not a text, a number or true or false (parameters).", await Refused(JenkinsConnector.BuildOperation, new() { ["job"] = "build", ["parameters"] = "{\"NOTE\": null}" }));
            Assert.Equal("Give the server's address - https://jenkins.example.com.", await Refused(JenkinsConnector.JobsOperation, new(), endpoint: ""));
            Assert.Equal("The Jenkins connection has no user name.", await Refused(JenkinsConnector.JobsOperation, new(), credentials: new() { ["password"] = "t" }));
            Assert.Equal("The Jenkins connection has no API token.", await Refused(JenkinsConnector.JobsOperation, new(), credentials: new() { ["username"] = "u" }));
            Assert.Empty(jenkins.Requests); // nothing sent

            async Task<string> Failed(string operation, HttpStatusCode status, string answer, bool header = true, string? error = null, string? location = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Jenkins { Answer = (_, _) => (status, answer, location, header, error) }, operation, new() { ["job"] = "build", ["number"] = "42", ["new_job"] = "fika", ["config"] = "<project/>" }))).Message;
            Assert.Equal("HTTP 400: A job already exists with the name 'fika'", await Failed(JenkinsConnector.CreateJobOperation, HttpStatusCode.BadRequest, "<html>Error</html>", error: "A job already exists with the name 'fika'")); // Jenkins's word, from its header
            Assert.Equal("HTTP 404.", await Failed(JenkinsConnector.JobOperation, HttpStatusCode.NotFound, "<html>Not Found</html>"));
            Assert.Equal("HTTP 403: " + new string('e', 300), await Failed(JenkinsConnector.JobOperation, HttpStatusCode.Forbidden, "", error: new string('e', 400))); // a long word cut
            Assert.Equal("Jenkins answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(JenkinsConnector.JobOperation, HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("HTTP 200 came without Jenkins's own header - check the address: a sign-in page or a proxy answers so.", await Failed(JenkinsConnector.ConsoleOperation, HttpStatusCode.OK, "<html>Sign in</html>", header: false));
            Assert.Equal("HTTP 302.", await Failed(JenkinsConnector.StopBuildOperation, HttpStatusCode.Found, "", header: false, location: "https://sso.example.test/login")); // a redirect not Jenkins's
            Assert.Equal("HTTP 302.", await Failed(JenkinsConnector.JobOperation, HttpStatusCode.Found, "", location: "https://jenkins.example.test/login")); // a read never redirected
            Assert.Equal("Jenkins did not say where it queued the build.", await Failed(JenkinsConnector.BuildOperation, HttpStatusCode.Created, ""));
            Assert.Equal("Jenkins did not say where it queued the build.", await Failed(JenkinsConnector.BuildOperation, HttpStatusCode.Created, "", location: "https://jenkins.example.test/job/build/"));
            Assert.Equal("Jenkins did not say where it queued the build.", await Failed(JenkinsConnector.BuildOperation, HttpStatusCode.Created, "", location: "https://jenkins.example.test/job/build/42/")); // a number, but not its queue's
            Assert.Equal("HTTP 503.", await Failed(JenkinsConnector.QuietDownOperation, HttpStatusCode.ServiceUnavailable, "")); // only a restart's or a shutdown's 503 is Jenkins going away
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Jenkins jenkins) =>
                new JenkinsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(jenkins), Site, null, Credentials));
            var me = new Jenkins { Answer = (_, _) => (HttpStatusCode.OK, "{\"fullName\":\"Kafé CI\",\"id\":\"kafe-ci\"}", null, true, null) };
            var tested = await Test(me);
            Assert.Equal((true, "Jenkins: the token is Kafé CI's (kafe-ci)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Site}/me/api/json?tree=fullName,id", "GET"), (me.Requests[0].Url, me.Requests[0].Method.Method));
            Assert.Equal("Jenkins: the token is a user's.", (await Test(new Jenkins())).Message);
            var refused = await Test(new Jenkins { Answer = (_, _) => (HttpStatusCode.Unauthorized, "<html>Unauthorized</html>", null, true, null) });
            Assert.Equal((false, "HTTP 401."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Basic, "jenkins"), (new JenkinsConnector().AuthType, new JenkinsConnector().ConnectorType));
            Assert.Equal(15, new JenkinsConnector().Operations.Count);
            Assert.Equal(("password", "API token (your user > Security > API Token)"), (Assert.Single(new JenkinsConnector().CredentialLabels!).Key, Assert.Single(new JenkinsConnector().CredentialLabels!).Value)); // in Jenkins's words
        }
    }
}
