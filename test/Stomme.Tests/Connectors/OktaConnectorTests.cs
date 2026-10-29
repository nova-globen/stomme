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
using Stomme.Modules.Connectors.Okta;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Okta (n8n's Okta node: its users, and their groups): every call the org's API v1 with the token in Okta's SSWS
    /// header; users and groups read page by page by the cursor Okta's Link header names - its after, never the address beside
    /// it - no more than asked; a user got by id or login, created with its profile and custom attributes, activated or not,
    /// updated with the attributes given, activated, deactivated, suspended, unsuspended and deleted; a user's groups; a user
    /// added to a group and removed; what could go wrong refused before anything is sent; Okta's words on a failure; a test
    /// naming whose the token is.
    /// </summary>
    public sealed class OktaConnectorTests
    {
        private const string Org = "https://kafe.okta.example.test";

        private const string Api = Org + "/api/v1";

        private const string UserId = "00u1a2b3c4d5e6f7g8h9";

        private const string GroupId = "00g9h8g7f6e5d4c3b2a1";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "00okta_1" };

        private sealed class Okta : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string, string?)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

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

        private static Task<List<ConnectorRecord>> Run(Okta okta, string operation, Dictionary<string, string> parameters, string endpoint = Org, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new OktaConnector(), Contexts.Extract(new StubEgress(okta), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Users(int first, int count) =>
            "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"00u" + i.ToString("D17") + "\",\"status\":\"ACTIVE\",\"created\":\"2026-10-13T08:00:00.000Z\",\"type\":{\"id\":\"oty1\"},\"profile\":{\"firstName\":\"Ada\",\"lastName\":\"Lovelace " + i + "\",\"login\":\"ada" + i + "@kafe.example.test\",\"email\":\"ada" + i + "@kafe.example.test\"},\"_links\":{\"self\":{\"href\":\"https://kafe.okta.example.test/api/v1/users/00u" + i + "\"}}}")) + "]";

        // Okta's Link header: its own page, and the next one's address - on another host here, so following the address and not
        // the cursor it carries would show.
        private static string Next(string after) =>
            "<https://kafe.okta.example.test/api/v1/users?limit=200>; rel=\"self\", <https://elsewhere.example.test/api/v1/users?limit=200&q=Ada&after=" + after + ">; rel=\"next\"";

        [Fact]
        public async Task Lists_are_read_page_by_page_by_oktas_cursor_no_more_than_asked()
        {
            var okta = new Okta
            {
                Answer = (_, url) => url.Contains("after=c%2B2", StringComparison.Ordinal) ? (HttpStatusCode.OK, Users(401, 30), null)
                    : url.Contains("after=c1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Users(201, 200), Next("c%2B2"))
                    : (HttpStatusCode.OK, Users(1, 200), Next("c1")),
            };

            var read = await Run(okta, OktaConnector.UsersOperation, new() { ["max"] = "300" });
            Assert.Equal(300, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Api}/users?limit=200", $"{Api}/users?limit=200&after=c1" }, okta.Requests.Select(request => request.Url)); // by its after, never the address
            Assert.All(okta.Requests, request => Assert.Equal(("GET", (string?)"SSWS 00okta_1", (string?)null), (request.Method.Method, request.Authorization, request.Body)));
            var first = read[0].Fields;
            Assert.Equal(("00u00000000000000001", "ACTIVE", "Lovelace 1", "ada1@kafe.example.test", "{\"id\":\"oty1\"}"), ((string?)first["id"], (string?)first["status"], (string?)first["lastName"], (string?)first["login"], (string?)first["type"]));
            Assert.False(first.ContainsKey("_links") || first.ContainsKey("profile")); // its profile's attributes beside its own
            okta.Requests.Clear();
            Assert.Equal(430, (await Run(okta, null!, new())).Count); // no operation: the users - no next the end
            Assert.Equal($"{Api}/users?limit=200&after=c%2B2", okta.Requests[^1].Url); // a cursor escaped as it went
            Assert.Equal(3, okta.Requests.Count);
            var empty = new Okta { Answer = (_, _) => (HttpStatusCode.OK, "[]", Next("c1")) };
            Assert.Empty(await Run(empty, OktaConnector.UsersOperation, new())); // an empty page the end
            Assert.Single(empty.Requests);
            var afterless = new Okta { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 2), "<https://kafe.okta.example.test/api/v1/users?limit=200>; rel=\"next\"") };
            Assert.Equal(2, (await Run(afterless, OktaConnector.UsersOperation, new())).Count); // a next with no cursor the end
            Assert.Single(afterless.Requests);
            var emptyAfter = new Okta { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 2), "<https://kafe.okta.example.test/api/v1/users?after=&limit=200>; rel=\"next\"") };
            Assert.Equal(2, (await Run(emptyAfter, OktaConnector.UsersOperation, new())).Count); // nor an empty cursor
            Assert.Single(emptyAfter.Requests);
            var endless = new Okta { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 200), Next(Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, OktaConnector.UsersOperation, new())).Count); // 1,000 at most when no max is given

            okta.Requests.Clear();
            await Run(okta, OktaConnector.UsersOperation, new() { ["search"] = "profile.department eq \"Fika & bröd\" and status eq \"ACTIVE\"", ["max"] = "5" });
            Assert.Equal($"{Api}/users?search=profile.department%20eq%20%22Fika%20%26%20br%C3%B6d%22%20and%20status%20eq%20%22ACTIVE%22&limit=5", okta.Requests[^1].Url); // escaped: an & stays in the search
            await Run(okta, OktaConnector.UsersOperation, new() { ["q"] = "Ada", ["max"] = "5" });
            Assert.Equal($"{Api}/users?q=Ada&limit=5", okta.Requests[^1].Url);

            var groups = new Okta { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":\"" + GroupId + "\",\"type\":\"OKTA_GROUP\",\"objectClass\":[\"okta:user_group\"],\"profile\":{\"name\":\"Fika\",\"description\":\"Kafé\"}}]", null) };
            var group = Assert.Single(await Run(groups, OktaConnector.GroupsOperation, new() { ["q"] = "Fika & bröd" })).Fields;
            Assert.Equal($"{Api}/groups?q=Fika%20%26%20br%C3%B6d&limit=200", groups.Requests[^1].Url);
            Assert.Equal((GroupId, "OKTA_GROUP", "[\"okta:user_group\"]", "Fika", "Kafé"), ((string?)group["id"], (string?)group["type"], (string?)group["objectClass"], (string?)group["name"], (string?)group["description"]));
            await Run(groups, OktaConnector.GroupsOperation, new());
            Assert.Equal($"{Api}/groups?limit=200", groups.Requests[^1].Url);
            await Run(groups, OktaConnector.UserGroupsOperation, new() { ["user"] = "ada@kafe.example.test", ["max"] = "20" });
            Assert.Equal($"{Api}/users/ada%40kafe.example.test/groups?limit=20", groups.Requests[^1].Url);
        }

        [Fact]
        public async Task Users_are_created_updated_and_kept_through_their_life_and_their_groups()
        {
            var okta = new Okta
            {
                Answer = (method, _) => method.Method is "GET" or "POST" ? (HttpStatusCode.OK, "{\"id\":\"" + UserId + "\",\"status\":\"PROVISIONED\",\"profile\":{\"login\":\"ada@kafe.example.test\",\"department\":\"Kafé\"}}", null) : (HttpStatusCode.NoContent, "", null),
            };
            (string, string, string?) Last() => (okta.Requests[^1].Method.Method, okta.Requests[^1].Url, okta.Requests[^1].Body);

            var user = Assert.Single(await Run(okta, OktaConnector.UserOperation, new() { ["user"] = UserId })).Fields;
            Assert.Equal(("GET", $"{Api}/users/{UserId}", (string?)null), Last());
            Assert.Equal((UserId, "PROVISIONED", "Kafé"), ((string?)user["id"], (string?)user["status"], (string?)user["department"]));
            await Run(okta, OktaConnector.UserOperation, new() { ["user"] = "ada@kafe.example.test" });
            Assert.Equal($"{Api}/users/ada%40kafe.example.test", Last().Item2); // by login

            var created = Assert.Single(await Run(okta, OktaConnector.CreateUserOperation, new() { ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["email"] = "ada@kafe.example.test", ["login"] = "ada@kafe.example.test", ["department"] = "Kafé & bröd", ["second_email"] = "ada@home.example.test", ["profile"] = "{\"badge\": \"K-17\", \"shifts\": 3}" })).Fields;
            Assert.Equal(("POST", $"{Api}/users?activate=true", (string?)"{\"profile\":{\"firstName\":\"Ada\",\"lastName\":\"Lovelace\",\"email\":\"ada@kafe.example.test\",\"login\":\"ada@kafe.example.test\",\"secondEmail\":\"ada@home.example.test\",\"department\":\"Kafé & bröd\",\"badge\":\"K-17\",\"shifts\":3}}"), Last());
            Assert.Equal(UserId, (string?)created["id"]);
            await Run(okta, OktaConnector.CreateUserOperation, new() { ["first_name"] = "Bo", ["last_name"] = "Ek", ["email"] = "bo@kafe.example.test", ["login"] = "bo", ["activate"] = "False" });
            Assert.Equal($"{Api}/users?activate=false", Last().Item2);
            await Run(okta, OktaConnector.UpdateUserOperation, new() { ["user"] = UserId, ["title"] = "Barista", ["mobile_phone"] = "+46 70 000 00 00" });
            Assert.Equal(("POST", $"{Api}/users/{UserId}", (string?)"{\"profile\":{\"title\":\"Barista\",\"mobilePhone\":\"+46 70 000 00 00\"}}"), Last()); // a POST: the rest kept
            await Run(okta, OktaConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.test", ["profile"] = "{\"badge\": null}" });
            Assert.Equal(("POST", $"{Api}/users/ada%40kafe.example.test", (string?)"{\"profile\":{\"badge\":null}}"), Last());

            var activated = Assert.Single(await Run(okta, OktaConnector.ActivateUserOperation, new() { ["user"] = UserId })).Fields;
            Assert.Equal((UserId, true), ((string?)activated["user"], (bool?)activated["activated"]));
            Assert.Equal(("POST", $"{Api}/users/{UserId}/lifecycle/activate?sendEmail=true", (string?)null), Last());
            await Run(okta, OktaConnector.ActivateUserOperation, new() { ["user"] = UserId, ["send_email"] = "false" });
            Assert.Equal($"{Api}/users/{UserId}/lifecycle/activate?sendEmail=false", Last().Item2);
            var deactivated = Assert.Single(await Run(okta, OktaConnector.DeactivateUserOperation, new() { ["user"] = "ada@kafe.example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/users/ada%40kafe.example.test/lifecycle/deactivate?sendEmail=false", (string?)null), Last());
            Assert.Equal(("ada@kafe.example.test", true), ((string?)deactivated["user"], (bool?)deactivated["deactivated"]));
            await Run(okta, OktaConnector.DeactivateUserOperation, new() { ["user"] = UserId, ["send_email"] = "true" });
            Assert.Equal($"{Api}/users/{UserId}/lifecycle/deactivate?sendEmail=true", Last().Item2);
            Assert.True((bool?)Assert.Single(await Run(okta, OktaConnector.SuspendUserOperation, new() { ["user"] = UserId })).Fields["suspended"]);
            Assert.Equal(("POST", $"{Api}/users/{UserId}/lifecycle/suspend", (string?)null), Last());
            Assert.True((bool?)Assert.Single(await Run(okta, OktaConnector.UnsuspendUserOperation, new() { ["user"] = UserId })).Fields["unsuspended"]);
            Assert.Equal(("POST", $"{Api}/users/{UserId}/lifecycle/unsuspend", (string?)null), Last());
            var deleted = Assert.Single(await Run(okta, OktaConnector.DeleteUserOperation, new() { ["user"] = UserId })).Fields;
            Assert.Equal(("DELETE", $"{Api}/users/{UserId}?sendEmail=false", (string?)null), Last());
            Assert.Equal((UserId, true), ((string?)deleted["user"], (bool?)deleted["deleted"]));
            await Run(okta, OktaConnector.DeleteUserOperation, new() { ["user"] = UserId, ["send_email"] = "TRUE" });
            Assert.Equal($"{Api}/users/{UserId}?sendEmail=true", Last().Item2);
            Assert.Equal("ada@kafe.example.test", (string?)Assert.Single(await Run(okta, OktaConnector.DeleteUserOperation, new() { ["user"] = "ada@kafe.example.test" })).Fields["user"]);
            Assert.Equal($"{Api}/users/ada%40kafe.example.test?sendEmail=false", Last().Item2); // by login: escaped in the address, as given in the record

            var added = Assert.Single(await Run(okta, OktaConnector.AddToGroupOperation, new() { ["group"] = GroupId, ["user"] = UserId })).Fields;
            Assert.Equal(("PUT", $"{Api}/groups/{GroupId}/users/{UserId}", (string?)null), Last());
            Assert.Equal((GroupId, UserId, true), ((string?)added["group"], (string?)added["user"], (bool?)added["added"]));
            Assert.True((bool?)Assert.Single(await Run(okta, OktaConnector.RemoveFromGroupOperation, new() { ["group"] = GroupId, ["user"] = UserId })).Fields["removed"]);
            Assert.Equal(("DELETE", $"{Api}/groups/{GroupId}/users/{UserId}", (string?)null), Last());
            Assert.All(okta.Requests, request => Assert.Equal("SSWS 00okta_1", request.Authorization));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_oktas_words_on_a_failure()
        {
            var okta = new Okta();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Org, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(okta, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> NewUser(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["first_name"] = "Ada", ["last_name"] = "Lovelace", ["email"] = "ada@kafe.example.test", ["login"] = "ada@kafe.example.test" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("Okta has no operation 'apps' - users, user, create_user,", await Refused("apps", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(OktaConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give a search or a name's start - Okta takes one of them.", await Refused(OktaConnector.UsersOperation, new() { ["search"] = "status eq \"ACTIVE\"", ["q"] = "Ada" }));
            Assert.Equal("Give the user's id or login.", await Refused(OktaConnector.UserOperation, new()));
            Assert.Equal("Give the user's id or login.", await Refused(OktaConnector.UserGroupsOperation, new()));
            Assert.Equal("'ada lovelace' is not a user's id or login (user).", await Refused(OktaConnector.DeleteUserOperation, new() { ["user"] = "ada lovelace" }));
            Assert.Equal("'00u1/../groups' is not a user's id or login (user).", await Refused(OktaConnector.SuspendUserOperation, new() { ["user"] = "00u1/../groups" }));
            Assert.Equal("'" + new string('a', 101) + "' is not a user's id or login (user).", await Refused(OktaConnector.UserOperation, new() { ["user"] = new string('a', 101) }));
            Assert.Equal("Give the group's id.", await Refused(OktaConnector.AddToGroupOperation, new() { ["user"] = UserId }));
            Assert.Equal("'fika' is not a group's id - 20 letters and digits (group).", await Refused(OktaConnector.AddToGroupOperation, new() { ["group"] = "fika", ["user"] = UserId }));
            Assert.Equal("'" + GroupId + "0' is not a group's id - 20 letters and digits (group).", await Refused(OktaConnector.RemoveFromGroupOperation, new() { ["group"] = GroupId + "0", ["user"] = UserId }));
            Assert.Equal("Give the user's id.", await Refused(OktaConnector.RemoveFromGroupOperation, new() { ["group"] = GroupId }));
            Assert.Equal("'ada@kafe.example.test' is not a user's id - 20 letters and digits (user).", await Refused(OktaConnector.AddToGroupOperation, new() { ["group"] = GroupId, ["user"] = "ada@kafe.example.test" }));
            Assert.Equal("Give the user's first name.", await Refused(OktaConnector.CreateUserOperation, NewUser(("first_name", ""))));
            Assert.Equal("Give the user's last name.", await Refused(OktaConnector.CreateUserOperation, NewUser(("last_name", ""))));
            Assert.Equal("Give the user's email.", await Refused(OktaConnector.CreateUserOperation, NewUser(("email", ""))));
            Assert.Equal("Give the user's login.", await Refused(OktaConnector.CreateUserOperation, NewUser(("login", ""))));
            Assert.Equal("'ada@kafe' is not an e-mail (email).", await Refused(OktaConnector.CreateUserOperation, NewUser(("email", "ada@kafe"))));
            Assert.Equal("'ada,bo@kafe.example.test' is not an e-mail (second_email).", await Refused(OktaConnector.UpdateUserOperation, new() { ["user"] = UserId, ["second_email"] = "ada,bo@kafe.example.test" }));
            Assert.Equal("'ada lovelace' is not a login - no spaces or slashes, 100 characters at most (login).", await Refused(OktaConnector.CreateUserOperation, NewUser(("login", "ada lovelace"))));
            Assert.Equal("The custom attributes are not a JSON object - {\"badge\": \"K-17\"}.", await Refused(OktaConnector.CreateUserOperation, NewUser(("profile", "badge = K-17"))));
            Assert.Equal("The custom attributes are not a JSON object - {\"badge\": \"K-17\"}.", await Refused(OktaConnector.UpdateUserOperation, new() { ["user"] = UserId, ["profile"] = "[\"K-17\"]" }));
            Assert.Equal("'Badge No' is not an attribute's name (profile).", await Refused(OktaConnector.UpdateUserOperation, new() { ["user"] = UserId, ["profile"] = "{\"Badge No\": \"K-17\"}" }));
            Assert.Equal("'1st' is not an attribute's name (profile).", await Refused(OktaConnector.UpdateUserOperation, new() { ["user"] = UserId, ["profile"] = "{\"1st\": \"K-17\"}" }));
            Assert.Equal("Give what to change - an attribute of the user's.", await Refused(OktaConnector.UpdateUserOperation, new() { ["user"] = UserId }));
            Assert.Equal("'yes' is not true or false (activate).", await Refused(OktaConnector.CreateUserOperation, NewUser(("activate", "yes"))));
            Assert.Equal("'maybe' is not true or false (send_email).", await Refused(OktaConnector.DeleteUserOperation, new() { ["user"] = UserId, ["send_email"] = "maybe" }));
            Assert.Equal("'1' is not true or false (send_email).", await Refused(OktaConnector.ActivateUserOperation, new() { ["user"] = UserId, ["send_email"] = "1" }));
            Assert.Equal("Give the Okta org's address - https://example.okta.com.", await Refused(OktaConnector.UsersOperation, new(), endpoint: ""));
            Assert.Equal("The Okta connection has no API token.", await Refused(OktaConnector.UsersOperation, new(), credentials: new()));
            Assert.Empty(okta.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Okta { Answer = (_, _) => (status, answer, null) }, OktaConnector.UserOperation, new() { ["user"] = UserId }))).Message;
            Assert.Equal("HTTP 404: Not found: Resource not found: ada (User)", await Failed(HttpStatusCode.NotFound, "{\"errorCode\":\"E0000007\",\"errorSummary\":\"Not found: Resource not found: ada (User)\",\"errorCauses\":[]}"));
            Assert.Equal("HTTP 400: Api validation failed: login; login: An object with this field already exists in the current organization", await Failed(HttpStatusCode.BadRequest, "{\"errorCode\":\"E0000001\",\"errorSummary\":\"Api validation failed: login\",\"errorCauses\":[{\"errorSummary\":\"login: An object with this field already exists in the current organization\"}]}")); // its causes beside
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"errorSummary\":\"" + new string('e', 400) + "\"}")); // a long one cut
            Assert.Equal("Okta answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_whose_the_token_is()
        {
            Task<ConnectionTestResult> Test(Okta okta, string endpoint = Org) =>
                new OktaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(okta), endpoint, null, Credentials));
            var me = new Okta { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"" + UserId + "\",\"profile\":{\"login\":\"ada@kafe.example.test\"}}", null) };
            var tested = await Test(me, Org + "/api/v1/");
            Assert.Equal((true, "Okta: the token is ada@kafe.example.test's."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/users/me", "GET", "SSWS 00okta_1"), (me.Requests[0].Url, me.Requests[0].Method.Method, me.Requests[0].Authorization)); // its API path given once
            Assert.Equal("Okta: the token is a user's.", (await Test(new Okta())).Message);
            var refused = await Test(new Okta { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"errorCode\":\"E0000011\",\"errorSummary\":\"Invalid token provided\",\"errorCauses\":[]}", null) });
            Assert.Equal((false, "HTTP 401: Invalid token provided"), (refused.Success, refused.Message));
            Assert.Equal((false, "Give the Okta org's address - https://example.okta.com."), ((await Test(new Okta(), "")).Success, (await Test(new Okta(), "")).Message));
            Assert.Equal((AuthenticationType.ApiKey, "okta"), (new OktaConnector().AuthType, new OktaConnector().ConnectorType));
            Assert.Equal(13, new OktaConnector().Operations.Count);
            Assert.Equal(("api-key", "API token (Security > API > Tokens)"), (Assert.Single(new OktaConnector().CredentialLabels!).Key, Assert.Single(new OktaConnector().CredentialLabels!).Value)); // in Okta's words
        }
    }
}
