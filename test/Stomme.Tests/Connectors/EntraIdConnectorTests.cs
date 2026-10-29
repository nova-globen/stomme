#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.EntraId;
using Stomme.Modules.Connectors.Shared;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The EntraIdConnectorTests cases (8), kept by name over the port; a user got, updated, deleted and
    /// kept in groups, a group read, got, created, updated and deleted, users and groups read no further than asked.</summary>
    public sealed class EntraIdConnectorTests
    {
        private const string UsersPageOneJson = """
            {"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$top=999&$skiptoken=u2",
             "value":[{"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.com","mail":"ada@contoso.com","accountEnabled":true,"userType":"Member","createdDateTime":"2024-01-01T00:00:00Z"}]}
            """;

        private const string UsersPageTwoJson = """
            {"value":[{"id":"u2","displayName":"Grace","userPrincipalName":"grace@contoso.com","mail":null,"accountEnabled":false,"userType":"Guest","createdDateTime":"2025-05-05T00:00:00Z"}]}
            """;

        private const string DevicesPageJson = """
            {"value":[{"id":"dev1","displayName":"LAPTOP-1","deviceId":"aaaa-bbbb","operatingSystem":"Windows","operatingSystemVersion":"10.0.26100","accountEnabled":true,"trustType":"AzureAd","approximateLastSignInDateTime":"2026-07-01T12:00:00Z"}]}
            """;

        private static RecordingHandler Handler(Func<HttpRequestMessage, int, HttpResponseMessage> graph) =>
            new(graph) { TokenRoute = request => request.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) };

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal)
        {
            ["token-url"] = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token",
            ["client-id"] = "client",
            ["client-secret"] = "secret",
        };

        private static ConnectorExtractContext Extract(RecordingHandler handler, string operation, Dictionary<string, string>? parameters = null) =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, credentials: Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_IsARealDirectoryConnector()
        {
            var connector = new EntraIdConnector();

            Assert.Equal(("entra", AuthenticationType.OAuth2ClientCredentials), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "users", "devices", "user", "update_user", "delete_user", "add_to_group", "remove_from_group", "groups", "group", "create_group", "update_group", "delete_group" }, connector.Operations.Select(o => o.Name));
            Assert.Equal(("https://graph.microsoft.com", "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token"), (connector.EndpointExample, connector.CredentialLabels!["token-url"]));
        }

        [Fact]
        public async Task Extract_Users_AppliesDefaultProjectionAndPages()
        {
            var handler = Handler((_, call) => Json(call == 0 ? UsersPageOneJson : UsersPageTwoJson));

            var records = await Contexts.DrainAsync(new EntraIdConnector(), Extract(handler, "users"));

            Assert.Equal(2, records.Count);
            Assert.Equal("Ada", records[0].Fields["displayName"]);
            Assert.Equal(true, records[0].Fields["accountEnabled"]);
            Assert.Null(records[1].Fields["mail"]);

            var graph = handler.Requests.ToList();
            Assert.Equal(2, graph.Count);
            Assert.StartsWith("https://graph.microsoft.com/v1.0/users?", graph[0].RequestUri!.AbsoluteUri);
            Assert.Contains(Uri.EscapeDataString(EntraIdConnector.DefaultUserColumns), graph[0].RequestUri!.AbsoluteUri);
            Assert.Contains("$skiptoken=u2", graph[1].RequestUri!.AbsoluteUri);
        }

        // Graph's next link followed only while it stays on the collection: one to another host refused, nothing sent there.
        [Fact]
        public async Task Extract_NextLinkToAnotherHost_IsRefusedWithNothingSentThere()
        {
            var handler = Handler((_, _) => Json(UsersPageOneJson.Replace("https://graph.microsoft.com/", "https://elsewhere.example.test/", StringComparison.Ordinal)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new EntraIdConnector(), Extract(handler, "users")));

            Assert.Equal("Microsoft Graph named a next page outside the collection - it is not followed.", ex.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Extract_Devices_UsesDeviceProjection()
        {
            var handler = Handler((_, _) => Json(DevicesPageJson));

            var records = await Contexts.DrainAsync(new EntraIdConnector(), Extract(handler, "devices"));

            var record = Assert.Single(records);
            Assert.Equal(("LAPTOP-1", "AzureAd"), (record.Fields["displayName"], record.Fields["trustType"]));
            var url = Assert.Single(handler.Requests).RequestUri!.AbsoluteUri;
            Assert.StartsWith("https://graph.microsoft.com/v1.0/devices?", url);
            Assert.Contains(Uri.EscapeDataString(EntraIdConnector.DefaultDeviceColumns), url);
        }

        [Fact]
        public async Task Extract_ExplicitSelectAndFilter_OverrideDefaults()
        {
            var handler = Handler((_, _) => Json(UsersPageTwoJson));

            await Contexts.DrainAsync(new EntraIdConnector(), Extract(handler, "users", new(StringComparer.Ordinal) { ["select"] = "id,mail", ["filter"] = "accountEnabled eq true" }));

            var url = Assert.Single(handler.Requests).RequestUri!.AbsoluteUri;
            Assert.Contains("$select=id%2Cmail", url);
            Assert.DoesNotContain("displayName", url);
            Assert.Contains("$filter=accountEnabled%20eq%20true", url);
        }

        [Fact]
        public async Task Extract_EmptyOperation_DefaultsToUsers()
        {
            var handler = Handler((_, _) => Json(UsersPageTwoJson));

            await Contexts.DrainAsync(new EntraIdConnector(), Extract(handler, string.Empty));

            Assert.StartsWith("https://graph.microsoft.com/v1.0/users?", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new EntraIdConnector(), Extract(Handler((_, _) => Json(UsersPageOneJson)), "alerts")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ProbesUsersAndSucceeds()
        {
            var handler = Handler((_, _) => Json("""{"value":[]}"""));

            var result = await new EntraIdConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("/v1.0/users?$top=1", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task TestConnection_Forbidden_MentionsApplicationPermission()
        {
            var handler = Handler((_, _) => Json("""{"error":{}}""", HttpStatusCode.Forbidden));

            var result = await new EntraIdConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("User.Read.All", result.Message);
        }

        private const string GroupId = "02bd9fd6-8f93-4758-87c3-1fb73740a315";

        private const string UserId = "87d349ed-44d7-43e1-9a83-5f2406dee5bd";

        private const string Api = "https://graph.microsoft.com/v1.0";

        private static Task<List<ConnectorRecord>> Run(RecordingHandler handler, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new EntraIdConnector(new OAuth2TokenProvider()), Extract(handler, operation, parameters));

        private static (string Method, string Url, string Body) Sent(RecordingHandler handler, int index = -1)
        {
            var call = handler.Calls[index < 0 ? handler.Calls.Count + index : index];
            return (call.Request.Method.Method, call.Request.RequestUri!.AbsoluteUri, call.Body);
        }

        // Users and groups read no further than asked, the page no larger than it.
        [Fact]
        public async Task Users_and_groups_are_read_no_further_than_asked()
        {
            var handler = Handler((_, call) => Json(call == 0 ? UsersPageOneJson : UsersPageTwoJson));
            Assert.Single(await Run(handler, "users", new() { ["max"] = "1" }));
            Assert.StartsWith($"{Api}/users?$top=1&", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri); // one page, of one

            var groups = Handler((_, _) => Json("""{"value":[{"id":"02bd9fd6-8f93-4758-87c3-1fb73740a315","displayName":"Kassan","groupTypes":["Unified"]}]}"""));
            var group = Assert.Single(await Run(groups, "groups", new() { ["filter"] = "startswith(displayName,'Kass')", ["max"] = "5000" })).Fields;
            Assert.Equal($"{Api}/groups?$top=999&$select={Uri.EscapeDataString(EntraIdConnector.DefaultGroupColumns)}&$filter=startswith%28displayName%2C%27Kass%27%29", Assert.Single(groups.Requests).RequestUri!.AbsoluteUri);
            Assert.Equal(("Kassan", "[\"Unified\"]"), ((string?)group["displayName"], (string?)group["groupTypes"]));
        }

        // A user got, updated - the properties Graph keeps apart in a PATCH of their own - and deleted, added to and
        // removed from a group.
        [Fact]
        public async Task A_user_is_got_updated_deleted_and_kept_in_groups()
        {
            var handler = Handler((request, _) => request.Method == HttpMethod.Get
                ? Json("""{"@odata.context":"x","id":"87d349ed-44d7-43e1-9a83-5f2406dee5bd","displayName":"Ada Lovelace","jobTitle":"Barista","businessPhones":["+46 8 123"]}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent));

            var user = Assert.Single(await Run(handler, "user", new() { ["user"] = "ada@kafe.onmicrosoft.com", ["select"] = "id, displayName,jobTitle,businessPhones" })).Fields;
            Assert.Equal(("GET", $"{Api}/users/ada%40kafe.onmicrosoft.com?$select=id%2CdisplayName%2CjobTitle%2CbusinessPhones", ""), Sent(handler));
            Assert.Equal(("Ada Lovelace", "[\"+46 8 123\"]", false), ((string?)user["displayName"], (string?)user["businessPhones"], user.ContainsKey("@odata.context")));
            Assert.Equal("Bearer tok-1", handler.Calls[^1].Request.Headers.Authorization!.ToString());
            await Run(handler, "user", new() { ["user"] = UserId });
            Assert.Equal($"{Api}/users/{UserId}?$select={Uri.EscapeDataString(EntraIdConnector.DefaultUserColumns)}", Sent(handler).Url);

            handler.Calls.Clear();
            var updated = Assert.Single(await Run(handler, "update_user", new() { ["user"] = UserId, ["values"] = """{"jobTitle": "Barista på Kafé", "accountEnabled": false, "aboutMe": "Fika", "skills": ["latte"]}""" })).Fields;
            Assert.Equal(new[]
            {
                ("PATCH", $"{Api}/users/{UserId}", """{"jobTitle":"Barista på Kafé","accountEnabled":false}"""),
                ("PATCH", $"{Api}/users/{UserId}", """{"aboutMe":"Fika","skills":["latte"]}"""), // what Graph keeps apart, in a request of its own
            }, new[] { Sent(handler, 0), Sent(handler, 1) });
            Assert.Equal(((object?)UserId, (object?)true), (updated["id"], updated["updated"]));
            handler.Calls.Clear();
            await Run(handler, "update_user", new() { ["user"] = UserId, ["values"] = """{"department": "Kassa"}""" });
            Assert.Single(handler.Calls); // nothing apart: one PATCH
            Assert.Equal(("PATCH", $"{Api}/users/{UserId}", """{"department":"Kassa"}"""), Sent(handler));

            var deleted = Assert.Single(await Run(handler, "delete_user", new() { ["user"] = UserId })).Fields;
            Assert.Equal(("DELETE", $"{Api}/users/{UserId}", ""), Sent(handler));
            Assert.Equal((object?)true, deleted["deleted"]);

            var added = Assert.Single(await Run(handler, "Add_To_Group", new() { ["user"] = "ada@kafe.onmicrosoft.com", ["group"] = GroupId })).Fields;
            Assert.Equal(("POST", $"{Api}/groups/{GroupId}/members/$ref", "{\"@odata.id\":\"" + Api + "/directoryObjects/ada%40kafe.onmicrosoft.com\"}"), Sent(handler));
            Assert.Equal(((object?)"ada@kafe.onmicrosoft.com", (object?)GroupId, (object?)true), (added["user"], added["group"], added["added"]));
            var removed = Assert.Single(await Run(handler, "remove_from_group", new() { ["user"] = UserId, ["group"] = GroupId })).Fields;
            Assert.Equal(("DELETE", $"{Api}/groups/{GroupId}/members/{UserId}/$ref", ""), Sent(handler));
            Assert.Equal((object?)true, removed["removed"]);
        }

        // A group got with its members, created as a security or a Microsoft 365 group, assigned or dynamic, updated and
        // deleted.
        [Fact]
        public async Task A_group_is_got_created_updated_and_deleted()
        {
            const string Group = """{"@odata.context":"x","id":"02bd9fd6-8f93-4758-87c3-1fb73740a315","displayName":"Kassan","members":[{"id":"u1","displayName":"Ada"}]}""";
            var handler = Handler((request, _) => request.Method == HttpMethod.Post ? Json(Group, HttpStatusCode.Created) : request.Method == HttpMethod.Get ? Json(Group) : new HttpResponseMessage(HttpStatusCode.NoContent));

            var group = Assert.Single(await Run(handler, "group", new() { ["group"] = GroupId, ["include_members"] = "True" })).Fields;
            Assert.Equal($"{Api}/groups/{GroupId}?$select={Uri.EscapeDataString(EntraIdConnector.DefaultGroupColumns)}&$expand={Uri.EscapeDataString("members($select=id,displayName,userPrincipalName,mail)")}", Sent(handler).Url);
            Assert.Equal("[{\"id\":\"u1\",\"displayName\":\"Ada\"}]", group["members"]);
            await Run(handler, "group", new() { ["group"] = GroupId, ["select"] = "id,displayName" });
            Assert.Equal($"{Api}/groups/{GroupId}?$select=id%2CdisplayName", Sent(handler).Url);

            var created = Assert.Single(await Run(handler, "create_group", new() { ["display_name"] = "Kassan", ["mail_nickname"] = "kassan", ["description"] = "Alla på kassan" })).Fields;
            Assert.Equal(("POST", $"{Api}/groups", """{"groupTypes":[],"mailEnabled":false,"securityEnabled":true,"displayName":"Kassan","mailNickname":"kassan","description":"Alla på kassan"}"""), Sent(handler));
            Assert.Equal("Kassan", created["displayName"]);
            await Run(handler, "create_group", new()
            {
                ["kind"] = "Microsoft365", ["membership"] = "dynamic", ["display_name"] = "Baristor", ["mail_nickname"] = "baristor", ["membership_rule"] = "(user.jobTitle -eq \"Barista\")",
                ["rule_processing"] = "paused", ["visibility"] = "private", ["assignable_to_role"] = "true", ["security_enabled"] = "true",
            });
            Assert.Equal("""{"groupTypes":["Unified","DynamicMembership"],"mailEnabled":true,"securityEnabled":true,"isAssignableToRole":true,"displayName":"Baristor","mailNickname":"baristor","membershipRule":"(user.jobTitle -eq \"Barista\")","membershipRuleProcessingState":"Paused","visibility":"Private"}""", Sent(handler).Body);
            await Run(handler, "create_group", new() { ["kind"] = "microsoft365", ["display_name"] = "Nyheter", ["mail_nickname"] = "nyheter", ["mail_enabled"] = "false" });
            Assert.Equal("""{"groupTypes":["Unified"],"mailEnabled":false,"securityEnabled":false,"displayName":"Nyheter","mailNickname":"nyheter"}""", Sent(handler).Body);

            var updated = Assert.Single(await Run(handler, "update_group", new() { ["group"] = GroupId, ["description"] = "Kassa och kafé", ["visibility"] = "Public", ["security_enabled"] = "false" })).Fields;
            Assert.Equal(("PATCH", $"{Api}/groups/{GroupId}", """{"securityEnabled":false,"description":"Kassa och kafé","visibility":"Public"}"""), Sent(handler));
            Assert.Equal(((object?)GroupId, (object?)true), (updated["id"], updated["updated"]));
            Assert.Equal((object?)true, Assert.Single(await Run(handler, "delete_group", new() { ["group"] = GroupId })).Fields["deleted"]);
            Assert.Equal(("DELETE", $"{Api}/groups/{GroupId}", ""), Sent(handler));
        }

        // What could go wrong refused before a token is asked for; Graph's code and message on a failure.
        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_graphs_words_on_a_failure()
        {
            var handler = Handler((_, _) => Json("""{"value":[]}"""));
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(handler, operation, parameters))).Message;
            var user = new Dictionary<string, string> { ["user"] = UserId };
            Dictionary<string, string> Group(params (string Name, string Value)[] more) => new[] { ("group", GroupId) }.Concat(more).ToDictionary(pair => pair.Item1, pair => pair.Item2);
            Dictionary<string, string> NewGroup(params (string Name, string Value)[] more) => new[] { ("display_name", "Kassan"), ("mail_nickname", "kassan") }.Concat(more).ToDictionary(pair => pair.Item1, pair => pair.Item2);

            Assert.Equal("Unknown operation 'alerts' for the Entra connector - users, devices, user, update_user, delete_user, add_to_group, remove_from_group, groups, group, create_group, update_group or delete_group.", await Refused("alerts", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("users", new() { ["max"] = "0" }));
            Assert.Equal("Give the user - its object id or user principal name (user).", await Refused("user", new()));
            Assert.Equal("'ada' is not a user's object id or user principal name (user).", await Refused("delete_user", new() { ["user"] = "ada" }));
            Assert.Equal("'../groups/x' is not a user's object id or user principal name (user).", await Refused("user", new() { ["user"] = "../groups/x" }));
            Assert.Equal("'../ada@kafe.onmicrosoft.com' is not a user's object id or user principal name (user).", await Refused("user", new() { ["user"] = "../ada@kafe.onmicrosoft.com" }));
            Assert.Equal("'zzzzzzzz-8f93-4758-87c3-1fb73740a315' is not a group's object id (group).", await Refused("delete_group", new() { ["group"] = "zzzzzzzz-8f93-4758-87c3-1fb73740a315" }));
            Assert.Equal("'02bd9fd6-8f93-4758-87c3-1fb73740a315/members' is not a group's object id (group).", await Refused("delete_group", new() { ["group"] = "02bd9fd6-8f93-4758-87c3-1fb73740a315/members" }));
            Assert.Equal("Give the group - its object id (group).", await Refused("add_to_group", user));
            Assert.Equal("'kassan@kafe.onmicrosoft.com' is not a group's object id (group).", await Refused("remove_from_group", new() { ["user"] = UserId, ["group"] = "kassan@kafe.onmicrosoft.com" }));
            Assert.Equal("'id;displayName' is not a comma-separated list of properties (select).", await Refused("user", new() { ["user"] = UserId, ["select"] = "id;displayName" }));
            Assert.Equal("Give what to change - a JSON object of Graph's user properties (values).", await Refused("update_user", user));
            const string NotAnObject = "The values are not a JSON object of Graph's user properties - {\"jobTitle\": \"Barista\"} (values).";
            Assert.Equal(NotAnObject, await Refused("update_user", new() { ["user"] = UserId, ["values"] = "jobTitle = Barista" }));
            Assert.Equal(NotAnObject, await Refused("update_user", new() { ["user"] = UserId, ["values"] = "{}" }));
            Assert.Equal(NotAnObject, await Refused("update_user", new() { ["user"] = UserId, ["values"] = "[{\"jobTitle\": \"Barista\"}]" }));
            Assert.Equal("'job title' is not a property's name - jobTitle, accountEnabled (values).", await Refused("update_user", new() { ["user"] = UserId, ["values"] = "{\"job title\": \"Barista\"}" }));
            Assert.Equal("'@odata.type' is not a property's name - jobTitle, accountEnabled (values).", await Refused("update_user", new() { ["user"] = UserId, ["values"] = "{\"@odata.type\": \"x\"}" }));
            Assert.Equal("'passwordProfile' is a password's - a password is never a step's parameter: set it in Entra ID (values).", await Refused("update_user", new() { ["user"] = UserId, ["values"] = "{\"passwordProfile\": {\"password\": \"x\"}}" }));
            Assert.Equal("'passwordPolicies' is a password's - a password is never a step's parameter: set it in Entra ID (values).", await Refused("update_user", new() { ["user"] = UserId, ["values"] = "{\"passwordPolicies\": \"DisablePasswordExpiration\"}" }));
            Assert.Equal("'yes' is not true or false (include_members).", await Refused("group", Group(("include_members", "yes"))));
            Assert.Equal("Give the group's name (display_name).", await Refused("create_group", new() { ["mail_nickname"] = "kassan" }));
            Assert.Equal("Give the group's mail nickname (mail_nickname).", await Refused("create_group", new() { ["display_name"] = "Kassan" }));
            Assert.Equal("'" + new string('a', 257) + "' is not a text of 256 characters at most, without control characters (display_name).", await Refused("update_group", Group(("display_name", new string('a', 257)))));
            Assert.Equal("'Kafé\u0001' is not a text of 1,024 characters at most, without control characters (description).", await Refused("update_group", Group(("description", "Kafé\u0001"))));
            Assert.Equal("'kassan@kafe.onmicrosoft.com' is not a mail nickname - the part before the @, 64 characters at most (mail_nickname).", await Refused("create_group", new() { ["display_name"] = "Kassan", ["mail_nickname"] = "kassan@kafe.onmicrosoft.com" }));
            Assert.Equal("'teams' is not security or microsoft365 (kind).", await Refused("create_group", NewGroup(("kind", "teams"))));
            Assert.Equal("'static' is not assigned or dynamic (membership).", await Refused("create_group", NewGroup(("membership", "static"))));
            Assert.Equal("Give a dynamic group's membership rule (membership_rule).", await Refused("create_group", NewGroup(("membership", "dynamic"))));
            Assert.Equal("A security group is not mail-enabled - make it a microsoft365 group (mail_enabled).", await Refused("create_group", NewGroup(("mail_enabled", "true"))));
            Assert.Equal("'Running' is not On or Paused (rule_processing).", await Refused("update_group", Group(("rule_processing", "Running"))));
            Assert.Equal("'Secret' is not Public, Private or HiddenMembership (visibility).", await Refused("update_group", Group(("visibility", "Secret"))));
            Assert.Equal("Give what to change - its name, mail nickname, description, membership rule, rule processing, visibility or security.", await Refused("update_group", Group()));
            Assert.Empty(handler.Calls); // nothing sent
            Assert.Empty(handler.TokenForms); // not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "delete_group") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Handler((_, _) => Json(answer, status)), operation, Group()))).Message;
            Assert.Equal("HTTP 403 Authorization_RequestDenied: Insufficient privileges to complete the operation.", await Failed(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}"""));
            Assert.Equal("HTTP 404 Request_ResourceNotFound", await Failed(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound"}}""", "group"));
            Assert.Equal("HTTP 400 Request_BadRequest: " + new string('e', 300 - "Request_BadRequest: ".Length), await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"" + new string('e', 400) + "\"}}")); // a long one cut
            Assert.Equal("Microsoft Graph answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Sign in</body></html>", "group"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("Microsoft Graph answered with nothing.", await Failed(HttpStatusCode.OK, "", "group"));
        }
    }
}
