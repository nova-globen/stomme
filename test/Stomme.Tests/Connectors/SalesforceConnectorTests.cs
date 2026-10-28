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
using Stomme.Modules.Connectors.Salesforce;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Salesforce (n8n's Salesforce node: records searched by SOQL, got, created, updated, upserted and deleted): the
    /// connected app's client id and secret exchanged at the org's token endpoint, the access token a bearer; what a SOQL query
    /// finds read page by page by Salesforce's nextRecordsUrl - only its own query path - up to the most asked for, each record its
    /// attributes dropped and a related record flattened; a record got with the fields named, created, updated, upserted by an
    /// external id and deleted; an object, a record id, a field's name, fields that are not a JSON object and an API version
    /// refused before a token is asked for; Salesforce's refusal its first error; a test naming the org.
    /// </summary>
    public sealed class SalesforceConnectorTests
    {
        private const string Org = "https://acme.my.salesforce.example";

        private const string Api = Org + "/services/data/v62.0";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "key-1", ["client-secret"] = "secret-1", ["token-url"] = Org + "/services/oauth2/token" };

        private sealed class SalesforceApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.EndsWith("/services/oauth2/token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"access-1\",\"instance_url\":\"" + Org + "\",\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> ApiRequests =>
                Requests.Where(request => !request.Url.EndsWith("/services/oauth2/token", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(SalesforceApi salesforce, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(salesforce), Org + "/lightning/page/home", operation, parameters, settings, Credentials);

        private static SalesforceConnector Connector() => new(new OAuth2TokenProvider());

        private static string Contact(string id, string name) =>
            "{\"attributes\":{\"type\":\"Contact\",\"url\":\"/services/data/v62.0/sobjects/Contact/" + id + "\"},\"Id\":\"" + id + "\",\"Name\":\"" + name + "\",\"Email\":\"ada@example.com\",\"NumberOfEmployees\":12,\"HasOptedOutOfEmail\":false,\"Birthdate\":null,"
                + "\"Account\":{\"attributes\":{\"type\":\"Account\",\"url\":\"/services/data/v62.0/sobjects/Account/001A\"},\"Name\":\"Acme\",\"Owner\":{\"attributes\":{\"type\":\"User\"},\"Alias\":\"ops\"}}}";

        [Fact]
        public async Task What_a_soql_query_finds_is_read_page_by_page_by_its_own_next_records_url_up_to_the_most_asked_for()
        {
            var salesforce = new SalesforceApi
            {
                Answer = (_, url, _) => url.Contains("/query/01gNEXT-2000", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"totalSize\":4,\"done\":false,\"records\":[" + Contact("003000000000003AAA", "Grace") + "," + Contact("003000000000004AAA", "Joan") + "],\"nextRecordsUrl\":\"/services/data/v62.0/query/01gNEXT-4000\"}")
                    : (HttpStatusCode.OK, "{\"totalSize\":4,\"done\":false,\"records\":[" + Contact("003000000000001AAA", "Ada") + "," + Contact("003000000000002AAA", "Lin") + "],\"nextRecordsUrl\":\"/services/data/v62.0/query/01gNEXT-2000\"}"),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Id, Name, Account.Name FROM Contact WHERE Email != null", ["max"] = "3" }));

            Assert.Equal(new[] { "Ada", "Lin", "Grace" }, records.Select(record => (string?)record.Fields["Name"])); // the most asked for: no page more
            var calls = salesforce.ApiRequests;
            Assert.Equal(2, calls.Count);
            Assert.Equal($"{Api}/query?q=SELECT%20Id%2C%20Name%2C%20Account.Name%20FROM%20Contact%20WHERE%20Email%20%21%3D%20null", calls[0].Url); // the org's address, its page path dropped
            Assert.Equal($"{Api}/query/01gNEXT-2000", calls[1].Url);
            Assert.All(calls, call => Assert.Equal("Bearer access-1", call.Authorization));
            var ada = records[0].Fields;
            Assert.Equal(("003000000000001AAA", "ada@example.com", 12L, false, "Acme", "ops"), ((string?)ada["Id"], (string?)ada["Email"], (long?)ada["NumberOfEmployees"], (bool?)ada["HasOptedOutOfEmail"], (string?)ada["Account.Name"], (string?)ada["Account.Owner.Alias"]));
            Assert.False(ada.ContainsKey("attributes"));
            Assert.Null(ada["Birthdate"]);

            var exact = new SalesforceApi { Answer = salesforce.Answer };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(exact, SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Id FROM Contact", ["max"] = "2" }))).Count);
            Assert.Single(exact.ApiRequests); // a page ending at the most asked for: the next is not asked for

            var elsewhere = new SalesforceApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"done\":false,\"records\":[" + Contact("003000000000001AAA", "Ada") + "],\"nextRecordsUrl\":\"https://elsewhere.example/services/data/v62.0/query/x\"}") };
            Assert.Single(await Contexts.DrainAsync(Connector(), Context(elsewhere, SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Id FROM Contact" })));
            Assert.Single(elsewhere.ApiRequests); // a next page that is not the org's own query path is not followed
        }

        [Fact]
        public async Task A_record_is_got_created_updated_upserted_by_an_external_id_and_deleted()
        {
            var salesforce = new SalesforceApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("POST", _) => (HttpStatusCode.Created, "{\"id\":\"00Q000000000001AAA\",\"success\":true,\"errors\":[]}"),
                    ("PATCH", _) when url.Contains("/Email__c/", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"id\":\"00Q000000000002AAA\",\"success\":true,\"errors\":[],\"created\":true}"),
                    ("PATCH", _) or ("DELETE", _) => (HttpStatusCode.NoContent, ""),
                    _ => (HttpStatusCode.OK, Contact("003000000000001AAA", "Ada")),
                },
            };

            var read = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.GetOperation, new() { ["object"] = "Contact", ["id"] = "003000000000001AAA", ["fields"] = "Name, Email" }));
            Assert.Equal($"{Api}/sobjects/Contact/003000000000001AAA?fields=Name,Email", salesforce.ApiRequests[0].Url);
            Assert.Equal("Ada", Assert.Single(read).Fields["Name"]);

            var made = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.CreateOperation, new() { ["object"] = "Lead", ["fields"] = "{\"LastName\": \"Lovelace\", \"Company\": \"Acme\"}" }));
            Assert.Equal((HttpMethod.Post, $"{Api}/sobjects/Lead", "{\"LastName\":\"Lovelace\",\"Company\":\"Acme\"}"), (salesforce.ApiRequests[1].Method, salesforce.ApiRequests[1].Url, salesforce.ApiRequests[1].Body));
            Assert.Equal(("00Q000000000001AAA", "Lead"), ((string?)Assert.Single(made).Fields["id"], (string?)made[0].Fields["object"]));

            var changed = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.UpdateOperation, new() { ["object"] = "Lead", ["id"] = "00Q000000000001AAA", ["fields"] = "{\"Status\": \"Qualified\"}" }));
            Assert.Equal((HttpMethod.Patch, $"{Api}/sobjects/Lead/00Q000000000001AAA"), (salesforce.ApiRequests[2].Method, salesforce.ApiRequests[2].Url));
            Assert.Equal("Status", Assert.Single(changed).Fields["updated"]);

            var upserted = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.UpsertOperation, new() { ["object"] = "Lead", ["external_id_field"] = "Email__c", ["external_id"] = "ada+vip@example.com", ["fields"] = "{\"LastName\": \"Lovelace\"}" }));
            Assert.Equal((HttpMethod.Patch, $"{Api}/sobjects/Lead/Email__c/ada%2Bvip%40example.com"), (salesforce.ApiRequests[3].Method, salesforce.ApiRequests[3].Url));
            Assert.Equal(("00Q000000000002AAA", true), ((string?)Assert.Single(upserted).Fields["id"], (bool?)upserted[0].Fields["created"]));

            var removed = await Contexts.DrainAsync(Connector(), Context(salesforce, SalesforceConnector.DeleteOperation, new() { ["object"] = "Lead", ["id"] = "00Q000000000001AAA" }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/sobjects/Lead/00Q000000000001AAA"), (salesforce.ApiRequests[4].Method, salesforce.ApiRequests[4].Url));
            Assert.True((bool?)Assert.Single(removed).Fields["deleted"]);

            var older = new SalesforceApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"done\":true,\"records\":[]}") };
            await Contexts.DrainAsync(Connector(), Context(older, SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Id FROM Lead" }, new() { ["api_version"] = "v59.0" }));
            Assert.StartsWith(Org + "/services/data/v59.0/query?", older.ApiRequests[0].Url); // the API version the setting names
        }

        [Fact]
        public async Task What_could_reach_the_path_is_refused_before_a_token_is_asked_for_and_salesforces_refusal_is_its_first_error()
        {
            var salesforce = new SalesforceApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(salesforce, operation, parameters, settings)))).Message;

            Assert.StartsWith("'Contact/../User' is not a Salesforce object's API name", await Refused(SalesforceConnector.GetOperation, new() { ["object"] = "Contact/../User", ["id"] = "003000000000001AAA" }));
            Assert.StartsWith("'003/../x' is not a Salesforce record's id", await Refused(SalesforceConnector.DeleteOperation, new() { ["object"] = "Contact", ["id"] = "003/../x" }));
            Assert.StartsWith("'Name; DROP' is not a Salesforce field's API name", await Refused(SalesforceConnector.GetOperation, new() { ["object"] = "Contact", ["id"] = "003000000000001AAA", ["fields"] = "Name; DROP" }));
            Assert.StartsWith("'Last Name' is not a Salesforce field's API name", await Refused(SalesforceConnector.CreateOperation, new() { ["object"] = "Lead", ["fields"] = "{\"Last Name\": \"x\"}" }));
            Assert.Equal("The fields are not JSON - {\"LastName\": \"Lovelace\"}.", await Refused(SalesforceConnector.CreateOperation, new() { ["object"] = "Lead", ["fields"] = "LastName=Lovelace" }));
            Assert.Equal("The fields are not a JSON object.", await Refused(SalesforceConnector.CreateOperation, new() { ["object"] = "Lead", ["fields"] = "[{\"LastName\": \"x\"}]" }));
            Assert.Equal("Give at least one field.",await Refused(SalesforceConnector.UpdateOperation, new() { ["object"] = "Lead", ["id"] = "00Q000000000001AAA", ["fields"] = "{}" }));
            Assert.StartsWith("'Email/x' is not a Salesforce external id field's API name", await Refused(SalesforceConnector.UpsertOperation, new() { ["object"] = "Lead", ["external_id_field"] = "Email/x", ["external_id"] = "a", ["fields"] = "{\"LastName\": \"x\"}" }));
            Assert.StartsWith("'latest' is not an API version", await Refused(SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Id FROM Lead" }, new() { ["api_version"] = "latest" }));
            Assert.Empty(salesforce.Requests); // not even a token asked for

            var refusing = new SalesforceApi { Answer = (_, _, _) => (HttpStatusCode.BadRequest, "[{\"message\":\"No such column 'Nmae' on entity 'Contact'.\",\"errorCode\":\"INVALID_FIELD\"}]") };
            Assert.Equal("HTTP 400 INVALID_FIELD: No such column 'Nmae' on entity 'Contact'.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, SalesforceConnector.QueryOperation, new() { ["soql"] = "SELECT Nmae FROM Contact" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_org()
        {
            var salesforce = new SalesforceApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"DailyApiRequests\":{\"Max\":15000,\"Remaining\":14998}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(salesforce), Org, credentials: Credentials));
            Assert.Equal((true, "Signed in to acme.my.salesforce.example."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/limits", "Bearer access-1"), (salesforce.ApiRequests[0].Url, salesforce.ApiRequests[0].Authorization));
            Assert.Equal(("grant_type=client_credentials&client_id=key-1&client_secret=secret-1", Org + "/services/oauth2/token"), (salesforce.Requests[0].Body, salesforce.Requests[0].Url)); // no scope
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new SalesforceApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "[{\"message\":\"Session expired or invalid\",\"errorCode\":\"INVALID_SESSION_ID\"}]") }), Org, credentials: Credentials));
            Assert.Equal((false, "HTTP 401 INVALID_SESSION_ID: Session expired or invalid"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "salesforce", ConnectorCategory.Crm), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "query", "get", "create", "update", "upsert", "delete" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
