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
using Stomme.Modules.Connectors.Netlify;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Netlify (n8n's Netlify node: sites and deploys): every call Netlify's API with the token as a bearer token;
    /// sites read page by page as long as Netlify links a next page, got and deleted; a site's deploys read by state, branch
    /// and production, a deploy got, created with its title, branch and draft, cancelled and published again; a site built,
    /// its cache cleared; a site's forms, a form's submissions - each field it was sent with its own too - and a submission
    /// deleted; what could go wrong refused before anything is sent; Netlify's word on a failure; a test naming whose the
    /// token is.
    /// </summary>
    public sealed class NetlifyConnectorTests
    {
        private const string Api = "https://api.netlify.com/api/v1";

        private const string DeployId = "5f1a2b3c4d5e6f7081920a3b";

        private const string FormId = "60bb1b2c3d4e5f6071829304";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "nfp_token_1" };

        private sealed class Netlify : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body, string? Link)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer, link) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (link is not null)
                {
                    response.Headers.TryAddWithoutValidation("Link", link);
                }

                return response;
            }
        }

        private static Task<List<ConnectorRecord>> Run(Netlify netlify, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new NetlifyConnector(), Contexts.Extract(new StubEgress(netlify), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Page(string prefix, int first, int count) =>
            "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + prefix + i + "\",\"name\":\"kafe-" + i + "\",\"ssl\":true,\"build_settings\":{\"repo_branch\":\"main\",\"base\":\"kafé\"}}")) + "]";

        [Fact]
        public async Task Sites_and_deploys_are_read_page_by_page_got_made_cancelled_restored_and_built()
        {
            var netlify = new Netlify
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.Contains("/sites?", StringComparison.Ordinal) && u.Contains("page=1&", StringComparison.Ordinal) => (HttpStatusCode.OK, Page("s", 1, 100), "<https://api.netlify.com/api/v1/sites?page=2>; rel=\"next\", <https://api.netlify.com/api/v1/sites?page=3>; rel=\"last\""),
                    ("GET", var u) when u.Contains("/sites?", StringComparison.Ordinal) => (HttpStatusCode.OK, Page("s", 101, 30), "<https://api.netlify.com/api/v1/sites?page=1>; rel=\"first\""),
                    ("GET", var u) when u.Contains("/deploys?", StringComparison.Ordinal) => (HttpStatusCode.OK, Page("d", 1, 2), null),
                    ("DELETE", _) => (HttpStatusCode.NoContent, "", null),
                    _ => (HttpStatusCode.OK, "{\"id\":\"" + DeployId + "\",\"state\":\"ready\",\"branch\":\"main\",\"context\":\"production\",\"title\":\"Fika & bröd\",\"locked\":false}", null),
                },
            };
            (string, string, string?) Last() => (netlify.Requests[^1].Method.Method, netlify.Requests[^1].Url, netlify.Requests[^1].Body);

            var sites = await Run(netlify, NetlifyConnector.SitesOperation, new());
            Assert.Equal(130, sites.Count); // as long as Netlify links a next page
            Assert.Equal(new[] { $"{Api}/sites?filter=all&page=1&per_page=100", $"{Api}/sites?filter=all&page=2&per_page=100" }, netlify.Requests.Select(request => request.Url));
            Assert.All(netlify.Requests, request => Assert.Equal("Bearer nfp_token_1", request.Authorization));
            Assert.Equal(("s1", "kafe-1", true, "{\"repo_branch\":\"main\",\"base\":\"kafé\"}"), ((string?)sites[0].Fields["id"], (string?)sites[0].Fields["name"], (bool?)sites[0].Fields["ssl"], (string?)sites[0].Fields["build_settings"]));
            netlify.Requests.Clear();
            Assert.Equal(120, (await Run(netlify, NetlifyConnector.SitesOperation, new() { ["max"] = "120" })).Count); // no more than asked
            Assert.Equal(2, netlify.Requests.Count);
            netlify.Requests.Clear();
            Assert.Equal(5, (await Run(netlify, NetlifyConnector.SitesOperation, new() { ["max"] = "5" })).Count);
            Assert.Equal($"{Api}/sites?filter=all&page=1&per_page=5", Assert.Single(netlify.Requests).Url); // a page as small as asked
            var answer = netlify.Answer;
            netlify.Requests.Clear();
            netlify.Answer = (_, _) => (HttpStatusCode.OK, "[]", "<https://api.netlify.com/api/v1/sites?page=2>; rel=\"next\"");
            Assert.Empty(await Run(netlify, NetlifyConnector.SitesOperation, new()));
            Assert.Single(netlify.Requests); // an empty page the end, though Netlify links a next one
            netlify.Answer = answer;

            await Run(netlify, NetlifyConnector.SiteOperation, new() { ["site"] = "kafe.netlify.app" }, endpoint: Api + "/");
            Assert.Equal(("GET", $"{Api}/sites/kafe.netlify.app", (string?)null), Last());
            var deleted = Assert.Single(await Run(netlify, NetlifyConnector.DeleteSiteOperation, new() { ["site"] = "3fa85f64-5717-4562-b3fc-2c963f66afa6" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/sites/3fa85f64-5717-4562-b3fc-2c963f66afa6", (string?)null), Last());
            Assert.Equal(("3fa85f64-5717-4562-b3fc-2c963f66afa6", true), ((string?)deleted["site"], (bool?)deleted["deleted"]));

            netlify.Requests.Clear();
            Assert.Equal(2, (await Run(netlify, NetlifyConnector.DeploysOperation, new() { ["site"] = "kafe", ["state"] = "Ready", ["branch"] = "feature/fika", ["production"] = "TRUE" })).Count); // no next page the end
            Assert.Equal($"{Api}/sites/kafe/deploys?state=ready&branch=feature%2Ffika&production=true&page=1&per_page=100", Assert.Single(netlify.Requests).Url);
            await Run(netlify, NetlifyConnector.DeploysOperation, new() { ["site"] = "kafe", ["production"] = "false" });
            Assert.Equal($"{Api}/sites/kafe/deploys?page=1&per_page=100", Last().Item2);
            await Run(netlify, null!, new() { ["site"] = "bageri" });
            Assert.Equal($"{Api}/sites/bageri/deploys?page=1&per_page=100", Last().Item2); // no operation: a site's deploys

            var deploy = Assert.Single(await Run(netlify, NetlifyConnector.DeployOperation, new() { ["deploy"] = DeployId })).Fields;
            Assert.Equal(("GET", $"{Api}/deploys/{DeployId}", (string?)null), Last());
            Assert.Equal(("ready", "Fika & bröd", false), ((string?)deploy["state"], (string?)deploy["title"], (bool?)deploy["locked"]));
            await Run(netlify, NetlifyConnector.CreateDeployOperation, new() { ["site"] = "kafe", ["title"] = "Fika & bröd", ["branch"] = "main", ["draft"] = "true" });
            Assert.Equal(("POST", $"{Api}/sites/kafe/deploys?title=Fika%20%26%20br%C3%B6d", (string?)"{\"branch\":\"main\",\"draft\":true}"), Last());
            await Run(netlify, NetlifyConnector.CreateDeployOperation, new() { ["site"] = "kafe", ["draft"] = "False" });
            Assert.Equal(("POST", $"{Api}/sites/kafe/deploys", (string?)"{\"draft\":false}"), Last()); // published, not a draft
            await Run(netlify, NetlifyConnector.CreateDeployOperation, new() { ["site"] = "kafe" });
            Assert.Equal(("POST", $"{Api}/sites/kafe/deploys", (string?)"{}"), Last());
            await Run(netlify, NetlifyConnector.CancelDeployOperation, new() { ["deploy"] = DeployId });
            Assert.Equal(("POST", $"{Api}/deploys/{DeployId}/cancel", (string?)null), Last());
            Assert.Equal("ready", (string?)Assert.Single(await Run(netlify, NetlifyConnector.RestoreDeployOperation, new() { ["site"] = "kafe", ["deploy"] = DeployId })).Fields["state"]);
            Assert.Equal(("POST", $"{Api}/sites/kafe/deploys/{DeployId}/restore", (string?)null), Last());
            await Run(netlify, NetlifyConnector.BuildOperation, new() { ["site"] = "kafe", ["clear_cache"] = "True" });
            Assert.Equal(("POST", $"{Api}/sites/kafe/builds", (string?)"{\"clear_cache\":true}"), Last());
            await Run(netlify, NetlifyConnector.BuildOperation, new() { ["site"] = "kafe" });
            Assert.Equal(("POST", $"{Api}/sites/kafe/builds", (string?)"{}"), Last());
        }

        [Fact]
        public async Task Forms_and_their_submissions_are_read_and_a_submission_deleted()
        {
            var netlify = new Netlify
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("GET", var u) when u.EndsWith("/forms", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"id\":\"" + FormId + "\",\"name\":\"fika\",\"submission_count\":2,\"fields\":[{\"name\":\"name\"}]}]", null),
                    ("GET", _) => (HttpStatusCode.OK, "[{\"id\":\"a1\",\"number\":2,\"name\":\"Ada Lovelace\",\"email\":\"ada@kafe.example.test\",\"data\":{\"name\":\"Ada\",\"email\":\"ada@kafe.example.test\",\"bun\":\"Kanelbulle\",\"cups\":2,\"ip\":\"192.0.2.10\"}},{\"id\":\"a2\",\"number\":1,\"data\":{}}]", "<https://api.netlify.com/api/v1/forms/x/submissions?page=1>; rel=\"prev\""),
                    _ => (HttpStatusCode.NoContent, "", null),
                },
            };
            (string, string, string?) Last() => (netlify.Requests[^1].Method.Method, netlify.Requests[^1].Url, netlify.Requests[^1].Body);

            var form = Assert.Single(await Run(netlify, NetlifyConnector.FormsOperation, new() { ["site"] = "kafe" })).Fields;
            Assert.Equal(("GET", $"{Api}/sites/kafe/forms", (string?)null), Last());
            Assert.Equal((FormId, 2L, "[{\"name\":\"name\"}]"), ((string?)form["id"], (long?)form["submission_count"], (string?)form["fields"]));

            var submissions = await Run(netlify, NetlifyConnector.SubmissionsOperation, new() { ["form"] = FormId });
            Assert.Equal($"{Api}/forms/{FormId}/submissions?page=1&per_page=100", Last().Item2); // a link that is not next the end
            Assert.Equal(2, submissions.Count);
            var first = submissions[0].Fields;
            Assert.Equal(("Ada Lovelace", "Kanelbulle", 2L, "192.0.2.10"), ((string?)first["name"], (string?)first["bun"], (long?)first["cups"], (string?)first["ip"])); // each field it was sent with its own too - Netlify's own kept
            Assert.StartsWith("{\"name\":\"Ada\"", (string?)first["data"]);
            Assert.False(submissions[1].Fields.ContainsKey("bun"));

            var gone = Assert.Single(await Run(netlify, NetlifyConnector.DeleteSubmissionOperation, new() { ["submission"] = DeployId })).Fields;
            Assert.Equal(("DELETE", $"{Api}/submissions/{DeployId}", (string?)null), Last());
            Assert.Equal((DeployId, true), ((string?)gone["id"], (bool?)gone["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_netlifys_word_on_a_failure()
        {
            var netlify = new Netlify();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(netlify, operation, parameters, credentials: credentials))).Message;

            Assert.StartsWith("Netlify has no operation 'publish' - sites, site, delete_site,", await Refused("publish", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(NetlifyConnector.SitesOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the site.", await Refused(NetlifyConnector.DeploysOperation, new()));
            Assert.Equal("'../account' is not a site's id, name or domain (site).", await Refused(NetlifyConnector.SiteOperation, new() { ["site"] = "../account" }));
            Assert.Equal("'kafe-' is not a site's id, name or domain (site).", await Refused(NetlifyConnector.BuildOperation, new() { ["site"] = "kafe-" }));
            Assert.Equal("Give the deploy.", await Refused(NetlifyConnector.CancelDeployOperation, new()));
            Assert.Equal("'5F1A2B3C4D5E6F7081920A3B' is not a deploy's id (deploy).", await Refused(NetlifyConnector.DeployOperation, new() { ["deploy"] = "5F1A2B3C4D5E6F7081920A3B" }));
            Assert.Equal("'fika' is not a form's id (form).", await Refused(NetlifyConnector.SubmissionsOperation, new() { ["form"] = "fika" }));
            Assert.Equal("Give the submission.", await Refused(NetlifyConnector.DeleteSubmissionOperation, new()));
            Assert.Equal("'fika bröd' is not a branch (branch).", await Refused(NetlifyConnector.CreateDeployOperation, new() { ["site"] = "kafe", ["branch"] = "fika bröd" }));
            Assert.StartsWith("'done' is not a deploy's state - ready, error, building,", await Refused(NetlifyConnector.DeploysOperation, new() { ["site"] = "kafe", ["state"] = "done" }));
            Assert.Equal("'yes' is not true or false (draft).", await Refused(NetlifyConnector.CreateDeployOperation, new() { ["site"] = "kafe", ["draft"] = "yes" }));
            Assert.Equal("'yes' is not true or false (clear_cache).", await Refused(NetlifyConnector.BuildOperation, new() { ["site"] = "kafe", ["clear_cache"] = "yes" }));
            Assert.Equal("'yes' is not true or false (production).", await Refused(NetlifyConnector.DeploysOperation, new() { ["site"] = "kafe", ["production"] = "yes" }));
            Assert.Equal("The Netlify connection has no access token.", await Refused(NetlifyConnector.SitesOperation, new(), credentials: new()));
            Assert.Empty(netlify.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Netlify { Answer = (_, _) => (status, answer, null) }, NetlifyConnector.SiteOperation, new() { ["site"] = "kafe" }))).Message;
            Assert.Equal("HTTP 404: Not Found", await Failed(HttpStatusCode.NotFound, "{\"code\":404,\"message\":\"Not Found\"}"));
            Assert.Equal("HTTP 401: Access Denied: your token is not valid", await Failed(HttpStatusCode.Unauthorized, "{\"error\":\"Access Denied: your token is not valid\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Netlify answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Netlify netlify) =>
                new NetlifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(netlify), "", null, Credentials));
            var user = new Netlify { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"u1\",\"full_name\":\"Ada Lovelace\",\"email\":\"ada@kafe.example.test\"}", null) };
            var tested = await Test(user);
            Assert.Equal((true, "Netlify: the token is Ada Lovelace's (ada@kafe.example.test)."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/user", "GET", "Bearer nfp_token_1"), (user.Requests[0].Url, user.Requests[0].Method.Method, user.Requests[0].Authorization));
            Assert.Equal("Netlify: the token is a user's.", (await Test(new Netlify { Answer = (_, _) => (HttpStatusCode.OK, "{\"full_name\":\"\"}", null) })).Message);
            var refused = await Test(new Netlify { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"code\":401,\"message\":\"Access Denied\"}", null) });
            Assert.Equal((false, "HTTP 401: Access Denied"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "netlify"), (new NetlifyConnector().AuthType, new NetlifyConnector().ConnectorType));
            Assert.Equal(12, new NetlifyConnector().Operations.Count);
            Assert.Equal(("token", "Personal access token (User settings > Applications)"), (Assert.Single(new NetlifyConnector().CredentialLabels!).Key, Assert.Single(new NetlifyConnector().CredentialLabels!).Value)); // in Netlify's words
        }
    }
}
