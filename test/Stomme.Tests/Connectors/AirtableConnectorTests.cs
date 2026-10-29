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
using Stomme.Modules.Connectors.Airtable;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Airtable (n8n's search, create and update): the token as a bearer; a table's records read page by page, their
    /// fields, id and created_time, a view and a formula passed on; records created ten at a time with typecast, and updated by
    /// their ids, a throttled call sent again; a base or a record id that could reach the path refused before anything is sent;
    /// Airtable's refusal its error type; a test asking whom the token is.
    /// </summary>
    public sealed class AirtableConnectorTests
    {
        private const string BaseId = "appAbCdEfGhIjKlMn";

        private static readonly Dictionary<string, string> Token = new() { ["token"] = "pat-1" };

        private sealed class Airtable : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, """{"records":[]}""");

            /// <summary>How many calls are answered "too many requests, try again now" before Answer answers.</summary>
            public int Throttled { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (Throttled > 0)
                {
                    Throttled--;
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"errors":[{"error":"RATE_LIMIT_REACHED"}]}""", Encoding.UTF8, "application/json") };
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return throttled;
                }

                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Airtable airtable, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(airtable), "https://airtable.example.test/v0", operation, parameters, credentials: Token);

        [Fact]
        public async Task Records_are_read_page_by_page_with_their_fields_id_and_created_time()
        {
            var airtable = new Airtable
            {
                Answer = (_, url, _) => url.Contains("offset=", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"records":[{"id":"rec00000000000003","createdTime":"2026-10-03T08:00:00.000Z","fields":{"Name":"Lin"}}]}""")
                    : (HttpStatusCode.OK, """{"records":[{"id":"rec00000000000001","createdTime":"2026-10-01T08:00:00.000Z","fields":{"Name":"Ada","Total":12,"Paid":true,"Rate":2.5,"Tags":["a","b"]}},{"id":"rec00000000000002","createdTime":"2026-10-02T08:00:00.000Z","fields":{"Name":"Grace"}}],"offset":"itrNext/rec2"}"""),
            };
            var records = await Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.SearchOperation, new() { ["base"] = BaseId, ["table"] = "Orders 2026/Q4", ["view"] = "Open ones", ["formula"] = "{Status} = 'Open'" }));

            Assert.Equal(3, records.Count);
            var first = airtable.Requests[0];
            Assert.Equal(($"https://airtable.example.test/v0/{BaseId}/Orders%202026%2FQ4?pageSize=100&view=Open%20ones&filterByFormula=%7BStatus%7D%20%3D%20%27Open%27", "Bearer pat-1"), (first.Url, first.Authorization));
            Assert.EndsWith("&offset=itrNext%2Frec2", airtable.Requests[1].Url); // the next page, by its offset
            var ada = records[0].Fields;
            Assert.Equal(("rec00000000000001", "2026-10-01T08:00:00.000Z", "Ada", 12L, true, 2.5, "[\"a\",\"b\"]"),
                ((string?)ada["id"], (string?)ada["created_time"], (string?)ada["Name"], (long?)ada["Total"], (bool?)ada["Paid"], (double?)ada["Rate"], (string?)ada["Tags"]));
            Assert.Equal("Lin", records[2].Fields["Name"]);
        }

        [Fact]
        public async Task Records_are_created_ten_at_a_time_with_typecast_and_updated_by_their_ids()
        {
            var airtable = new Airtable
            {
                Answer = (_, _, body) =>
                {
                    var sent = JsonNode.Parse(body!)!["records"]!.AsArray();
                    var kept = new JsonArray(sent.Select((record, index) => (JsonNode?)new JsonObject
                    {
                        ["id"] = record!["id"]?.GetValue<string>() ?? "rec" + index.ToString("D14", System.Globalization.CultureInfo.InvariantCulture),
                        ["createdTime"] = "2026-10-11T09:00:00.000Z",
                        ["fields"] = record!["fields"]!.DeepClone(),
                    }).ToArray());
                    return (HttpStatusCode.OK, new JsonObject { ["records"] = kept }.ToJsonString());
                },
            };
            var twelve = "[" + string.Join(",", Enumerable.Range(1, 12).Select(n => "{\"Name\":\"N" + n + "\",\"Total\":\"" + n + "\"}")) + "]";
            var created = await Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.CreateOperation, new() { ["base"] = BaseId, ["table"] = "Orders", ["fields"] = twelve }));

            Assert.Equal(12, created.Count);
            Assert.Equal(new[] { 10, 2 }, airtable.Requests.Select(request => JsonNode.Parse(request.Body!)!["records"]!.AsArray().Count)); // ten at a time
            var batch = JsonNode.Parse(airtable.Requests[0].Body!)!;
            Assert.Equal((HttpMethod.Post, true, "N1"), (airtable.Requests[0].Method, batch["typecast"]!.GetValue<bool>(), batch["records"]![0]!["fields"]!["Name"]!.GetValue<string>()));

            var updating = new Airtable { Answer = airtable.Answer, Throttled = 1 }; // throttled once: the same body sent again
            var expected = """{"records":[{"id":"rec00000000000001","fields":{"Status":"Done"}}],"typecast":true}""";
            var updated = await Contexts.DrainAsync(new AirtableConnector(), Context(updating, AirtableConnector.UpdateOperation, new() { ["base"] = BaseId, ["table"] = "Orders", ["records"] = """{"id":"rec00000000000001","Status":"Done"}""" }));
            Assert.Equal(new[] { (HttpMethod.Patch, expected), (HttpMethod.Patch, expected) }, updating.Requests.Select(request => (request.Method, request.Body!)));
            Assert.Equal(("rec00000000000001", "Done"), ((string?)Assert.Single(updated).Fields["id"], (string?)updated[0].Fields["Status"]));
        }

        [Fact]
        public async Task A_base_or_record_id_that_could_reach_the_path_is_refused_before_anything_is_sent_and_airtables_refusal_is_its_type()
        {
            var airtable = new Airtable();
            Assert.StartsWith("'../meta' is not a base id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.SearchOperation, new() { ["base"] = "../meta", ["table"] = "Orders" })))).Message);
            Assert.StartsWith("'rec1/../x' is not a record id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.UpdateOperation, new() { ["base"] = BaseId, ["table"] = "Orders", ["records"] = """{"id":"rec1/../x","Status":"Done"}""" })))).Message);
            Assert.StartsWith("'12' is not a record id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.UpdateOperation, new() { ["base"] = BaseId, ["table"] = "Orders", ["records"] = """{"id":12,"Status":"Done"}""" })))).Message);
            Assert.Equal("The records are not a record or a list of records.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(airtable, AirtableConnector.CreateOperation, new() { ["base"] = BaseId, ["table"] = "Orders", ["fields"] = "[1, 2]" })))).Message);
            Assert.Empty(airtable.Requests);

            var missing = new Airtable { Answer = (_, _, _) => (HttpStatusCode.NotFound, """{"error":{"type":"TABLE_NOT_FOUND","message":"Could not find table Orderz"}}""") };
            Assert.Equal("HTTP 404 TABLE_NOT_FOUND.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(missing, AirtableConnector.SearchOperation, new() { ["base"] = BaseId, ["table"] = "Orderz" })))).Message);
            var plain = new Airtable { Answer = (_, _, _) => (HttpStatusCode.NotFound, """{"error":"NOT_FOUND"}""") };
            Assert.Equal("HTTP 404 NOT_FOUND.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AirtableConnector(), Context(plain, AirtableConnector.SearchOperation, new() { ["base"] = BaseId, ["table"] = "Orders" })))).Message);
        }

        [Fact]
        public async Task A_test_asks_whom_the_token_is()
        {
            var airtable = new Airtable { Answer = (_, _, _) => (HttpStatusCode.OK, """{"id":"usrAbc","email":"ops@example.com","scopes":["data.records:read"]}""") };
            var tested = await new AirtableConnector().TestConnectionAsync(Contexts.Test(new StubEgress(airtable), "", credentials: Token));
            Assert.Equal((true, "Signed in as ops@example.com."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.airtable.com/v0/meta/whoami", "Bearer pat-1"), (airtable.Requests[0].Url, airtable.Requests[0].Authorization));
            var refused = await new AirtableConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Airtable { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, """{"error":{"type":"AUTHENTICATION_REQUIRED"}}""") }), "", credentials: Token));
            Assert.Equal((false, "HTTP 401 AUTHENTICATION_REQUIRED."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "airtable"), (new AirtableConnector().AuthType, new AirtableConnector().ConnectorType));
            Assert.Equal(new[] { "search", "create", "update" }, new AirtableConnector().Operations.Select(operation => operation.Name));
        }
    }
}
