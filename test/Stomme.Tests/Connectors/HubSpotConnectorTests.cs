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
using Stomme.Modules.Connectors.HubSpot;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// HubSpot (n8n's Get Many, Search, Create, Update and Create or Update): the private app's token as a bearer; an
    /// object's records searched page by page by their cursor up to the most asked for, a search, filters and properties passed
    /// on, each record its id, times and properties; records created, updated by their ids and upserted by a property, in
    /// HubSpot's batches of 100, each property as text; an object, an id or a property's name that could reach the path or the
    /// request refused before anything is sent; HubSpot's refusal its category; a test naming the account.
    /// </summary>
    public sealed class HubSpotConnectorTests
    {
        private static readonly Dictionary<string, string> Token = new() { ["token"] = "pat-1" };

        private sealed class HubSpot : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, """{"results":[]}""");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(HubSpot hubspot, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(hubspot), "https://hubspot.example.test", operation, parameters, credentials: Token);

        private static string Found(string id, string email) =>
            $$"""{"id":"{{id}}","properties":{"email":"{{email}}","firstname":"Ada","hs_object_id":"{{id}}"},"createdAt":"2026-10-01T08:00:00Z","updatedAt":"2026-10-02T08:00:00Z","archived":false}""";

        [Fact]
        public async Task An_objects_records_are_searched_page_by_page_up_to_the_most_asked_for()
        {
            var hubspot = new HubSpot
            {
                Answer = (_, _, body) => JsonNode.Parse(body!)!["after"] is null
                    ? (HttpStatusCode.OK, """{"total":4,"results":[""" + Found("101", "ada@example.com") + "," + Found("102", "lin@example.com") + """],"paging":{"next":{"after":"102"}}}""")
                    : (HttpStatusCode.OK, """{"total":4,"results":[""" + Found("103", "grace@example.com") + "," + Found("104", "joan@example.com") + """],"paging":{"next":{"after":"104"}}}"""),
            };
            var records = await Contexts.DrainAsync(new HubSpotConnector(), Context(hubspot, HubSpotConnector.SearchOperation, new()
            {
                ["object"] = "contacts", ["query"] = "example.com", ["filters"] = "lifecyclestage = customer\nhs_lead_status = OPEN", ["properties"] = "email, firstname", ["max"] = "3",
            }));

            Assert.Equal(new[] { "101", "102", "103" }, records.Select(record => (string)record.Fields["id"]!)); // at most three - no third page
            Assert.Equal(2, hubspot.Requests.Count);
            var first = hubspot.Requests[0];
            Assert.Equal((HttpMethod.Post, "https://hubspot.example.test/crm/v3/objects/contacts/search", "Bearer pat-1"), (first.Method, first.Url, first.Authorization));
            Assert.Equal("""{"limit":3,"query":"example.com","filterGroups":[{"filters":[{"propertyName":"lifecyclestage","operator":"EQ","value":"customer"},{"propertyName":"hs_lead_status","operator":"EQ","value":"OPEN"}]}],"properties":["email","firstname"]}""", first.Body);
            Assert.Equal((1, "102"), (JsonNode.Parse(hubspot.Requests[1].Body!)!["limit"]!.GetValue<int>(), JsonNode.Parse(hubspot.Requests[1].Body!)!["after"]!.GetValue<string>()));
            var ada = records[0].Fields;
            Assert.Equal(("ada@example.com", "Ada", "2026-10-01T08:00:00Z", "2026-10-02T08:00:00Z", false), ((string?)ada["email"], (string?)ada["firstname"], (string?)ada["created_at"], (string?)ada["updated_at"], (bool?)ada["archived"]));
        }

        [Fact]
        public async Task Records_are_created_updated_and_upserted_in_batches_of_a_hundred_each_property_as_text()
        {
            var hubspot = new HubSpot
            {
                Answer = (_, _, body) =>
                {
                    var inputs = JsonNode.Parse(body!)!["inputs"]!.AsArray();
                    return (HttpStatusCode.OK, new JsonObject
                    {
                        ["results"] = new JsonArray(inputs.Select((input, index) => (JsonNode?)new JsonObject
                        {
                            ["id"] = input!["id"]?.GetValue<string>() is { } id && id.All(char.IsAsciiDigit) ? id : (500 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["properties"] = input["properties"]!.DeepClone(),
                        }).ToArray()),
                    }.ToJsonString());
                },
            };
            var many = "[" + string.Join(",", Enumerable.Range(1, 101).Select(n => "{\"name\":\"Company " + n + "\",\"numberofemployees\":" + n + ",\"is_public\":true}")) + "]";
            var created = await Contexts.DrainAsync(new HubSpotConnector(), Context(hubspot, HubSpotConnector.CreateOperation, new() { ["object"] = "companies", ["properties"] = many }));

            Assert.Equal(101, created.Count);
            Assert.Equal(new[] { 100, 1 }, hubspot.Requests.Select(request => JsonNode.Parse(request.Body!)!["inputs"]!.AsArray().Count)); // a hundred at a time
            Assert.Equal("https://hubspot.example.test/crm/v3/objects/companies/batch/create", hubspot.Requests[0].Url);
            Assert.Equal("""{"properties":{"name":"Company 1","numberofemployees":"1","is_public":"true"}}""", JsonNode.Parse(hubspot.Requests[0].Body!)!["inputs"]![0]!.ToJsonString());

            var updating = new HubSpot { Answer = hubspot.Answer };
            var updated = await Contexts.DrainAsync(new HubSpotConnector(), Context(updating, HubSpotConnector.UpdateOperation, new() { ["object"] = "deals", ["records"] = """{"id":"9001","dealstage":"closedwon","amount":1200,"closedate":null}""" }));
            Assert.Equal(("https://hubspot.example.test/crm/v3/objects/deals/batch/update", """{"inputs":[{"id":"9001","properties":{"dealstage":"closedwon","amount":"1200","closedate":""}}]}"""), (updating.Requests[0].Url, updating.Requests[0].Body));
            Assert.Equal(("9001", "closedwon"), ((string?)Assert.Single(updated).Fields["id"], (string?)updated[0].Fields["dealstage"]));

            var upserting = new HubSpot { Answer = hubspot.Answer };
            await Contexts.DrainAsync(new HubSpotConnector(), Context(upserting, HubSpotConnector.UpsertOperation, new() { ["records"] = """[{"email":"ada@example.com","firstname":"Ada"}]""" }));
            Assert.Equal(("https://hubspot.example.test/crm/v3/objects/contacts/batch/upsert", """{"inputs":[{"idProperty":"email","id":"ada@example.com","properties":{"email":"ada@example.com","firstname":"Ada"}}]}"""), (upserting.Requests[0].Url, upserting.Requests[0].Body));
        }

        [Fact]
        public async Task What_could_reach_the_path_or_the_request_is_refused_before_anything_is_sent_and_hubspots_refusal_is_its_category()
        {
            var hubspot = new HubSpot();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new HubSpotConnector(), Context(hubspot, operation, parameters)))).Message;

            Assert.StartsWith("'../owners' is not a HubSpot object", await Refused(HubSpotConnector.SearchOperation, new() { ["object"] = "../owners" }));
            Assert.StartsWith("'1/../2' is not a HubSpot record id", await Refused(HubSpotConnector.UpdateOperation, new() { ["records"] = """{"id":"1/../2","firstname":"Ada"}""" }));
            Assert.StartsWith("'First Name' is not a HubSpot property's name", await Refused(HubSpotConnector.CreateOperation, new() { ["properties"] = """{"First Name":"Ada"}""" }));
            Assert.Equal("A record has no email - each is found by it.", await Refused(HubSpotConnector.UpsertOperation, new() { ["records"] = """{"firstname":"Ada"}""" }));
            Assert.Equal("A record has no email - each is found by it.", await Refused(HubSpotConnector.UpsertOperation, new() { ["records"] = """[{"email":"ada@example.com"},{"email":"","firstname":"Lin"}]""" })); // an empty one finds nothing
            Assert.StartsWith("'lifecyclestage' is not 'property = value'", await Refused(HubSpotConnector.SearchOperation, new() { ["filters"] = "lifecyclestage" }));
            Assert.Empty(hubspot.Requests);

            var refusing = new HubSpot { Answer = (_, _, _) => (HttpStatusCode.BadRequest, """{"status":"error","message":"Property values were not valid","category":"VALIDATION_ERROR"}""") };
            Assert.Equal("HTTP 400 VALIDATION_ERROR.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new HubSpotConnector(), Context(refusing, HubSpotConnector.CreateOperation, new() { ["properties"] = """{"email":"x"}""" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var hubspot = new HubSpot { Answer = (_, _, _) => (HttpStatusCode.OK, """{"portalId":4711,"accountType":"STANDARD"}""") };
            var tested = await new HubSpotConnector().TestConnectionAsync(Contexts.Test(new StubEgress(hubspot), "", credentials: Token));
            Assert.Equal((true, "Signed in to account 4711."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.hubapi.com/account-info/v3/details", "Bearer pat-1"), (hubspot.Requests[0].Url, hubspot.Requests[0].Authorization));
            Assert.Equal((AuthenticationType.Bearer, "hubspot"), (new HubSpotConnector().AuthType, new HubSpotConnector().ConnectorType));
            Assert.Equal(new[] { "search", "create", "update", "upsert" }, new HubSpotConnector().Operations.Select(operation => operation.Name));
        }
    }
}
