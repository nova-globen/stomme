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
using Stomme.Modules.Connectors.MicrosoftGraphSecurity;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft Graph Security (n8n's Microsoft Graph Security node: secure scores and their control profiles): the
    /// app's client credentials at the tenant's token endpoint, Graph's security API with the token as a bearer; scores and
    /// profiles read page by page by Graph's next link - followed only while it stays under the same collection - no more than
    /// asked, a score's control scores dropped unless asked for; one got; a profile updated by a PATCH with Graph's vendor
    /// information, its state, comment and assignee; what could go wrong refused before a token is asked for; Graph's code and
    /// message on a failure; a test naming the latest score.
    /// </summary>
    public sealed class MicrosoftGraphSecurityConnectorTests
    {
        private const string Api = "https://graph.microsoft.com/v1.0/security";

        private const string TokenUrl = "https://login.microsoftonline.com/kafe-tenant/oauth2/v2.0/token";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = TokenUrl };

        private sealed class Graph : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string? Prefer)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"value\":[]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body, request.Headers.TryGetValues("Prefer", out var prefer) ? string.Join(",", prefer) : null));
                var (status, answer) = url == TokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"eyJ.graph\",\"expires_in\":3599,\"token_type\":\"Bearer\"}") : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Graph graph, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new MicrosoftGraphSecurityConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(graph), "", operation, parameters, credentials: credentials ?? Credentials));

        private static string Scores(int first, int count, string? next) =>
            "{\"@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#security/secureScores\",\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"kafe-tenant_2026-10-" + (i % 28 + 1).ToString("D2") + "\",\"currentScore\":" + (40 + i % 10) + ".5,\"maxScore\":120,\"enabledServices\":[\"HasExchange\"],\"controlScores\":[{\"controlName\":\"AdminMFAV2\",\"score\":10}]}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";

        [Fact]
        public async Task Scores_and_profiles_are_read_page_by_page_by_graphs_next_link_no_more_than_asked()
        {
            var graph = new Graph
            {
                Answer = (_, url) => url.Contains("skiptoken=p2", StringComparison.Ordinal) ? (HttpStatusCode.OK, Scores(201, 30, null))
                    : url.Contains("skiptoken=p1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Scores(101, 100, Api + "/secureScores?$top=100&$skiptoken=p2"))
                    : (HttpStatusCode.OK, Scores(1, 100, Api + "/secureScores?$top=100&$skiptoken=p1")),
            };

            var scores = await Run(graph, GraphOps.Scores, new() { ["max"] = "150" });
            Assert.Equal(150, scores.Count); // no more than asked
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=app-1&client_secret=secret-1&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default"), (graph.Requests[0].Method.Method, graph.Requests[0].Url, graph.Requests[0].Body));
            Assert.Equal(new[] { $"{Api}/secureScores?$top=100", $"{Api}/secureScores?$top=100&$skiptoken=p1" }, graph.Requests.Skip(1).Select(request => request.Url));
            Assert.All(graph.Requests.Skip(1), request => Assert.Equal("Bearer eyJ.graph", request.Authorization));
            var first = scores[0].Fields;
            Assert.Equal(("kafe-tenant_2026-10-02", 41.5, 120L, "[\"HasExchange\"]"), ((string?)first["id"], (double?)first["currentScore"], (long?)first["maxScore"], (string?)first["enabledServices"]));
            Assert.False(first.ContainsKey("controlScores") || first.Keys.Any(key => key.StartsWith("@odata.", StringComparison.Ordinal))); // control scores dropped unless asked for
            graph.Requests.Clear();
            Assert.Equal(230, (await Run(graph, null!, new() { ["include_control_scores"] = "True" })).Count); // no operation: the scores - no next link the end
            Assert.Equal(3, graph.Requests.Count - 1);
            var withControls = await Run(graph, GraphOps.Scores, new() { ["include_control_scores"] = "true", ["max"] = "1", ["filter"] = "createdDateTime ge 2026-10-01T00:00:00Z" });
            Assert.Equal("[{\"controlName\":\"AdminMFAV2\",\"score\":10}]", (string?)Assert.Single(withControls).Fields["controlScores"]);
            Assert.Equal($"{Api}/secureScores?$top=1&$filter=createdDateTime%20ge%202026-10-01T00%3A00%3A00Z", graph.Requests[^1].Url);
            var endless = new Graph { Answer = (_, url) => (HttpStatusCode.OK, Scores(1, 100, Api + "/secureScores?$skiptoken=" + Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, GraphOps.Scores, new())).Count); // 1,000 at most when no max is given

            var elsewhere = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Scores(1, 2, "https://elsewhere.example.test/v1.0/security/secureScores?$skiptoken=p1")) };
            Assert.Equal("Microsoft Graph named a next page outside the collection - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elsewhere, GraphOps.Scores, new()))).Message);
            Assert.Equal(2, elsewhere.Requests.Count); // the token, one page: nothing sent elsewhere
            var otherCollection = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Scores(1, 2, Api + "/alerts?$skiptoken=p1")) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(otherCollection, GraphOps.Scores, new()));
            var oneScore = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Scores(1, 2, Api + "/secureScores/kafe-tenant_2026-10-01?$skiptoken=p1")) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(oneScore, GraphOps.Scores, new())); // nor one naming a single score
            Assert.Equal(2, oneScore.Requests.Count); // the token, one page: nothing sent to the score
            var same = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Scores(1, 2, Api + "/secureScores?$top=100")) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(same, GraphOps.Scores, new())); // a next link naming the page itself not followed

            var profiles = new Graph { Answer = (_, _) => (HttpStatusCode.OK, "{\"value\":[{\"id\":\"AdminMFAV2\",\"title\":\"Ensure MFA for admins\",\"maxScore\":10,\"controlStateUpdates\":[{\"state\":\"Default\"}]}]}") };
            var profile = Assert.Single(await Run(profiles, GraphOps.Profiles, new() { ["filter"] = "service eq 'Exchange'", ["max"] = "5" })).Fields;
            Assert.Equal($"{Api}/secureScoreControlProfiles?$top=5&$filter=service%20eq%20%27Exchange%27", profiles.Requests[^1].Url);
            Assert.Equal(("AdminMFAV2", 10L, "[{\"state\":\"Default\"}]"), ((string?)profile["id"], (long?)profile["maxScore"], (string?)profile["controlStateUpdates"]));
        }

        [Fact]
        public async Task A_score_and_a_profile_are_got_and_a_profile_updated()
        {
            var graph = new Graph { Answer = (method, url) => (HttpStatusCode.OK, url.Contains("/secureScores/", StringComparison.Ordinal) ? "{\"@odata.context\":\"x\",\"id\":\"kafe-tenant_2026-10-14\",\"currentScore\":48.5}" : "{\"@odata.context\":\"x\",\"id\":\"AdminMFAV2\",\"title\":\"Ensure MFA for admins\",\"controlStateUpdates\":[{\"state\":\"Ignored\"}]}") };
            (string, string, string?, string?) Last() => (graph.Requests[^1].Method.Method, graph.Requests[^1].Url, graph.Requests[^1].Body, graph.Requests[^1].Prefer);

            var score = Assert.Single(await Run(graph, GraphOps.Score, new() { ["id"] = "kafe-tenant_2026-10-14" })).Fields;
            Assert.Equal(("GET", $"{Api}/secureScores/kafe-tenant_2026-10-14", (string?)null, (string?)null), Last());
            Assert.Equal(48.5, (double?)score["currentScore"]);
            Assert.False(score.ContainsKey("@odata.context"));
            Assert.Equal("Ensure MFA for admins", (string?)Assert.Single(await Run(graph, GraphOps.Profile, new() { ["id"] = "AdminMFAV2" })).Fields["title"]);
            Assert.Equal(("GET", $"{Api}/secureScoreControlProfiles/AdminMFAV2", (string?)null, (string?)null), Last());

            await Run(graph, GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["state"] = "ignored", ["comment"] = "Kafé ser till det", ["assigned_to"] = "ada@kafe.example.com" });
            Assert.Equal(("PATCH", $"{Api}/secureScoreControlProfiles/AdminMFAV2", (string?)"{\"vendorInformation\":{\"provider\":\"SecureScore\",\"vendor\":\"Microsoft\"},\"state\":\"Ignored\",\"comment\":\"Kafé ser till det\",\"assignedTo\":\"ada@kafe.example.com\"}", (string?)"return=representation"), Last());
            await Run(graph, GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["state"] = "ThirdParty", ["provider"] = "Partner", ["vendor"] = "Kafé AB" });
            Assert.Equal("{\"vendorInformation\":{\"provider\":\"Partner\",\"vendor\":\"Kafé AB\"},\"state\":\"ThirdParty\"}", Last().Item3);
            Assert.All(graph.Requests.Where(request => request.Url != TokenUrl), request => Assert.Equal("Bearer eyJ.graph", request.Authorization));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_graphs_words_on_a_failure()
        {
            var graph = new Graph();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(graph, operation, parameters, credentials))).Message;

            Assert.Equal("Microsoft Graph Security has no operation 'alerts' - secure_scores, secure_score, control_profiles, control_profile or update_control_profile.", await Refused("alerts", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GraphOps.Scores, new() { ["max"] = "0" }));
            Assert.Equal("'yes' is not true or false (include_control_scores).", await Refused(GraphOps.Scores, new() { ["include_control_scores"] = "yes" }));
            Assert.Equal("Give the secure score's id (id).", await Refused(GraphOps.Score, new()));
            Assert.Equal("'kafe/../alerts' is not the secure score's id (id).", await Refused(GraphOps.Score, new() { ["id"] = "kafe/../alerts" }));
            Assert.Equal("Give the control profile's id (id).", await Refused(GraphOps.Profile, new()));
            Assert.Equal("'Admin MFA' is not the control profile's id (id).", await Refused(GraphOps.Update, new() { ["id"] = "Admin MFA", ["state"] = "Ignored" }));
            Assert.Equal("Give what to change - its state, a comment or whom it is assigned to.", await Refused(GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["vendor"] = "Microsoft" }));
            Assert.Equal("'Reviewed' is not Default, Ignored or ThirdParty (state).", await Refused(GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["state"] = "Reviewed" }));
            Assert.Equal("'Kafé\u0001' is not a text of 1,000 characters at most, without control characters (comment).", await Refused(GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["comment"] = "Kafé\u0001" }));
            Assert.Equal("'" + new string('a', 1001) + "' is not a text of 1,000 characters at most, without control characters (assigned_to).", await Refused(GraphOps.Update, new() { ["id"] = "AdminMFAV2", ["assigned_to"] = new string('a', 1001) }));
            Assert.Equal("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token.", await Refused(GraphOps.Scores, new(), new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1" }));
            Assert.Empty(graph.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Graph { Answer = (_, _) => (status, answer) }, GraphOps.Profile, new() { ["id"] = "AdminMFAV2" }))).Message;
            Assert.Equal("HTTP 403 Forbidden: Insufficient privileges to complete the operation.", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Insufficient privileges to complete the operation.\"}}"));
            Assert.Equal("HTTP 404 ResourceNotFound", await Failed(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ResourceNotFound\"}}"));
            Assert.Equal("HTTP 500 InternalServerError: " + new string('e', 300 - "InternalServerError: ".Length), await Failed(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"InternalServerError\",\"message\":\"" + new string('e', 400) + "\"}}")); // a long one cut
            Assert.Equal("Microsoft Graph answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_latest_secure_score()
        {
            Task<ConnectionTestResult> Test(Graph graph) =>
                new MicrosoftGraphSecurityConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(graph), "", null, Credentials));
            var graph = new Graph { Answer = (_, _) => (HttpStatusCode.OK, Scores(1, 1, null)) };
            var tested = await Test(graph);
            Assert.Equal((true, "Microsoft Graph Security: the latest secure score is 41.5 of 120."), (tested.Success, tested.Message));
            Assert.Equal(new[] { TokenUrl, $"{Api}/secureScores?$top=1" }, graph.Requests.Select(request => request.Url));
            Assert.Equal("Microsoft Graph Security: no secure score yet.", (await Test(new Graph())).Message);
            var refused = await Test(new Graph { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Missing SecurityEvents.Read.All.\"}}") });
            Assert.Equal((false, "HTTP 403 Forbidden: Missing SecurityEvents.Read.All."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "microsoftgraphsecurity", 5), (new MicrosoftGraphSecurityConnector().AuthType, new MicrosoftGraphSecurityConnector().ConnectorType, new MicrosoftGraphSecurityConnector().Operations.Count));
            Assert.Equal("Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token", new MicrosoftGraphSecurityConnector().CredentialLabels!["token-url"]);
        }

        private static class GraphOps
        {
            public const string Scores = MicrosoftGraphSecurityConnector.SecureScoresOperation;

            public const string Score = MicrosoftGraphSecurityConnector.SecureScoreOperation;

            public const string Profiles = MicrosoftGraphSecurityConnector.ControlProfilesOperation;

            public const string Profile = MicrosoftGraphSecurityConnector.ControlProfileOperation;

            public const string Update = MicrosoftGraphSecurityConnector.UpdateControlProfileOperation;
        }
    }
}
