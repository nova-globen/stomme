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
using Stomme.Modules.Connectors.MicrosoftDynamics;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft Dynamics 365 (n8n's Microsoft Dynamics CRM node: an environment's accounts - here any table): an
    /// application user's client credentials at the tenant's token endpoint for the environment's own scope, the Dataverse Web
    /// API with the token as a bearer; records read page by page by the environment's next link - followed only while it stays
    /// on the table - no further than asked, with a filter, columns, related records and an order; one got, created, updated
    /// (an update never creating one) and deleted; what could go wrong refused before a token is asked for; the environment's
    /// code and message on a failure; a test naming the application user.
    /// </summary>
    public sealed class MicrosoftDynamicsConnectorTests
    {
        private const string Environment = "https://kafe.crm4.dynamics.com";

        private const string Api = Environment + "/api/data/v9.2";

        private const string TokenUrl = "https://login.microsoftonline.com/kafe-tenant/oauth2/v2.0/token";

        private const string AccountId = "475b158c-541c-e511-80d3-3863bb347ba8";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = TokenUrl };

        private sealed class Dataverse : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string Headers)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"value\":[]}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                string Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body, $"prefer={Header("Prefer")};if-match={Header("If-Match")};odata={Header("OData-Version")};accept={Header("Accept")}"));
                var (status, answer) = url == TokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"eyJ.crm\",\"expires_in\":3599,\"token_type\":\"Bearer\"}") : Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Dataverse dataverse, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null, string endpoint = Environment, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new MicrosoftDynamicsConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(dataverse), endpoint, operation, parameters, settings, credentials ?? Credentials));

        private static string Accounts(int first, int count, string? next) =>
            "{\"@odata.context\":\"" + Api + "/$metadata#accounts(name)\",\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"@odata.etag\":\"W/\\\"" + i + "\\\"\",\"accountid\":\"" + Guid.Empty.ToString()[..^4] + i.ToString("D4") + "\",\"name\":\"Kafé " + i + "\",\"revenue\":" + (1000 + i) + ".5,\"statecode\":0}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";

        [Fact]
        public async Task Records_are_read_page_by_page_by_the_environments_next_link_no_further_than_asked()
        {
            var dataverse = new Dataverse
            {
                Answer = (_, url) => url.Contains("skiptoken=p2", StringComparison.Ordinal) ? (HttpStatusCode.OK, Accounts(1001, 30, null))
                    : url.Contains("skiptoken=p1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Accounts(501, 500, Api + "/accounts?$select=name&$skiptoken=p2"))
                    : (HttpStatusCode.OK, Accounts(1, 500, Api + "/accounts?$select=name&$skiptoken=p1")),
            };

            var accounts = await Run(dataverse, "records", new() { ["table"] = "accounts", ["select"] = "name, revenue,statecode", ["filter"] = "statecode eq 0 and contains(name,'Kafé')", ["expand"] = "primarycontactid($select=fullname)", ["order_by"] = "name asc,createdon desc", ["max"] = "750" });
            Assert.Equal(750, accounts.Count); // no further than asked
            Assert.Equal(("POST", TokenUrl, (string?)"grant_type=client_credentials&client_id=app-1&client_secret=secret-1&scope=https%3A%2F%2Fkafe.crm4.dynamics.com%2F.default"), (dataverse.Requests[0].Method.Method, dataverse.Requests[0].Url, dataverse.Requests[0].Body));
            Assert.Equal(new[]
            {
                $"{Api}/accounts?$select=name,revenue,statecode&$expand=primarycontactid%28%24select%3Dfullname%29&$filter=statecode%20eq%200%20and%20contains%28name%2C%27Kaf%C3%A9%27%29&$orderby=name%20asc%2Ccreatedon%20desc",
                $"{Api}/accounts?$select=name&$skiptoken=p1",
            }, dataverse.Requests.Skip(1).Select(request => request.Url));
            Assert.All(dataverse.Requests.Skip(1), request => Assert.Equal(("Bearer eyJ.crm", "prefer=odata.maxpagesize=500;if-match=;odata=4.0;accept=application/json"), (request.Authorization, request.Headers)));
            var first = accounts[0].Fields;
            Assert.Equal(("00000000-0000-0000-0000-00000000" + "0001", "Kafé 1", 1001.5, 0L), ((string?)first["accountid"], (string?)first["name"], (double?)first["revenue"], (long?)first["statecode"]));
            Assert.False(first.Keys.Any(key => key.StartsWith("@odata.", StringComparison.Ordinal))); // the annotations left out
            dataverse.Requests.Clear();
            Assert.Equal(1030, (await Run(dataverse, null!, new() { ["table"] = "accounts", ["max"] = "5000" })).Count); // no operation: the records - no next link the end
            Assert.Equal($"{Api}/accounts", dataverse.Requests[1].Url);
            Assert.Equal("prefer=odata.maxpagesize=500;if-match=;odata=4.0;accept=application/json", dataverse.Requests[1].Headers);
            var small = new Dataverse { Answer = (_, _) => (HttpStatusCode.OK, Accounts(1, 3, null)) };
            Assert.Equal(2, (await Run(small, "records", new() { ["table"] = "contacts", ["max"] = "2" })).Count);
            Assert.Equal("prefer=odata.maxpagesize=2;if-match=;odata=4.0;accept=application/json", small.Requests[^1].Headers); // a page no larger than asked
            var endless = new Dataverse { Answer = (_, _) => (HttpStatusCode.OK, Accounts(1, 500, Api + "/accounts?$skiptoken=" + Guid.NewGuid().ToString("N"))) };
            Assert.Equal(1000, (await Run(endless, "records", new() { ["table"] = "accounts" })).Count); // 1,000 at most when no max is given

            foreach (var link in new[] { "https://elsewhere.example.test/api/data/v9.2/accounts?$skiptoken=p1", Api + "/contacts?$skiptoken=p1", Api + "/accounts(" + AccountId + ")?$skiptoken=p1", Api + "/accounts" })
            {
                var stray = new Dataverse { Answer = (_, _) => (HttpStatusCode.OK, Accounts(1, 2, link)) };
                Assert.Equal("Microsoft Dynamics 365 named a next page outside the table - it is not followed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stray, "records", new() { ["table"] = "accounts" }))).Message);
                Assert.Equal(2, stray.Requests.Count); // the token, one page: nothing sent there
            }

            var itself = new Dataverse { Answer = (_, _) => (HttpStatusCode.OK, Accounts(1, 2, Api + "/accounts?$select=name")) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(itself, "records", new() { ["table"] = "accounts", ["select"] = "name" })); // nor one naming the page itself
            Assert.Equal(2, itself.Requests.Count);
        }

        [Fact]
        public async Task A_record_is_got_created_updated_and_deleted()
        {
            const string Account = "{\"@odata.context\":\"x\",\"@odata.etag\":\"W/\\\"7\\\"\",\"accountid\":\"" + AccountId + "\",\"name\":\"Kafé AB\",\"address1_city\":\"Stockholm\",\"primarycontactid\":{\"fullname\":\"Ada Lovelace\"}}";
            var dataverse = new Dataverse { Answer = (method, _) => method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "") : (method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, Account) };
            (string, string, string?, string) Last() => (dataverse.Requests[^1].Method.Method, dataverse.Requests[^1].Url, dataverse.Requests[^1].Body, dataverse.Requests[^1].Headers);

            var account = Assert.Single(await Run(dataverse, "record", new() { ["table"] = "accounts", ["id"] = AccountId, ["select"] = "name,address1_city", ["expand"] = "primarycontactid($select=fullname)" })).Fields;
            Assert.Equal(("GET", $"{Api}/accounts({AccountId})?$select=name,address1_city&$expand=primarycontactid%28%24select%3Dfullname%29", (string?)null, "prefer=;if-match=;odata=4.0;accept=application/json"), Last());
            Assert.Equal(("Kafé AB", "{\"fullname\":\"Ada Lovelace\"}", false), ((string?)account["name"], (string?)account["primarycontactid"], account.ContainsKey("@odata.etag")));

            var created = Assert.Single(await Run(dataverse, "create_record", new() { ["table"] = "accounts", ["values"] = "{\"name\": \"Kafé AB\", \"revenue\": 1200.5, \"donotemail\": true, \"description\": null, \"primarycontactid@odata.bind\": \"/contacts(00000000-0000-0000-0000-000000000001)\"}", ["select"] = "accountid,name" })).Fields;
            Assert.Equal(("POST", $"{Api}/accounts?$select=accountid,name", (string?)"{\"name\":\"Kafé AB\",\"revenue\":1200.5,\"donotemail\":true,\"description\":null,\"primarycontactid@odata.bind\":\"/contacts(00000000-0000-0000-0000-000000000001)\"}", "prefer=return=representation;if-match=;odata=4.0;accept=application/json"), Last());
            Assert.Equal(AccountId, (string?)created["accountid"]);

            await Run(dataverse, "update_record", new() { ["table"] = "accounts", ["id"] = AccountId, ["values"] = "{\"telephone1\": \"+46 8 123\"}" });
            Assert.Equal(("PATCH", $"{Api}/accounts({AccountId})", (string?)"{\"telephone1\":\"+46 8 123\"}", "prefer=return=representation;if-match=*;odata=4.0;accept=application/json"), Last()); // an update, never a new record

            var deleted = Assert.Single(await Run(dataverse, "Delete_Record", new() { ["table"] = "accounts", ["id"] = AccountId, ["select"] = "name" })).Fields;
            Assert.Equal(("DELETE", $"{Api}/accounts({AccountId})", (string?)null, "prefer=;if-match=;odata=4.0;accept=application/json"), Last());
            Assert.Equal(((object?)AccountId, (object?)true), (deleted["id"], deleted["deleted"]));
            await Run(dataverse, "record", new() { ["table"] = "accounts", ["id"] = AccountId }, endpoint: Environment + "/api/data/v9.2/accounts");
            Assert.Equal($"{Api}/accounts({AccountId})", Last().Item2); // a pasted API address forgiven down to the environment
            await Run(dataverse, "record", new() { ["table"] = "accounts", ["id"] = AccountId }, settings: new() { ["scope"] = "https://kafe.crm.dynamics.com/.default" });
            Assert.EndsWith("&scope=https%3A%2F%2Fkafe.crm.dynamics.com%2F.default", dataverse.Requests.Last(request => request.Url == TokenUrl).Body);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_the_environments_words_on_a_failure()
        {
            var dataverse = new Dataverse();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null, string endpoint = Environment) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(dataverse, operation, parameters, credentials, endpoint))).Message;
            Dictionary<string, string> Account(params (string Name, string Value)[] more) => new[] { ("table", "accounts"), ("id", AccountId) }.Concat(more).ToDictionary(pair => pair.Item1, pair => pair.Item2);

            Assert.Equal("Microsoft Dynamics 365 has no operation 'leads' - records, record, create_record, update_record or delete_record.", await Refused("leads", new()));
            const string NoEnvironment = "Give the environment's address - https://kafe.crm4.dynamics.com (Power Platform admin center > the environment > Environment URL).";
            Assert.Equal(NoEnvironment, await Refused("records", new() { ["table"] = "accounts" }, endpoint: ""));
            Assert.Equal(NoEnvironment, await Refused("records", new() { ["table"] = "accounts" }, endpoint: "kafe.crm4.dynamics.com"));
            Assert.Equal(NoEnvironment, await Refused("records", new() { ["table"] = "accounts" }, endpoint: "https://kafe.crm4.dynamics.com/main.aspx?appid=1"));
            Assert.Equal(NoEnvironment, await Refused("records", new() { ["table"] = "accounts" }, endpoint: "https://kafe.crm4.dynamics.com/main.aspx"));
            Assert.Equal(NoEnvironment, await Refused("records", new() { ["table"] = "accounts" }, endpoint: "ftp://kafe.crm4.dynamics.com"));
            Assert.Equal("Give the table - its entity set name (table).", await Refused("records", new()));
            Assert.Equal("'Accounts' is not a table's entity set name - accounts, contacts (table).", await Refused("records", new() { ["table"] = "Accounts" }));
            Assert.Equal("'accounts/../contacts' is not a table's entity set name - accounts, contacts (table).", await Refused("records", new() { ["table"] = "accounts/../contacts" }));
            Assert.Equal("Give the record's id (id).", await Refused("record", new() { ["table"] = "accounts" }));
            Assert.Equal("'Kafé AB' is not a record's id - a GUID (id).", await Refused("delete_record", new() { ["table"] = "accounts", ["id"] = "Kafé AB" }));
            Assert.Equal("'zzzzzzzz-541c-e511-80d3-3863bb347ba8' is not a record's id - a GUID (id).", await Refused("record", new() { ["table"] = "accounts", ["id"] = "zzzzzzzz-541c-e511-80d3-3863bb347ba8" }));
            Assert.Equal("'475b158c-541c-e511-80d3-3863bb347ba8)/contacts(' is not a record's id - a GUID (id).", await Refused("record", new() { ["table"] = "accounts", ["id"] = AccountId + ")/contacts(" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("records", new() { ["table"] = "accounts", ["max"] = "0" }));
            Assert.Equal("'name;revenue' is not a comma-separated list of column names (select).", await Refused("records", new() { ["table"] = "accounts", ["select"] = "name;revenue" }));
            Assert.Equal("'Name' is not a comma-separated list of column names (select).", await Refused("record", Account(("select", "Name"))));
            Assert.Equal("'name sideways' is not a comma-separated list of columns, each asc or desc (order_by).", await Refused("records", new() { ["table"] = "accounts", ["order_by"] = "name sideways" }));
            Assert.Equal("'statecode eq 0\u0001' is not a text without control characters (filter).", await Refused("records", new() { ["table"] = "accounts", ["filter"] = "statecode eq 0\u0001" }));
            Assert.Equal("'contacts\u0001' is not a text without control characters (expand).", await Refused("record", Account(("expand", "contacts\u0001"))));
            Assert.Equal("Give the values - a JSON object of columns and values (values).", await Refused("create_record", new() { ["table"] = "accounts" }));
            const string NotAnObject = "The values are not a JSON object of columns and values - {\"name\": \"Kafé AB\"} (values).";
            Assert.Equal(NotAnObject, await Refused("create_record", new() { ["table"] = "accounts", ["values"] = "name = Kafé AB" }));
            Assert.Equal(NotAnObject, await Refused("update_record", Account(("values", "{}"))));
            Assert.Equal(NotAnObject, await Refused("update_record", Account(("values", "[{\"name\": \"Kafé\"}]"))));
            Assert.Equal("'Name' is not a column's name - lower-case letters, digits and _, or a lookup's column@odata.bind (values).", await Refused("update_record", Account(("values", "{\"Name\": \"Kafé\"}"))));
            Assert.Equal("'primarycontactid@odata.type' is not a column's name - lower-case letters, digits and _, or a lookup's column@odata.bind (values).", await Refused("update_record", Account(("values", "{\"primarycontactid@odata.type\": \"x\"}"))));
            Assert.Equal("The value of 'address1' is not a text, a number, true, false or null (values).", await Refused("update_record", Account(("values", "{\"address1\": {\"city\": \"Stockholm\"}}"))));
            Assert.Equal("The value of 'emailaddress1' is not a text, a number, true, false or null (values).", await Refused("update_record", Account(("values", "{\"emailaddress1\": [\"a@kafe.example.test\"]}"))));
            Assert.Equal("The value of 'name' has a control character (values).", await Refused("update_record", Account(("values", "{\"name\": \"Kafé\\u0001\"}"))));
            Assert.Equal("The value of 'primarycontactid@odata.bind' is not a record's reference - /contacts(00000000-0000-0000-0000-000000000001) (values).", await Refused("update_record", Account(("values", "{\"primarycontactid@odata.bind\": \"https://elsewhere.example.test/contacts(1)\"}"))));
            Assert.Equal("The value of 'primarycontactid@odata.bind' is not a record's reference - /contacts(00000000-0000-0000-0000-000000000001) (values).", await Refused("update_record", Account(("values", "{\"primarycontactid@odata.bind\": null}"))));
            Assert.Equal("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token.", await Refused("records", new() { ["table"] = "accounts" }, new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1" }));
            Assert.Empty(dataverse.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "record") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Dataverse { Answer = (_, _) => (status, answer) }, operation, Account(("values", "{\"name\": \"Kafé\"}"))))).Message;
            Assert.Equal("HTTP 404 0x80040217: account With Id = 475b158c-541c-e511-80d3-3863bb347ba8 Does Not Exist", await Failed(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"0x80040217\",\"message\":\"account With Id = 475b158c-541c-e511-80d3-3863bb347ba8 Does Not Exist\"}}", "update_record"));
            Assert.Equal("HTTP 403 0x80040220", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"0x80040220\"}}"));
            Assert.Equal("HTTP 400 0x80048d19: " + new string('e', 300 - "0x80048d19: ".Length), await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"0x80048d19\",\"message\":\"" + new string('e', 400) + "\"}}", "create_record")); // a long one cut
            Assert.Equal("Microsoft Dynamics 365 answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "delete_record"));
            Assert.Equal("Microsoft Dynamics 365 answered with no record.", await Failed(HttpStatusCode.NoContent, "", "update_record"));
        }

        [Fact]
        public async Task A_test_names_the_application_user()
        {
            Task<ConnectionTestResult> Test(Dataverse dataverse, string endpoint = Environment) =>
                new MicrosoftDynamicsConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(dataverse), endpoint, null, Credentials));
            var dataverse = new Dataverse { Answer = (_, _) => (HttpStatusCode.OK, "{\"BusinessUnitId\":\"b1\",\"UserId\":\"a8c1f0b4-1111-4a2b-9c3d-000000000042\",\"OrganizationId\":\"o1\"}") };
            var tested = await Test(dataverse);
            Assert.Equal((true, "Microsoft Dynamics 365: signed in as the application user a8c1f0b4-1111-4a2b-9c3d-000000000042."), (tested.Success, tested.Message));
            Assert.Equal(new[] { TokenUrl, $"{Api}/WhoAmI" }, dataverse.Requests.Select(request => request.Url));
            var refused = await Test(new Dataverse { Answer = (_, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"0x80072560\",\"message\":\"The user is not a member of the organization.\"}}") });
            Assert.Equal((false, "HTTP 403 0x80072560: The user is not a member of the organization."), (refused.Success, refused.Message));
            Assert.False((await Test(new Dataverse(), "")).Success);
            var connector = new MicrosoftDynamicsConnector();
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "microsoftdynamics", 5, "https://kafe.crm4.dynamics.com"), (connector.AuthType, connector.ConnectorType, connector.Operations.Count, connector.EndpointExample));
        }
    }
}
