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
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.AwsIam;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// n8n's AWS IAM node over IAM's Query protocol, each call a form signed with SigV4 for iam in us-east-1 at IAM's
    /// global address: every name, path, ARN and tag held to IAM's shape before anything is signed; users listed by a path prefix
    /// page by page by IAM's Marker up to the most; a user read, created with its path, boundary and tags, renamed and moved, put
    /// in a group and taken out, deleted after leaving its groups; groups with their users, made, renamed and deleted after their
    /// users leave; AWS's error code and message; a test naming the account's alias.
    /// </summary>
    public sealed class AwsIamConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private const string Api = "https://iam.amazonaws.com/";
        private const string Ns = "xmlns=\"https://iam.amazonaws.com/doc/2010-05-08/\"";

        private sealed record Sent(string Url, string? Authorization, string? ContentType, Dictionary<string, string> Form);

        private sealed class Iam(Func<Sent, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
                var sent = new Sent(request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var signed) ? signed.Single() : null, request.Content?.Headers.ContentType?.MediaType, form);
                Requests.Add(sent);
                var (status, xml) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
            }
        }

        private static string Result(string action, string inner) => $"<{action}Response {Ns}><{action}Result>{inner}</{action}Result><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></{action}Response>";

        private static string User(string name, string path = "/") => $"<UserName>{name}</UserName><UserId>AIDA{name.ToUpperInvariant().Replace("-", "")}</UserId><Arn>arn:aws:iam::123456789012:user{path}{name}</Arn><Path>{path}</Path><CreateDate>2026-10-18T08:00:00Z</CreateDate>";

        private static Task<List<ConnectorRecord>> Run(Iam iam, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new AwsIamConnector(), Contexts.Extract(new StubEgress(iam), "", operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_aws_iam_node()
        {
            IConnector connector = new AwsIamConnector();
            Assert.Equal(("awsiam", "AWS IAM", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "users", "user", "user_create", "user_update", "user_delete", "user_add_to_group", "user_remove_from_group", "groups", "group", "group_create", "group_update", "group_delete" },
                ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.DoesNotContain(((IConnectorExtractor)connector).Operations.SelectMany(operation => operation.Parameters?.Fields ?? Array.Empty<ConnectorConfigField>()), field => field.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("key", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var iam = new Iam(_ => (HttpStatusCode.OK, "<x/>"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(iam, operation, parameters, settings));
                Assert.Empty(iam.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("AWS IAM has no operation 'roles' - users, user, user_create", await Refused("roles", new()), StringComparison.Ordinal);
            Assert.Equal("Give a user's name (user).", await Refused("user", new()));
            Assert.Equal("'kassa ada' is not a user's name - letters, digits and + = , . @ _ -, at most 64 (user).", await Refused("user_delete", new() { ["user"] = "kassa ada" }));
            Assert.Equal("'" + new string('a', 65) + "' is not a user's name - letters, digits and + = , . @ _ -, at most 64 (user).", await Refused("user_create", new() { ["user"] = new string('a', 65) }));
            Assert.Equal("Give a group's name (group).", await Refused("user_add_to_group", new() { ["user"] = "ada" }));
            Assert.Equal("'kassa/1' is not a group's name - letters, digits and + = , . @ _ -, at most 128 (group).", await Refused("group_create", new() { ["group"] = "kassa/1" }));
            Assert.Equal("'kassa/' is not a path - /kassa/, starting and ending with / (path).", await Refused("user_create", new() { ["user"] = "ada", ["path"] = "kassa/" }));
            Assert.Equal("'/kassa' is not a path - /kassa/, starting and ending with / (path_prefix).", await Refused("users", new() { ["path_prefix"] = "/kassa" }));
            Assert.Equal("'arn:aws:iam::123456789012:role/Kassa' is not a managed policy's ARN - arn:aws:iam::123456789012:policy/KassaBoundary (permissions_boundary).", await Refused("user_create", new() { ["user"] = "ada", ["permissions_boundary"] = "arn:aws:iam::123456789012:role/Kassa" }));
            Assert.Equal("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).", await Refused("user_create", new() { ["user"] = "ada", ["tags"] = "[\"kassa\"]" }));
            Assert.Equal("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).", await Refused("user_create", new() { ["user"] = "ada", ["tags"] = "{" + string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"k{i}\": \"v\"")) + "}" }));
            Assert.Equal("A tag's key is 1 to 128 characters and its value at most 256 ('team', tags).", await Refused("user_create", new() { ["user"] = "ada", ["tags"] = "{\"team\": \"" + new string('k', 257) + "\"}" }));
            Assert.Equal("Give what changes - a new name or a new path (new_name, new_path).", await Refused("user_update", new() { ["user"] = "ada" }));
            Assert.Equal("Give what changes - a new name or a new path (new_name, new_path).", await Refused("group_update", new() { ["group"] = "kassa" }));
            Assert.Equal("'ada lovelace' is not a user's name - letters, digits and + = , . @ _ -, at most 64 (new_name).", await Refused("user_update", new() { ["user"] = "ada", ["new_name"] = "ada lovelace" }));
            Assert.Equal("'maybe' is not true or false (include_users).", await Refused("groups", new() { ["include_users"] = "maybe" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("users", new() { ["max"] = "0" }));
            Assert.Equal("'eu-west' is not an AWS region - us-east-1 (region).", await Refused("users", new(), new Dictionary<string, string> { ["region"] = "eu-west" }));
        }

        [Fact]
        public async Task Users_are_listed_page_by_page_by_iams_marker_and_one_read()
        {
            string Page(int from, int count, string? marker) => Result("ListUsers", "<Users>" + string.Concat(Enumerable.Range(from, count).Select(i => "<member>" + User("kassa-" + i, "/kassa/") + "</member>")) + "</Users>" + (marker is null ? "<IsTruncated>false</IsTruncated>" : $"<IsTruncated>true</IsTruncated><Marker>{marker}</Marker>"));
            var iam = new Iam(sent => sent.Form["Action"] switch
            {
                "ListUsers" => (HttpStatusCode.OK, sent.Form.TryGetValue("Marker", out var marker) && marker == "m2" ? Page(1000, 3, null) : Page(0, 1000, "m2")),
                "GetUser" => (HttpStatusCode.OK, Result("GetUser", "<User>" + User("ada") + "<Tags><member><Key>team</Key><Value>kassa</Value></member></Tags></User>")),
                _ => (HttpStatusCode.OK, "<x/>"),
            });
            var all = await Run(iam, "users", new() { ["path_prefix"] = "/kassa/", ["max"] = "2000" });
            Assert.Equal(1003, all.Count);
            Assert.Equal(new Dictionary<string, object?> { ["UserName"] = "kassa-0", ["UserId"] = "AIDAKASSA0", ["Arn"] = "arn:aws:iam::123456789012:user/kassa/kassa-0", ["Path"] = "/kassa/", ["CreateDate"] = "2026-10-18T08:00:00Z" }, all[0].Fields);
            Assert.Equal(new[] { "Action=ListUsers Version=2010-05-08 PathPrefix=/kassa/ MaxItems=1000", "Action=ListUsers Version=2010-05-08 PathPrefix=/kassa/ MaxItems=1000 Marker=m2" }, iam.Requests.Select(sent => string.Join(" ", sent.Form.Select(pair => pair.Key + "=" + pair.Value))));
            Assert.All(iam.Requests, sent => Assert.Equal((Api, "application/x-www-form-urlencoded"), (sent.Url, sent.ContentType)));
            Assert.Contains("/us-east-1/iam/aws4_request", iam.Requests[0].Authorization, StringComparison.Ordinal);

            iam.Requests.Clear();
            Assert.Equal(5, (await Run(iam, "users", new() { ["max"] = "5" })).Count);
            Assert.Equal("5", iam.Requests.Single().Form["MaxItems"]); // a page no larger than what is wanted
            var ada = Assert.Single(await Run(iam, "user", new() { ["user"] = "ada" })).Fields;
            Assert.Equal(("ada", "[{\"Key\":\"team\",\"Value\":\"kassa\"}]"), ((string?)ada["UserName"], (string?)ada["Tags"]));
            await Run(iam, "users", new(), new Dictionary<string, string> { ["region"] = "us-gov-west-1" });
            Assert.Contains("/us-gov-west-1/iam/aws4_request", iam.Requests.Last().Authorization, StringComparison.Ordinal); // another partition's signing region
        }

        [Fact]
        public async Task A_user_is_created_changed_grouped_and_deleted_after_leaving_its_groups()
        {
            var iam = new Iam(sent => sent.Form["Action"] switch
            {
                "CreateUser" => (HttpStatusCode.OK, Result("CreateUser", "<User>" + User("ada", "/kassa/") + "</User>")),
                "ListGroupsForUser" => (HttpStatusCode.OK, Result("ListGroupsForUser", "<Groups><member><GroupName>kassa</GroupName></member><member><GroupName>kafe</GroupName></member></Groups><IsTruncated>false</IsTruncated>")),
                _ => (HttpStatusCode.OK, Result(sent.Form["Action"], string.Empty)),
            });
            var made = Assert.Single(await Run(iam, "user_create", new() { ["user"] = "ada", ["path"] = "/kassa/", ["permissions_boundary"] = "arn:aws:iam::123456789012:policy/KassaBoundary", ["tags"] = "{\"team\": \"kassa & kafe\", \"till\": 4}" })).Fields;
            Assert.Equal(("ada", "/kassa/"), ((string?)made["UserName"], (string?)made["Path"]));
            Assert.Equal(new Dictionary<string, string>
            {
                ["Action"] = "CreateUser", ["Version"] = "2010-05-08", ["UserName"] = "ada", ["Path"] = "/kassa/", ["PermissionsBoundary"] = "arn:aws:iam::123456789012:policy/KassaBoundary",
                ["Tags.member.1.Key"] = "team", ["Tags.member.1.Value"] = "kassa & kafe", ["Tags.member.2.Key"] = "till", ["Tags.member.2.Value"] = "4",
            }, iam.Requests.Single().Form);

            iam.Requests.Clear();
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["updated"] = true }, Assert.Single(await Run(iam, "user_update", new() { ["user"] = "ada", ["new_name"] = "ada.lovelace", ["new_path"] = "/kafe/" })).Fields);
            await Run(iam, "user_add_to_group", new() { ["user"] = "ada", ["group"] = "kassa" });
            await Run(iam, "user_remove_from_group", new() { ["user"] = "ada", ["group"] = "kassa" });
            Assert.Equal(new[] { "UpdateUser UserName=ada NewUserName=ada.lovelace NewPath=/kafe/", "AddUserToGroup GroupName=kassa UserName=ada", "RemoveUserFromGroup GroupName=kassa UserName=ada" },
                iam.Requests.Select(sent => sent.Form["Action"] + " " + string.Join(" ", sent.Form.Where(pair => pair.Key is not ("Action" or "Version")).Select(pair => pair.Key + "=" + pair.Value))));

            iam.Requests.Clear();
            var deleted = Assert.Single(await Run(iam, "user_delete", new() { ["user"] = "ada" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["deleted"] = true, ["left_groups"] = "[\"kassa\",\"kafe\"]" }, deleted);
            Assert.Equal(new[] { "ListGroupsForUser", "RemoveUserFromGroup", "RemoveUserFromGroup", "DeleteUser" }, iam.Requests.Select(sent => sent.Form["Action"]));
        }

        [Fact]
        public async Task Groups_are_listed_with_their_users_made_renamed_and_deleted_after_their_users_leave()
        {
            var iam = new Iam(sent => sent.Form["Action"] switch
            {
                "ListGroups" => (HttpStatusCode.OK, Result("ListGroups", "<Groups><member><GroupName>kassa</GroupName><GroupId>AGPAKASSA</GroupId><Path>/</Path></member></Groups><IsTruncated>false</IsTruncated>")),
                "GetGroup" => (HttpStatusCode.OK, sent.Form.TryGetValue("Marker", out var marker) && marker == "u2"
                    ? Result("GetGroup", "<Group><GroupName>kassa</GroupName></Group><Users><member>" + User("grace") + "</member></Users><IsTruncated>false</IsTruncated>")
                    : Result("GetGroup", "<Group><GroupName>kassa</GroupName><GroupId>AGPAKASSA</GroupId><Path>/</Path></Group><Users><member>" + User("ada") + "</member></Users><IsTruncated>true</IsTruncated><Marker>u2</Marker>")),
                "CreateGroup" => (HttpStatusCode.OK, Result("CreateGroup", "<Group><GroupName>kassa</GroupName><GroupId>AGPAKASSA</GroupId><Path>/kafe/</Path><Arn>arn:aws:iam::123456789012:group/kafe/kassa</Arn></Group>")),
                _ => (HttpStatusCode.OK, Result(sent.Form["Action"], string.Empty)),
            });
            Assert.False(Assert.Single(await Run(iam, "groups", new())).Fields.ContainsKey("Users"));
            var withUsers = Assert.Single(await Run(iam, "groups", new() { ["include_users"] = "true" })).Fields;
            var users = JsonNode.Parse((string)withUsers["Users"]!)!.AsArray();
            Assert.Equal(new[] { "ada", "grace" }, users.Select(user => user!["UserName"]!.GetValue<string>())); // every page of them
            var group = Assert.Single(await Run(iam, "group", new() { ["group"] = "kassa" })).Fields;
            Assert.Equal(("kassa", "AGPAKASSA"), ((string?)group["GroupName"], (string?)group["GroupId"]));

            iam.Requests.Clear();
            Assert.Equal("/kafe/", Assert.Single(await Run(iam, "group_create", new() { ["group"] = "kassa", ["path"] = "/kafe/" })).Fields["Path"]);
            await Run(iam, "group_update", new() { ["group"] = "kassa", ["new_name"] = "kassor" });
            var deleted = Assert.Single(await Run(iam, "group_delete", new() { ["group"] = "kassa" })).Fields;
            Assert.Equal("[\"ada\",\"grace\"]", deleted["removed_users"]);
            Assert.Equal(new[] { "CreateGroup", "UpdateGroup", "GetGroup", "GetGroup", "RemoveUserFromGroup", "RemoveUserFromGroup", "DeleteGroup" }, iam.Requests.Select(sent => sent.Form["Action"]));
            Assert.Equal("kassor", iam.Requests[1].Form["NewGroupName"]);

            var refusing = new Iam(_ => (HttpStatusCode.Conflict, $"<ErrorResponse {Ns}><Error><Type>Sender</Type><Code>DeleteConflict</Code><Message>Cannot delete entity, must remove access keys first.</Message></Error><RequestId>r-2</RequestId></ErrorResponse>"));
            Assert.Equal("AWS IAM answered HTTP 409 DeleteConflict: Cannot delete entity, must remove access keys first.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "user", new() { ["user"] = "ada" }))).Message);
            var aliases = new Iam(_ => (HttpStatusCode.OK, Result("ListAccountAliases", "<AccountAliases><member>kafe-prod</member></AccountAliases><IsTruncated>false</IsTruncated>")));
            Assert.Equal((true, "AWS IAM answers: the account kafe-prod."), await Tested(aliases));
            Assert.Equal((true, "AWS IAM answers (the account has no alias)."), await Tested(new Iam(_ => (HttpStatusCode.OK, Result("ListAccountAliases", "<AccountAliases/><IsTruncated>false</IsTruncated>")))));
        }

        private static async Task<(bool, string)> Tested(Iam iam)
        {
            var result = await new AwsIamConnector().TestConnectionAsync(Contexts.Test(new StubEgress(iam), "", credentials: Credentials));
            return (result.Success, result.Message);
        }
    }

    /// <summary>
    /// The AWS IAM connector against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which checks every
    /// SigV4 signature against the IAM key it issued, through the HOST's egress: loopback refused until allowlisted; a test; under a
    /// path of the test's own, a group made, a user created with tags, found by the path, put in the group - which then lists them -
    /// renamed and moved; the group deleted with its user taken out first; the user put in a second group and deleted - out of it
    /// first, the group then listing nobody - and gone in AWS's words. Fails, never skips, when moto is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsIamConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = MotoTestEndpoint.SecretKey };

        private static HostConnectorEgress Egress => new(new[] { MotoTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsIamConnector(), Contexts.Extract(Egress, MotoTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Credentials, timeoutSeconds: 30));

        [Fact]
        public async Task Users_and_groups_are_administered_under_a_path_of_the_tests_own()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AwsIamConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), MotoTestEndpoint.Endpoint, credentials: Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var tested = await new AwsIamConnector().TestConnectionAsync(Contexts.Test(Egress, MotoTestEndpoint.Endpoint, credentials: Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {tested.Success}: {tested.Message}");
            Assert.True(tested.Success, tested.Message);

            var token = Guid.NewGuid().ToString("N")[..8];
            var path = $"/kafe-{token}/";
            var group = "kassa-" + token;
            var user = "ada-" + token;
            Assert.Equal(path, Assert.Single(await Run("group_create", new() { ["group"] = group, ["path"] = path })).Fields["Path"]);
            var made = Assert.Single(await Run("user_create", new() { ["user"] = user, ["path"] = path, ["tags"] = "{\"team\": \"kassa\"}" })).Fields;
            Assert.Equal((user, path), ((string?)made["UserName"], (string?)made["Path"]));
            Assert.Equal(user, Assert.Single(await Run("users", new() { ["path_prefix"] = path })).Fields["UserName"]);
            var read = Assert.Single(await Run("user", new() { ["user"] = user })).Fields;
            Assert.Contains("kassa", (string?)read["Tags"] ?? string.Empty, StringComparison.Ordinal);

            await Run("user_add_to_group", new() { ["user"] = user, ["group"] = group });
            var members = JsonNode.Parse((string)Assert.Single(await Run("group", new() { ["group"] = group, ["include_users"] = "true" })).Fields["Users"]!)!.AsArray();
            Assert.Equal(user, Assert.Single(members)!["UserName"]!.GetValue<string>());
            var renamed = user + "-l";
            await Run("user_update", new() { ["user"] = user, ["new_name"] = renamed, ["new_path"] = path + "kassa/" });
            Assert.Equal(path + "kassa/", Assert.Single(await Run("user", new() { ["user"] = renamed })).Fields["Path"]);

            var groupGone = Assert.Single(await Run("group_delete", new() { ["group"] = group })).Fields;
            Assert.Equal($"[\"{renamed}\"]", groupGone["removed_users"]); // its user taken out first, as n8n's node does

            // A user deleted while in a group leaves it first - moto, unlike IAM, would delete it and leave the group naming it.
            var kept = group + "-k";
            await Run("group_create", new() { ["group"] = kept, ["path"] = path });
            await Run("user_add_to_group", new() { ["user"] = renamed, ["group"] = kept });
            var deleted = Assert.Single(await Run("user_delete", new() { ["user"] = renamed })).Fields;
            Assert.Equal((true, $"[\"{kept}\"]"), ((bool)deleted["deleted"]!, (string?)deleted["left_groups"]));
            Assert.Empty(JsonNode.Parse((string)Assert.Single(await Run("group", new() { ["group"] = kept, ["include_users"] = "true" })).Fields["Users"]!)!.AsArray());
            Assert.Equal("[]", Assert.Single(await Run("group_delete", new() { ["group"] = kept })).Fields["removed_users"]);
            // moto, unlike IAM, lists every group whatever the prefix - so the test looks for its own path among them.
            Assert.DoesNotContain(await Run("groups", new() { ["path_prefix"] = path }), found => (string?)found.Fields["Path"] == path);
            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("user", new() { ["user"] = renamed }));
            output.WriteLine($"[live] gone: {gone.Message}");
            Assert.StartsWith("AWS IAM answered HTTP 404 NoSuchEntity", gone.Message, StringComparison.Ordinal);
        }
    }
}
