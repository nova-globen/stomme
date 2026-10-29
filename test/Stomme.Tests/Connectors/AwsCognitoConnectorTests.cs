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
using Stomme.Modules.Connectors.AwsCognito;
using Stomme.Modules.Connectors.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// n8n's AWS Cognito node over the Cognito Identity Provider's JSON protocol, each call signed with SigV4 for
    /// cognito-idp in the connection's region: every parameter held to its shape before anything is signed; users listed by an
    /// attribute's start or the whole of it - its value escaped - page by page up to the most, their attributes their fields unless
    /// asked not; a user read, created with their attributes and invitation and never a password, updated, deleted, added to a
    /// group and removed from it; groups listed and read with their users if asked, created, updated and deleted; a pool described
    /// without its long settings unless asked, pools listed; the step's pool, else the connection's; AWS's refusals with their type.
    /// </summary>
    public sealed class AwsCognitoConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-west-1", ["user_pool"] = "eu-west-1_Kassa01" };
        private const string Api = "https://cognito-idp.eu-west-1.amazonaws.com/";

        private sealed record Sent(string Method, string Url, string? Target, string? Authorization, string Body);

        private sealed class Aws(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, Header("X-Amz-Target")?.Replace("AWSCognitoIdentityProviderService.", "", StringComparison.Ordinal), Header("Authorization"), body);
                Requests.Add(sent);
                return answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1") };

        private static Task<List<ConnectorRecord>> Run(Aws aws, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new AwsCognitoConnector(), Contexts.Extract(new StubEgress(aws), "", operation, parameters, settings ?? Settings, Credentials));

        private const string Ada = "{\"Username\":\"ada\",\"Attributes\":[{\"Name\":\"email\",\"Value\":\"ada@kafe.example\"},{\"Name\":\"custom:team\",\"Value\":\"kassa\"}],\"Enabled\":true,\"UserStatus\":\"FORCE_CHANGE_PASSWORD\"}";

        [Fact]
        public void It_declares_itself_as_n8ns_aws_cognito_node()
        {
            IConnector connector = new AwsCognitoConnector();
            Assert.Equal(("awscognito", "Amazon Cognito", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "users", "user", "user_create", "user_update", "user_delete", "user_add_to_group", "user_remove_from_group", "groups", "group", "group_create", "group_update", "group_delete", "user_pool", "user_pools" },
                ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "region", "user_pool" }, connector.ConfigSchema.Fields.Select(field => field.Name));
            Assert.DoesNotContain(((IConnectorExtractor)connector).Operations.SelectMany(operation => operation.Parameters?.Fields ?? Array.Empty<ConnectorConfigField>()), field => field.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var aws = new Aws(_ => Json("{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(aws, operation, parameters, settings));
                Assert.Empty(aws.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Amazon Cognito has no operation 'users_import' - users, user, user_create", await Refused("users_import", new()), StringComparison.Ordinal);
            Assert.Equal("Give the user pool - eu-west-1_AbC123dEf (user_pool), or the connection's.", await Refused("users", new(), new() { ["region"] = "eu-west-1" }));
            Assert.Equal("'kassa pool' is not a user pool's id - eu-west-1_AbC123dEf (user_pool).", await Refused("users", new() { ["user_pool"] = "kassa pool" }));
            Assert.Equal("Give a user's name (user).", await Refused("user", new()));
            Assert.Equal("'Ada Lovelace' is not a user's name Cognito takes - no spaces, at most 128 (user).", await Refused("user_delete", new() { ["user"] = "Ada Lovelace" }));
            Assert.Equal("Give a group's name (group).", await Refused("user_add_to_group", new() { ["user"] = "ada" }));
            Assert.Equal("Give a user's name (username).", await Refused("user_create", new()));
            Assert.Equal("The attributes are a JSON object of names and values - {\"email\": \"ada@kafe.example\"} (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["attributes"] = "[1]" }));
            Assert.Equal("'e mail' is not an attribute's name - email, or custom:team (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["attributes"] = "{\"e mail\": \"x\"}" }));
            Assert.Equal("team's value is not a text, a number or true or false (attributes).", await Refused("user_update", new() { ["user"] = "ada", ["attributes"] = "{\"team\": {\"name\": \"kassa\"}}" }));
            Assert.Equal("email's value is longer than Cognito's 2,048 characters (attributes).", await Refused("user_update", new() { ["user"] = "ada", ["attributes"] = "{\"email\": \"" + new string('a', 2049) + "\"}" }));
            Assert.Equal("Give the attributes - a JSON object, {\"email\": \"ada@kafe.example\"} (attributes).", await Refused("user_update", new() { ["user"] = "ada" }));
            Assert.Equal("email_verified without email: give the email it verifies (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["attributes"] = "{\"email_verified\": true}" }));
            Assert.Equal("phone_number_verified without phone_number: give the phone_number it verifies (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["attributes"] = "{\"phone_number_verified\": \"true\"}" }));
            Assert.Equal("'fax' is not email or sms (delivery).", await Refused("user_create", new() { ["username"] = "ada", ["delivery"] = "email, fax" }));
            Assert.Equal("An invitation by email needs the email attribute (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["delivery"] = "email" }));
            Assert.Equal("An invitation by sms needs the phone_number attribute (attributes).", await Refused("user_create", new() { ["username"] = "ada", ["attributes"] = "{\"email\": \"ada@kafe.example\"}", ["delivery"] = "EMAIL,sms" }));
            Assert.Equal("'send' is not resend or suppress (message_action).", await Refused("user_create", new() { ["username"] = "ada", ["message_action"] = "send" }));
            Assert.Equal("'maybe' is not true or false (force_alias).", await Refused("user_create", new() { ["username"] = "ada", ["force_alias"] = "maybe" }));
            Assert.Equal("'-1' is not a number from 0 (precedence).", await Refused("group_create", new() { ["group"] = "kassa", ["precedence"] = "-1" }));
            Assert.Equal("A group's description is at most 2,048 characters (description).", await Refused("group_create", new() { ["group"] = "kassa", ["description"] = new string('a', 2049) }));
            Assert.Equal("'arn:aws:iam::12:role/Kassa' is not an IAM role's ARN - arn:aws:iam::123456789012:role/Kassa (role_arn).", await Refused("group_create", new() { ["group"] = "kassa", ["role_arn"] = "arn:aws:iam::12:role/Kassa" }));
            Assert.Equal("Give what changes - a description, a precedence or an IAM role (description, precedence, role_arn).", await Refused("group_update", new() { ["group"] = "kassa" }));
            Assert.StartsWith("'city' is not an attribute Cognito filters users on - username, email,", await Refused("users", new() { ["filter_attribute"] = "city", ["filter_value"] = "Lund" }), StringComparison.Ordinal);
            Assert.Equal("Give the value email is to start with (filter_value).", await Refused("users", new() { ["filter_attribute"] = "EMAIL" }));
            Assert.Equal("Give the attribute the value is of (filter_attribute).", await Refused("users", new() { ["filter_value"] = "ada" }));
            Assert.Equal("'maybe' is not true or false (exact).", await Refused("users", new() { ["filter_attribute"] = "email", ["filter_value"] = "ada", ["exact"] = "maybe" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("user_pools", new() { ["max"] = "0" }));
            Assert.Equal("'maybe' is not true or false (simple).", await Refused("user", new() { ["user"] = "ada", ["simple"] = "maybe" }));
            Assert.Equal("'yes' is not true or false (include_users).", await Refused("groups", new() { ["include_users"] = "yes" }));
        }

        [Fact]
        public async Task Users_are_listed_by_an_attributes_start_or_whole_page_by_page_their_attributes_their_fields()
        {
            string Page(int from, int count, string? next) => "{\"Users\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => Ada.Replace("\"ada\"", "\"ada" + i + "\"", StringComparison.Ordinal))) + "]" + (next is null ? "" : ",\"PaginationToken\":\"" + next + "\"") + "}";
            var aws = new Aws(sent => sent.Body.Contains("\"PaginationToken\":\"p2\"", StringComparison.Ordinal) ? Json(Page(60, 10, null)) : Json(Page(0, 60, "p2")));
            var all = await Run(aws, "users", new() { ["filter_attribute"] = "Email", ["filter_value"] = "ada\"\\" });
            Assert.Equal(70, all.Count);
            Assert.Equal(new[]
            {
                "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Filter\":\"email ^= \\\"ada\\\\\\\"\\\\\\\\\\\"\",\"Limit\":60}",
                "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Filter\":\"email ^= \\\"ada\\\\\\\"\\\\\\\\\\\"\",\"Limit\":60,\"PaginationToken\":\"p2\"}",
            }, aws.Requests.Select(sent => sent.Body)); // the value's quote and backslash escaped as Cognito's filter reads them
            Assert.All(aws.Requests, sent => Assert.Equal(("POST", Api, "ListUsers"), (sent.Method, sent.Url, sent.Target)));
            Assert.Contains("/eu-west-1/cognito-idp/aws4_request", aws.Requests[0].Authorization, StringComparison.Ordinal);
            var first = all[0].Fields;
            Assert.Equal(("ada0", "ada@kafe.example", "kassa", "FORCE_CHANGE_PASSWORD", (object?)true), ((string?)first["Username"], (string?)first["email"], (string?)first["custom:team"], (string?)first["UserStatus"], first["Enabled"]));
            Assert.False(first.ContainsKey("Attributes"));

            aws.Requests.Clear();
            var raw = await Run(aws, "users", new() { ["filter_attribute"] = "cognito:user_status", ["filter_value"] = "CONFIRMED", ["exact"] = "true", ["simple"] = "false", ["max"] = "5" });
            Assert.Equal(5, raw.Count);
            Assert.Equal("{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Filter\":\"cognito:user_status = \\\"CONFIRMED\\\"\",\"Limit\":5}", aws.Requests.Single().Body); // a page no larger than what is wanted
            Assert.StartsWith("[{\"Name\":\"email\",\"Value\":\"ada@kafe.example\"}", (string)raw[0].Fields["Attributes"]!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_user_is_read_created_without_a_password_updated_deleted_and_grouped()
        {
            var aws = new Aws(sent => sent.Target switch
            {
                "AdminGetUser" => Json("{\"Username\":\"ada\",\"UserAttributes\":[{\"Name\":\"sub\",\"Value\":\"0d1e\"},{\"Name\":\"email\",\"Value\":\"ada@kafe.example\"}],\"UserStatus\":\"CONFIRMED\"}"),
                "AdminCreateUser" => Json("{\"User\":" + Ada + "}"),
                _ => Json("{}"),
            });
            var read = Assert.Single(await Run(aws, "user", new() { ["user"] = "ada", ["user_pool"] = "eu-west-1_Cafe02" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["Username"] = "ada", ["UserStatus"] = "CONFIRMED", ["sub"] = "0d1e", ["email"] = "ada@kafe.example" }, read);
            Assert.Equal(("AdminGetUser", "{\"UserPoolId\":\"eu-west-1_Cafe02\",\"Username\":\"ada\"}"), (aws.Requests[0].Target, aws.Requests[0].Body)); // the step's pool before the connection's
            var whole = Assert.Single(await Run(aws, "user", new() { ["user"] = "0d1e", ["simple"] = "false" })).Fields;
            Assert.StartsWith("[{\"Name\":\"sub\"", (string)whole["UserAttributes"]!, StringComparison.Ordinal);

            aws.Requests.Clear();
            var made = Assert.Single(await Run(aws, "user_create", new()
            {
                ["username"] = "ada", ["attributes"] = "{\"email\": \"ada@kafe.example\", \"email_verified\": true, \"phone_number\": \"+46701740605\", \"custom:till\": 4}",
                ["delivery"] = "email, SMS, email", ["message_action"] = "Suppress", ["force_alias"] = "true",
            })).Fields;
            Assert.Equal("{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"ada\",\"UserAttributes\":[{\"Name\":\"email\",\"Value\":\"ada@kafe.example\"},{\"Name\":\"email_verified\",\"Value\":\"true\"},{\"Name\":\"phone_number\",\"Value\":\"+46701740605\"},{\"Name\":\"custom:till\",\"Value\":\"4\"}],\"DesiredDeliveryMediums\":[\"EMAIL\",\"SMS\"],\"MessageAction\":\"SUPPRESS\",\"ForceAliasCreation\":true}", aws.Requests.Single().Body);
            Assert.Equal(("ada", "kassa"), ((string?)made["Username"], (string?)made["custom:team"]));
            await Run(aws, "user_create", new() { ["username"] = "+46701740605" });
            Assert.Equal("{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"+46701740605\"}", aws.Requests.Last().Body); // nothing else given: Cognito's defaults

            aws.Requests.Clear();
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["updated"] = true }, Assert.Single(await Run(aws, "user_update", new() { ["user"] = "ada", ["attributes"] = "{\"family_name\": \"Lovelace\"}" })).Fields);
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["group"] = "kassa", ["added_to_group"] = true }, Assert.Single(await Run(aws, "user_add_to_group", new() { ["user"] = "ada", ["group"] = "kassa" })).Fields);
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["group"] = "kassa", ["removed_from_group"] = true }, Assert.Single(await Run(aws, "user_remove_from_group", new() { ["user"] = "ada", ["group"] = "kassa" })).Fields);
            Assert.Equal(new Dictionary<string, object?> { ["user"] = "ada", ["deleted"] = true }, Assert.Single(await Run(aws, "user_delete", new() { ["user"] = "ada" })).Fields);
            Assert.Equal(new[]
            {
                ("AdminUpdateUserAttributes", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"ada\",\"UserAttributes\":[{\"Name\":\"family_name\",\"Value\":\"Lovelace\"}]}"),
                ("AdminAddUserToGroup", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"ada\",\"GroupName\":\"kassa\"}"),
                ("AdminRemoveUserFromGroup", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"ada\",\"GroupName\":\"kassa\"}"),
                ("AdminDeleteUser", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Username\":\"ada\"}"),
            }, aws.Requests.Select(sent => (sent.Target!, sent.Body)));
        }

        [Fact]
        public async Task Groups_are_listed_and_read_with_their_users_made_changed_and_deleted()
        {
            string Groups(int from, int count, string? next) => "{\"Groups\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"GroupName\":\"kassa" + i + "\",\"Precedence\":" + i + "}")) + "]" + (next is null ? "" : ",\"NextToken\":\"" + next + "\"") + "}";
            var aws = new Aws(sent => sent.Target switch
            {
                "ListGroups" => Json(sent.Body.Contains("\"NextToken\":\"g2\"", StringComparison.Ordinal) ? Groups(60, 2, null) : Groups(0, 60, "g2")),
                "ListUsersInGroup" => Json(sent.Body.Contains("\"NextToken\":\"u2\"", StringComparison.Ordinal) ? "{\"Users\":[" + Ada.Replace("\"ada\"", "\"grace\"", StringComparison.Ordinal) + "]}" : "{\"Users\":[" + Ada + "],\"NextToken\":\"u2\"}"),
                "GetGroup" or "CreateGroup" or "UpdateGroup" => Json("{\"Group\":{\"GroupName\":\"kassa\",\"Description\":\"Kassan\",\"Precedence\":2,\"RoleArn\":\"arn:aws:iam::123456789012:role/Kassa\"}}"),
                _ => Json("{}"),
            });
            var all = await Run(aws, "groups", new());
            Assert.Equal(62, all.Count);
            Assert.Equal(new[] { "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Limit\":60}", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"Limit\":60,\"NextToken\":\"g2\"}" }, aws.Requests.Select(sent => sent.Body));
            Assert.Equal((object?)61L, all[61].Fields["Precedence"]);

            aws.Requests.Clear();
            var withUsers = Assert.Single(await Run(aws, "group", new() { ["group"] = "kassa", ["include_users"] = "true" })).Fields;
            Assert.Equal(("kassa", "Kassan"), ((string?)withUsers["GroupName"], (string?)withUsers["Description"]));
            var users = JsonNode.Parse((string)withUsers["Users"]!)!.AsArray();
            Assert.Equal(new[] { "ada", "grace" }, users.Select(user => user!["Username"]!.GetValue<string>()));
            Assert.Equal("kassa", users[0]!["custom:team"]!.GetValue<string>()); // each user as a user record is
            Assert.Equal(new[] { "GetGroup", "ListUsersInGroup", "ListUsersInGroup" }, aws.Requests.Select(sent => sent.Target));
            Assert.Equal("{\"UserPoolId\":\"eu-west-1_Kassa01\",\"GroupName\":\"kassa\",\"Limit\":60,\"NextToken\":\"u2\"}", aws.Requests[2].Body);
            Assert.False(Assert.Single(await Run(aws, "group", new() { ["group"] = "kassa" })).Fields.ContainsKey("Users"));

            aws.Requests.Clear();
            Assert.Equal("arn:aws:iam::123456789012:role/Kassa", Assert.Single(await Run(aws, "group_create", new() { ["group"] = "kassa", ["description"] = "Kassan", ["precedence"] = "2", ["role_arn"] = "arn:aws:iam::123456789012:role/Kassa" })).Fields["RoleArn"]);
            await Run(aws, "group_update", new() { ["group"] = "kassa", ["precedence"] = "0" });
            Assert.Equal(new Dictionary<string, object?> { ["group"] = "kassa", ["deleted"] = true }, Assert.Single(await Run(aws, "group_delete", new() { ["group"] = "kassa" })).Fields);
            Assert.Equal(new[]
            {
                ("CreateGroup", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"GroupName\":\"kassa\",\"Description\":\"Kassan\",\"Precedence\":2,\"RoleArn\":\"arn:aws:iam::123456789012:role/Kassa\"}"),
                ("UpdateGroup", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"GroupName\":\"kassa\",\"Precedence\":0}"),
                ("DeleteGroup", "{\"UserPoolId\":\"eu-west-1_Kassa01\",\"GroupName\":\"kassa\"}"),
            }, aws.Requests.Select(sent => (sent.Target!, sent.Body)));
        }

        [Fact]
        public async Task A_pool_is_described_without_its_long_settings_pools_listed_and_a_test_says_which()
        {
            var pool = "{\"UserPool\":{\"Id\":\"eu-west-1_Kassa01\",\"Name\":\"Kassa\",\"MfaConfiguration\":\"OFF\",\"Policies\":{\"PasswordPolicy\":{\"MinimumLength\":12}},\"LambdaConfig\":{},\"SchemaAttributes\":[{\"Name\":\"email\"}],\"EstimatedNumberOfUsers\":3}}";
            var aws = new Aws(sent => sent.Target switch
            {
                "DescribeUserPool" => Json(pool),
                "ListUserPools" => Json("{\"UserPools\":[{\"Id\":\"eu-west-1_Kassa01\",\"Name\":\"Kassa\"},{\"Id\":\"eu-west-1_Cafe02\",\"Name\":\"Kafé\"}]}"),
                _ => Json("{}"),
            });
            Assert.Equal(new Dictionary<string, object?> { ["Id"] = "eu-west-1_Kassa01", ["Name"] = "Kassa", ["MfaConfiguration"] = "OFF", ["EstimatedNumberOfUsers"] = 3L }, Assert.Single(await Run(aws, "user_pool", new())).Fields);
            var whole = Assert.Single(await Run(aws, "user_pool", new() { ["simple"] = "false" })).Fields;
            Assert.Equal("{\"PasswordPolicy\":{\"MinimumLength\":12}}", whole["Policies"]);
            Assert.Equal(new[] { "Kassa", "Kafé" }, (await Run(aws, "user_pools", new(), new() { ["region"] = "eu-west-1" })).Select(found => (string?)found.Fields["Name"]));
            Assert.Equal("{\"MaxResults\":60}", aws.Requests.Last().Body); // no pool asked for: none sent

            var tested = await new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(aws), "", Settings, Credentials));
            Assert.Equal((true, "Amazon Cognito in eu-west-1 answers: user pool Kassa."), (tested.Success, tested.Message));
            var listed = await new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(aws), "", new Dictionary<string, string> { ["region"] = "eu-west-1" }, Credentials));
            Assert.Equal((true, "Amazon Cognito in eu-west-1 answers (its user pools listed)."), (listed.Success, listed.Message));
            Assert.Equal("{\"MaxResults\":1}", aws.Requests.Last().Body);

            var refusing = new Aws(_ => Json("{\"__type\":\"UserNotFoundException\",\"message\":\"User does not exist.\"}", HttpStatusCode.BadRequest));
            Assert.Equal("Amazon Cognito answered HTTP 400 UserNotFoundException: User does not exist.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "user", new() { ["user"] = "nobody" }))).Message);
            var bad = await new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refusing), "", new Dictionary<string, string> { ["region"] = "eu-west-1", ["user_pool"] = "kassa" }, Credentials));
            Assert.Equal((false, "'kassa' is not a user pool's id - eu-west-1_AbC123dEf (user_pool)."), (bad.Success, bad.Message));
        }
    }

    /// <summary>
    /// The Amazon Cognito connector against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which
    /// checks every SigV4 signature against the IAM key it issued, through the HOST's egress: loopback refused until allowlisted;
    /// a wrong secret refused; a test naming the pool; in a pool made for the test, a group made and changed, a user created with
    /// no invitation sent, found by the start of their email, read, updated, put in the group - which then lists them - and taken
    /// out, deleted; the group deleted and the pool with it. Fails, never skips, when moto is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsCognitoConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = MotoTestEndpoint.SecretKey };

        private static HostConnectorEgress Egress => new(new[] { MotoTestEndpoint.Host });

        private static Dictionary<string, string> Settings(string? pool = null) => pool is null ? new() { ["region"] = MotoTestEndpoint.Region } : new() { ["region"] = MotoTestEndpoint.Region, ["user_pool"] = pool };

        private static Task<List<ConnectorRecord>> Run(string pool, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsCognitoConnector(), Contexts.Extract(Egress, MotoTestEndpoint.Endpoint, operation, parameters, Settings(pool), Credentials, timeoutSeconds: 30));

        // The pool itself is the test's to make and drop - the connector administers what is in one.
        private static AwsJsonSession Admin() =>
            AwsJsonSession.Of("moto", "cognito-idp", "AWSCognitoIdentityProviderService", MotoTestEndpoint.Endpoint, Settings(), Credentials, Egress, TimeSpan.FromSeconds(30), TimeProvider.System);

        [Fact]
        public async Task A_test_names_the_pool_a_wrong_secret_is_refused_and_loopback_until_allowed()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), MotoTestEndpoint.Endpoint, Settings(), Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var wrong = await new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(Egress, MotoTestEndpoint.Endpoint, Settings(), new Dictionary<string, string> { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = "not-" + MotoTestEndpoint.SecretKey }, timeoutSeconds: 30));
            output.WriteLine($"[live] wrong secret: {wrong.Success}: {wrong.Message}");
            Assert.False(wrong.Success);
            Assert.StartsWith("Amazon Cognito answered HTTP 403", wrong.Message, StringComparison.Ordinal);

            var pool = (await Admin().CallAsync("CreateUserPool", new JsonObject { ["PoolName"] = "kafe-test-" + Guid.NewGuid().ToString("N")[..8] }, default))!["UserPool"]!["Id"]!.GetValue<string>();
            try
            {
                var right = await new AwsCognitoConnector().TestConnectionAsync(Contexts.Test(Egress, MotoTestEndpoint.Endpoint, Settings(pool), Credentials, timeoutSeconds: 30));
                output.WriteLine($"[live] {right.Success}: {right.Message}");
                Assert.True(right.Success, right.Message);
                Assert.StartsWith("Amazon Cognito in eu-west-1 answers: user pool kafe-test-", right.Message, StringComparison.Ordinal);
                Assert.Contains(await Contexts.DrainAsync(new AwsCognitoConnector(), Contexts.Extract(Egress, MotoTestEndpoint.Endpoint, "user_pools", new Dictionary<string, string>(), Settings(), Credentials, timeoutSeconds: 30)), found => (string?)found.Fields["Id"] == pool);
            }
            finally
            {
                await Admin().CallAsync("DeleteUserPool", new JsonObject { ["UserPoolId"] = pool }, default);
            }
        }

        [Fact]
        public async Task A_pools_users_and_groups_are_administered()
        {
            var pool = (await Admin().CallAsync("CreateUserPool", new JsonObject { ["PoolName"] = "kafe-test-" + Guid.NewGuid().ToString("N")[..8] }, default))!["UserPool"]!["Id"]!.GetValue<string>();
            try
            {
                var group = Assert.Single(await Run(pool, "group_create", new() { ["group"] = "kassa", ["description"] = "Kassan", ["precedence"] = "2" })).Fields;
                Assert.Equal(("kassa", "Kassan", (object?)2L), ((string?)group["GroupName"], (string?)group["Description"], group["Precedence"]));
                Assert.Equal((object?)0L, Assert.Single(await Run(pool, "group_update", new() { ["group"] = "kassa", ["precedence"] = "0" })).Fields["Precedence"]);

                var made = Assert.Single(await Run(pool, "user_create", new() { ["username"] = "ada", ["attributes"] = "{\"email\": \"ada@kafe.example\", \"given_name\": \"Ada\"}", ["message_action"] = "suppress" })).Fields;
                Assert.Equal(("ada", "ada@kafe.example"), ((string?)made["Username"], (string?)made["email"]));
                await Run(pool, "user_create", new() { ["username"] = "grace", ["attributes"] = "{\"email\": \"grace@kafe.example\"}", ["message_action"] = "suppress" });
                var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pool, "user_create", new() { ["username"] = "ada", ["message_action"] = "suppress" }));
                Assert.StartsWith("Amazon Cognito answered HTTP 400 UsernameExistsException", duplicate.Message, StringComparison.Ordinal);

                var found = Assert.Single(await Run(pool, "users", new() { ["filter_attribute"] = "email", ["filter_value"] = "ada@" })).Fields;
                Assert.Equal("ada", found["Username"]);
                Assert.Equal(2, (await Run(pool, "users", new())).Count);
                Assert.Empty(await Run(pool, "users", new() { ["filter_attribute"] = "email", ["filter_value"] = "ada@", ["exact"] = "true" }));

                Assert.True((bool)Assert.Single(await Run(pool, "user_update", new() { ["user"] = "ada", ["attributes"] = "{\"family_name\": \"Lovelace\"}" })).Fields["updated"]!);
                var read = Assert.Single(await Run(pool, "user", new() { ["user"] = "ada" })).Fields;
                Assert.Equal(("Lovelace", "Ada"), ((string?)read["family_name"], (string?)read["given_name"]));

                await Run(pool, "user_add_to_group", new() { ["user"] = "ada", ["group"] = "kassa" });
                var members = JsonNode.Parse((string)Assert.Single(await Run(pool, "group", new() { ["group"] = "kassa", ["include_users"] = "true" })).Fields["Users"]!)!.AsArray();
                Assert.Equal("ada@kafe.example", Assert.Single(members)!["email"]!.GetValue<string>());
                await Run(pool, "user_remove_from_group", new() { ["user"] = "ada", ["group"] = "kassa" });
                Assert.Empty(JsonNode.Parse((string)Assert.Single(await Run(pool, "groups", new() { ["include_users"] = "true" })).Fields["Users"]!)!.AsArray());

                await Run(pool, "user_delete", new() { ["user"] = "ada" });
                var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pool, "user", new() { ["user"] = "ada" }));
                Assert.StartsWith("Amazon Cognito answered HTTP 400 UserNotFoundException", gone.Message, StringComparison.Ordinal);
                await Run(pool, "group_delete", new() { ["group"] = "kassa" });
                Assert.Empty(await Run(pool, "groups", new()));
            }
            finally
            {
                await Admin().CallAsync("DeleteUserPool", new JsonObject { ["UserPoolId"] = pool }, default);
            }
        }
    }
}
