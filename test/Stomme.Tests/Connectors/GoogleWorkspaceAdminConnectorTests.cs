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
using Stomme.Modules.Connectors.GoogleWorkspaceAdmin;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Workspace Admin (n8n's G Suite Admin node: a Workspace's users, groups and ChromeOS devices): a refresh
    /// token exchanged at Google's token endpoint, the Directory API with the token as a bearer; users, groups, a group's members
    /// and devices read page by page by Google's token, a page no bigger than what is left; a user got, updated by a PATCH - the
    /// rest kept - deleted, added to a group and removed; groups created, updated by a PATCH and deleted; a device got, updated
    /// and disabled; never a user created, never a password; what could go wrong refused before a token is asked for; Google's
    /// status word and message on a failure; a test naming the Workspace's domain.
    /// </summary>
    public sealed class GoogleWorkspaceAdminConnectorTests
    {
        private const string Api = GoogleWorkspaceAdminConnector.DefaultEndpoint;

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith(GoogleWorkspaceAdminConnector.DefaultTokenUrl, StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"access_token\":\"ya29.admin\",\"expires_in\":3599,\"token_type\":\"Bearer\"}")
                    : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new GoogleWorkspaceAdminConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, credentials: Credentials));

        private static string Users(int first, int count, string? next) =>
            "{\"kind\":\"admin#directory#users\",\"etag\":\"\\\"e1\\\"\",\"users\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"kind\":\"admin#directory#user\",\"id\":\"1" + i.ToString("D9") + "\",\"primaryEmail\":\"user" + i + "@kafe.example.com\",\"name\":{\"givenName\":\"Ada\",\"familyName\":\"Lovelace " + i + "\"},\"isAdmin\":false,\"suspended\":false}")) + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";

        [Fact]
        public async Task Lists_are_read_page_by_page_by_googles_token_no_more_than_asked()
        {
            var google = new Google
            {
                Answer = (_, url) => url.Contains("pageToken=p%2B2", StringComparison.Ordinal) ? (HttpStatusCode.OK, Users(1001, 100, null))
                    : url.Contains("pageToken=p1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Users(501, 500, "p+2"))
                    : (HttpStatusCode.OK, Users(1, 500, "p1")),
            };

            var users = await Run(google, GoogleWorkspaceAdminConnector.UsersOperation, new() { ["max"] = "700" });
            Assert.Equal(700, users.Count); // no more than asked
            Assert.Equal(("POST", GoogleWorkspaceAdminConnector.DefaultTokenUrl, (string?)"grant_type=refresh_token&refresh_token=refresh-1&client_id=client-1&client_secret=secret-1"), (google.Requests[0].Method.Method, google.Requests[0].Url, google.Requests[0].Body));
            Assert.Equal(new[] { $"{Api}/users?customer=my_customer&maxResults=500", $"{Api}/users?customer=my_customer&maxResults=200&pageToken=p1" }, google.Requests.Skip(1).Select(request => request.Url)); // a page no bigger than what is left
            Assert.All(google.Requests.Skip(1), request => Assert.Equal("Bearer ya29.admin", request.Authorization));
            var first = users[0].Fields;
            Assert.Equal(("1000000001", "user1@kafe.example.com", "{\"givenName\":\"Ada\",\"familyName\":\"Lovelace 1\"}", false), ((string?)first["id"], (string?)first["primaryEmail"], (string?)first["name"], (bool?)first["isAdmin"]));
            Assert.False(first.ContainsKey("kind") || first.ContainsKey("etag"));
            google.Requests.Clear();
            Assert.Equal(1100, (await Run(google, null!, new() { ["max"] = "5000" })).Count); // no operation: the users - no token the end
            Assert.Equal($"{Api}/users?customer=my_customer&maxResults=500&pageToken=p%2B2", google.Requests[^1].Url);
            var again = new Google { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 2, "same")) };
            Assert.Equal(4, (await Run(again, GoogleWorkspaceAdminConnector.UsersOperation, new())).Count); // a token given twice the end
            var empty = new Google { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 0, "more")) };
            Assert.Empty(await Run(empty, GoogleWorkspaceAdminConnector.UsersOperation, new()));
            Assert.Equal(2, empty.Requests.Count); // the token, one page: an empty page the end
            var endless = new Google { Answer = (_, _) => (HttpStatusCode.OK, Users(1, 500, Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, GoogleWorkspaceAdminConnector.UsersOperation, new())).Count); // 1,000 at most when no max is given

            google.Requests.Clear();
            await Run(google, GoogleWorkspaceAdminConnector.UsersOperation, new() { ["domain"] = "kafe.example.com", ["query"] = "orgUnitPath=/Kafé & bröd", ["show_deleted"] = "True", ["order_by"] = "FAMILYNAME", ["sort_order"] = "descending", ["projection"] = "full", ["max"] = "5" });
            Assert.Equal($"{Api}/users?domain=kafe.example.com&query=orgUnitPath%3D%2FKaf%C3%A9%20%26%20br%C3%B6d&showDeleted=true&orderBy=familyName&sortOrder=DESCENDING&projection=full&maxResults=5", google.Requests[^1].Url);
            await Run(google, GoogleWorkspaceAdminConnector.UsersOperation, new() { ["customer"] = "C01abc23d", ["show_deleted"] = "false", ["max"] = "5" });
            Assert.Equal($"{Api}/users?customer=C01abc23d&maxResults=5", google.Requests[^1].Url);
            await Run(google, GoogleWorkspaceAdminConnector.GroupsOperation, new() { ["member"] = "ada@kafe.example.com", ["query"] = "email:fika*", ["sort_order"] = "Ascending" });
            Assert.Equal($"{Api}/groups?userKey=ada%40kafe.example.com&query=email%3Afika%2A&orderBy=email&sortOrder=ASCENDING&maxResults=200", google.Requests[^1].Url);
            await Run(google, GoogleWorkspaceAdminConnector.GroupsOperation, new() { ["max"] = "5" });
            Assert.Equal($"{Api}/groups?customer=my_customer&maxResults=5", google.Requests[^1].Url);
            await Run(google, GoogleWorkspaceAdminConnector.GroupMembersOperation, new() { ["group"] = "fika@kafe.example.com", ["roles"] = "Owner, manager" });
            Assert.Equal($"{Api}/groups/fika%40kafe.example.com/members?roles=OWNER%2CMANAGER&maxResults=200", google.Requests[^1].Url);
            await Run(google, GoogleWorkspaceAdminConnector.DevicesOperation, new() { ["query"] = "status:provisioned", ["org_unit_path"] = "/Kafé", ["include_child_units"] = "true", ["order_by"] = "serialnumber", ["sort_order"] = "ascending", ["projection"] = "basic", ["max"] = "10" });
            Assert.Equal($"{Api}/customer/my_customer/devices/chromeos?query=status%3Aprovisioned&orgUnitPath=%2FKaf%C3%A9&includeChildOrgunits=true&orderBy=serialNumber&sortOrder=ASCENDING&projection=BASIC&maxResults=10", google.Requests[^1].Url);
            var devices = new Google { Answer = (_, _) => (HttpStatusCode.OK, "{\"chromeosdevices\":[{\"deviceId\":\"d1\",\"serialNumber\":\"5CD1\",\"status\":\"ACTIVE\"}],\"nextPageToken\":\"\"}") };
            Assert.Equal("5CD1", (string?)Assert.Single(await Run(devices, GoogleWorkspaceAdminConnector.DevicesOperation, new() { ["customer"] = "C01abc23d" })).Fields["serialNumber"]); // an empty token the end
            Assert.Equal($"{Api}/customer/C01abc23d/devices/chromeos?maxResults=200", devices.Requests[^1].Url);
        }

        [Fact]
        public async Task Users_groups_and_devices_are_kept_what_is_not_given_with_them()
        {
            var google = new Google
            {
                Answer = (method, url) => (method.Method, url) switch
                {
                    ("DELETE", _) => (HttpStatusCode.NoContent, ""),
                    ("POST", var u) when u.EndsWith("/action", StringComparison.Ordinal) => (HttpStatusCode.NoContent, ""),
                    (_, var u) when u.Contains("/members", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"kind\":\"admin#directory#member\",\"id\":\"1000000001\",\"email\":\"ada@kafe.example.com\",\"role\":\"MEMBER\",\"type\":\"USER\"}"),
                    (_, var u) when u.Contains("/groups", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"kind\":\"admin#directory#group\",\"id\":\"g1\",\"email\":\"fika@kafe.example.com\",\"name\":\"Fika\",\"directMembersCount\":\"3\"}"),
                    (_, var u) when u.Contains("/chromeos/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"kind\":\"admin#directory#chromeosdevice\",\"deviceId\":\"d1\",\"annotatedUser\":\"Ada\",\"status\":\"ACTIVE\"}"),
                    _ => (HttpStatusCode.OK, "{\"kind\":\"admin#directory#user\",\"id\":\"1000000001\",\"primaryEmail\":\"ada@kafe.example.com\",\"suspended\":true}"),
                },
            };
            (string, string, string?) Last() => (google.Requests[^1].Method.Method, google.Requests[^1].Url, google.Requests[^1].Body);

            var user = Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "ada@kafe.example.com", ["projection"] = "Full" })).Fields;
            Assert.Equal(("GET", $"{Api}/users/ada%40kafe.example.com?projection=full", (string?)null), Last());
            Assert.Equal(("ada@kafe.example.com", true), ((string?)user["primaryEmail"], (bool?)user["suspended"]));
            await Run(google, GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "1000000001" });
            Assert.Equal($"{Api}/users/1000000001", Last().Item2); // by id
            await Run(google, GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.com", ["first_name"] = "Åsa", ["last_name"] = "Öberg", ["primary_email"] = "asa@kafe.example.com", ["org_unit_path"] = "/Kafé/Kassa", ["suspended"] = "False", ["archived"] = "true", ["change_password_at_next_login"] = "true" });
            Assert.Equal(("PATCH", $"{Api}/users/ada%40kafe.example.com", (string?)"{\"name\":{\"givenName\":\"Åsa\",\"familyName\":\"Öberg\"},\"primaryEmail\":\"asa@kafe.example.com\",\"orgUnitPath\":\"/Kafé/Kassa\",\"suspended\":false,\"archived\":true,\"changePasswordAtNextLogin\":true}"), Last()); // a PATCH: the rest kept
            await Run(google, GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "1000000001", ["last_name"] = "Lovelace" });
            Assert.Equal("{\"name\":{\"familyName\":\"Lovelace\"}}", Last().Item3);
            var deleted = Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.DeleteUserOperation, new() { ["user"] = "ada@kafe.example.com" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/users/ada%40kafe.example.com", (string?)null), Last());
            Assert.Equal(("ada@kafe.example.com", true), ((string?)deleted["user"], (bool?)deleted["deleted"]));

            var member = Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["user"] = "ada@kafe.example.com", ["group"] = "fika@kafe.example.com" })).Fields;
            Assert.Equal(("POST", $"{Api}/groups/fika%40kafe.example.com/members", (string?)"{\"email\":\"ada@kafe.example.com\",\"role\":\"MEMBER\"}"), Last()); // a member by default
            Assert.Equal(("MEMBER", "USER"), ((string?)member["role"], (string?)member["type"]));
            await Run(google, GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["user"] = "1000000001", ["group"] = "g1", ["role"] = "Owner" });
            Assert.Equal(("POST", $"{Api}/groups/g1/members", (string?)"{\"id\":\"1000000001\",\"role\":\"OWNER\"}"), Last()); // an id as an id
            var removed = Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.RemoveFromGroupOperation, new() { ["user"] = "ada@kafe.example.com", ["group"] = "g1" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/groups/g1/members/ada%40kafe.example.com", (string?)null), Last());
            Assert.Equal(("g1", "ada@kafe.example.com", true), ((string?)removed["group"], (string?)removed["user"], (bool?)removed["removed"]));

            Assert.Equal("3", (string?)Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.GroupOperation, new() { ["group"] = "fika@kafe.example.com" })).Fields["directMembersCount"]);
            Assert.Equal(("GET", $"{Api}/groups/fika%40kafe.example.com", (string?)null), Last());
            await Run(google, GoogleWorkspaceAdminConnector.CreateGroupOperation, new() { ["email"] = "fika@kafe.example.com", ["name"] = "Fika & bröd", ["description"] = "Kafé" });
            Assert.Equal(("POST", $"{Api}/groups", (string?)"{\"email\":\"fika@kafe.example.com\",\"name\":\"Fika & bröd\",\"description\":\"Kafé\"}"), Last());
            await Run(google, GoogleWorkspaceAdminConnector.UpdateGroupOperation, new() { ["group"] = "g1", ["description"] = "Kassa" });
            Assert.Equal(("PATCH", $"{Api}/groups/g1", (string?)"{\"description\":\"Kassa\"}"), Last());
            Assert.True((bool?)Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.DeleteGroupOperation, new() { ["group"] = "g1" })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/groups/g1", (string?)null), Last());

            Assert.Equal("Ada", (string?)Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.DeviceOperation, new() { ["device"] = "d1", ["projection"] = "full" })).Fields["annotatedUser"]);
            Assert.Equal(("GET", $"{Api}/customer/my_customer/devices/chromeos/d1?projection=FULL", (string?)null), Last());
            await Run(google, GoogleWorkspaceAdminConnector.UpdateDeviceOperation, new() { ["device"] = "d1", ["annotated_user"] = "Åsa", ["annotated_location"] = "Kassa 2", ["annotated_asset_id"] = "K-17", ["notes"] = "Fika", ["org_unit_path"] = "/Kafé" });
            Assert.Equal(("PATCH", $"{Api}/customer/my_customer/devices/chromeos/d1", (string?)"{\"annotatedUser\":\"Åsa\",\"annotatedLocation\":\"Kassa 2\",\"annotatedAssetId\":\"K-17\",\"notes\":\"Fika\",\"orgUnitPath\":\"/Kafé\"}"), Last());
            var disabled = Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.DeviceActionOperation, new() { ["device"] = "d1", ["action"] = "Disable" })).Fields;
            Assert.Equal(("POST", $"{Api}/customer/my_customer/devices/chromeos/d1/action", (string?)"{\"action\":\"disable\"}"), Last());
            Assert.Equal(("d1", true), ((string?)disabled["device"], (bool?)disabled["disabled"]));
            Assert.True((bool?)Assert.Single(await Run(google, GoogleWorkspaceAdminConnector.DeviceActionOperation, new() { ["device"] = "d1", ["action"] = "reenable" })).Fields["reenabled"]);
            Assert.DoesNotContain(new GoogleWorkspaceAdminConnector().Operations, operation => operation.Name.Contains("create_user", StringComparison.Ordinal)); // never created: Google needs a password
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_googles_words_on_a_failure()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters))).Message;

            Assert.StartsWith("Google Workspace Admin has no operation 'create_user' - users, user, update_user,", await Refused("create_user", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.UserOperation, new()));
            Assert.Equal("'ada lovelace' is not the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.DeleteUserOperation, new() { ["user"] = "ada lovelace" }));
            Assert.Equal("'ada/../admin' is not the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "ada/../admin" }));
            Assert.Equal("'ada/../x@kafe.example.com' is not the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "ada/../x@kafe.example.com" }));
            Assert.Equal("Give the group's e-mail or id (group).", await Refused(GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["user"] = "ada@kafe.example.com" }));
            Assert.Equal("'fika group' is not the group's e-mail or id (group).", await Refused(GoogleWorkspaceAdminConnector.GroupOperation, new() { ["group"] = "fika group" }));
            Assert.Equal("Give the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["group"] = "g1" }));
            Assert.Equal("'ada lovelace' is not the user's e-mail or id (user).", await Refused(GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["group"] = "g1", ["user"] = "ada lovelace" }));
            Assert.Equal("'boss' is not member, manager or owner (role).", await Refused(GoogleWorkspaceAdminConnector.AddToGroupOperation, new() { ["group"] = "g1", ["user"] = "ada@kafe.example.com", ["role"] = "boss" }));
            Assert.Equal("Give a customer or a domain - one of them.", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["customer"] = "my_customer", ["domain"] = "kafe.example.com" }));
            Assert.Equal("Give a customer, a domain or a member - one of them.", await Refused(GoogleWorkspaceAdminConnector.GroupsOperation, new() { ["domain"] = "kafe.example.com", ["member"] = "ada@kafe.example.com" }));
            Assert.Equal("'kafe' is not a domain - kafe.example.com (domain).", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["domain"] = "kafe" }));
            Assert.Equal("'my customer' is not a customer's id - my_customer or C01abc23d (customer).", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["customer"] = "my customer" }));
            Assert.Equal("'my customer' is not a customer's id - my_customer or C01abc23d (customer).", await Refused(GoogleWorkspaceAdminConnector.DevicesOperation, new() { ["customer"] = "my customer" }));
            Assert.Equal("'ada lovelace' is not a user's e-mail or id (member).", await Refused(GoogleWorkspaceAdminConnector.GroupsOperation, new() { ["member"] = "ada lovelace" }));
            Assert.Equal("'name' is not email, familyName or givenName (order_by).", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["order_by"] = "name" }));
            Assert.Equal("'up' is not ascending or descending (sort_order).", await Refused(GoogleWorkspaceAdminConnector.GroupsOperation, new() { ["sort_order"] = "up" }));
            Assert.Equal("'custom' is not basic or full (projection).", await Refused(GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "ada@kafe.example.com", ["projection"] = "custom" }));
            Assert.Equal("'yes' is not true or false (show_deleted).", await Refused(GoogleWorkspaceAdminConnector.UsersOperation, new() { ["show_deleted"] = "yes" }));
            Assert.Equal("Give what to change - a name, a primary e-mail, an organizational unit, suspended, archived or a new password at next sign-in.", await Refused(GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.com" }));
            Assert.Equal("'asa@kafe' is not an e-mail (primary_email).", await Refused(GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.com", ["primary_email"] = "asa@kafe" }));
            Assert.Equal("'Kafé/Kassa' is not an organizational unit's path - /Kafé/Kassa (org_unit_path).", await Refused(GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.com", ["org_unit_path"] = "Kafé/Kassa" }));
            Assert.Equal("'maybe' is not true or false (suspended).", await Refused(GoogleWorkspaceAdminConnector.UpdateUserOperation, new() { ["user"] = "ada@kafe.example.com", ["suspended"] = "maybe" }));
            Assert.Equal("Give the group's e-mail (email).", await Refused(GoogleWorkspaceAdminConnector.CreateGroupOperation, new() { ["name"] = "Fika" }));
            Assert.Equal("'fika,kassa@kafe.example.com' is not an e-mail (email).", await Refused(GoogleWorkspaceAdminConnector.CreateGroupOperation, new() { ["email"] = "fika,kassa@kafe.example.com" }));
            Assert.Equal("Give what to change - its e-mail, name or description.", await Refused(GoogleWorkspaceAdminConnector.UpdateGroupOperation, new() { ["group"] = "g1" }));
            Assert.Equal("'guest' is not member, manager or owner (roles).", await Refused(GoogleWorkspaceAdminConnector.GroupMembersOperation, new() { ["group"] = "g1", ["roles"] = "member, guest" }));
            Assert.Equal("'model' is not annotatedLocation, annotatedUser, lastSync, notes, serialNumber or status (order_by).", await Refused(GoogleWorkspaceAdminConnector.DevicesOperation, new() { ["order_by"] = "model" }));
            Assert.Equal("Give the device's id (device).", await Refused(GoogleWorkspaceAdminConnector.DeviceOperation, new()));
            Assert.Equal("'d1/../x' is not a device's id (device).", await Refused(GoogleWorkspaceAdminConnector.UpdateDeviceOperation, new() { ["device"] = "d1/../x", ["notes"] = "Fika" }));
            Assert.Equal("Give the action - disable or reenable (action).", await Refused(GoogleWorkspaceAdminConnector.DeviceActionOperation, new() { ["device"] = "d1" }));
            Assert.Equal("'deprovision' is not disable or reenable (action).", await Refused(GoogleWorkspaceAdminConnector.DeviceActionOperation, new() { ["device"] = "d1", ["action"] = "deprovision" }));
            Assert.Equal("Give what to change - its user, location, asset id, notes or organizational unit.", await Refused(GoogleWorkspaceAdminConnector.UpdateDeviceOperation, new() { ["device"] = "d1" }));
            Assert.Empty(google.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Google { Answer = (_, _) => (status, answer) }, GoogleWorkspaceAdminConnector.UserOperation, new() { ["user"] = "ada@kafe.example.com" }))).Message;
            Assert.Equal("HTTP 404 NOT_FOUND: Resource Not Found: userKey", await Failed(HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"Resource Not Found: userKey\",\"status\":\"NOT_FOUND\"}}"));
            Assert.Equal("HTTP 403 PERMISSION_DENIED: " + new string('e', 300), await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"" + new string('e', 400) + "\",\"status\":\"PERMISSION_DENIED\"}}")); // a long one cut
            Assert.Equal("Google answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_workspaces_domain()
        {
            Task<ConnectionTestResult> Test(Google google) =>
                new GoogleWorkspaceAdminConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(google), "", null, Credentials));
            var google = new Google { Answer = (_, _) => (HttpStatusCode.OK, "{\"kind\":\"admin#directory#customer\",\"id\":\"C01abc23d\",\"customerDomain\":\"kafe.example.com\"}") };
            var tested = await Test(google);
            Assert.Equal((true, "Google Workspace: kafe.example.com."), (tested.Success, tested.Message));
            Assert.Equal(new[] { GoogleWorkspaceAdminConnector.DefaultTokenUrl, $"{Api}/customers/my_customer" }, google.Requests.Select(request => request.Url));
            Assert.Equal("Google Workspace: the administrator's Workspace.", (await Test(new Google())).Message);
            var refused = await Test(new Google { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"Not Authorized to access this resource/api\",\"status\":\"PERMISSION_DENIED\"}}") });
            Assert.Equal((false, "HTTP 403 PERMISSION_DENIED: Not Authorized to access this resource/api"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googleworkspaceadmin"), (new GoogleWorkspaceAdminConnector().AuthType, new GoogleWorkspaceAdminConnector().ConnectorType));
            Assert.Equal(16, new GoogleWorkspaceAdminConnector().Operations.Count);
        }
    }
}
