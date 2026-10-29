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
using Stomme.Modules.Connectors.Notion;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Notion (n8n's database pages got, created and updated): the integration's secret as the bearer, the API version
    /// named; a database's pages read cursor by cursor, a filter and sorts passed on, each property a plain value; pages created
    /// with their fields typed by the database's properties, and updated typed by the page's own, what no property takes left out
    /// and said, every page typed before the first is sent; a database or page named by its address; a throttled call sent
    /// again; an id that could reach the path refused before anything is sent; Notion's refusal its code; a test asking who the
    /// integration is.
    /// </summary>
    public sealed class NotionConnectorTests
    {
        private const string Database = "0123456789abcdef0123456789abcdef";
        private const string DatabaseDashed = "01234567-89ab-cdef-0123-456789abcdef";
        private const string Page = "fedcba98-7654-3210-fedc-ba9876543210";

        private static readonly Dictionary<string, string> Secret = new() { ["token"] = "ntn-1" };

        private sealed class Notion : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Version, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, """{"results":[],"has_more":false}""");

            /// <summary>How many calls are answered "too many requests, try again now" before Answer answers.</summary>
            public int Throttled { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Notion-Version", out var versions) ? versions.Single() : null, body));
                if (Throttled > 0)
                {
                    Throttled--;
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"object":"error","status":429,"code":"rate_limited"}""", Encoding.UTF8, "application/json") };
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return throttled;
                }

                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Notion notion, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(notion), "https://notion.example.test/v1", operation, parameters, settings, credentials: Secret);

        // A database's schema: one property of each type the tests write, and two Notion computes.
        private const string Schema = """
            {"object":"database","id":"01234567-89ab-cdef-0123-456789abcdef","properties":{
              "Name":{"id":"title","type":"title","title":{}},
              "Notes":{"id":"a","type":"rich_text","rich_text":{}},
              "Total":{"id":"b","type":"number","number":{}},
              "Paid":{"id":"c","type":"checkbox","checkbox":{}},
              "Status":{"id":"d","type":"status","status":{}},
              "Tags":{"id":"e","type":"multi_select","multi_select":{}},
              "Due":{"id":"f","type":"date","date":{}},
              "Link":{"id":"g","type":"url","url":{}},
              "Score":{"id":"h","type":"formula","formula":{}},
              "Created":{"id":"i","type":"created_time","created_time":{}}}}
            """;

        // A page as Notion answers one: its properties of every kind the reader flattens.
        private static string PageJson(string id, string name, bool paid) => """
            {"object":"page","id":"@ID","url":"https://www.notion.so/@NAME-@SLUG","created_time":"2026-10-01T08:00:00.000Z","last_edited_time":"2026-10-02T08:00:00.000Z",
             "properties":{
              "Name":{"id":"title","type":"title","title":[{"plain_text":"@NAME "},{"plain_text":"Lovelace"}]},
              "Notes":{"id":"a","type":"rich_text","rich_text":[]},
              "Total":{"id":"b","type":"number","number":12},
              "Rate":{"id":"r","type":"number","number":2.5},
              "Paid":{"id":"c","type":"checkbox","checkbox":@PAID},
              "Status":{"id":"d","type":"status","status":{"name":"Open"}},
              "Kind":{"id":"k","type":"select","select":null},
              "Tags":{"id":"e","type":"multi_select","multi_select":[{"name":"a"},{"name":"b"}]},
              "Due":{"id":"f","type":"date","date":{"start":"2026-10-12","end":"2026-10-14"}},
              "Link":{"id":"g","type":"url","url":"https://example.com"},
              "Owner":{"id":"p","type":"people","people":[{"object":"user","id":"u1","name":"Grace"}]},
              "Orders":{"id":"o","type":"relation","relation":[{"id":"r1"},{"id":"r2"}]},
              "Score":{"id":"h","type":"formula","formula":{"type":"number","number":7}},
              "Sum":{"id":"s","type":"rollup","rollup":{"type":"number","number":30}},
              "Ref":{"id":"u","type":"unique_id","unique_id":{"prefix":"ORD","number":42}},
              "By":{"id":"y","type":"created_by","created_by":{"object":"user","id":"u2","name":"Lin"}}}}
            """.Replace("@ID", id, StringComparison.Ordinal).Replace("@SLUG", id.Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("@NAME", name, StringComparison.Ordinal).Replace("@PAID", paid ? "true" : "false", StringComparison.Ordinal);

        [Fact]
        public async Task A_databases_pages_are_read_cursor_by_cursor_each_property_a_plain_value()
        {
            var notion = new Notion
            {
                Answer = (_, _, body) => JsonNode.Parse(body!)!["start_cursor"] is null
                    ? (HttpStatusCode.OK, $$"""{"results":[{{PageJson(Page, "Ada", paid: true)}}],"has_more":true,"next_cursor":"cursor-2"}""")
                    : (HttpStatusCode.OK, $$"""{"results":[{{PageJson("11111111-2222-3333-4444-555555555555", "Lin", paid: false)}}],"has_more":false,"next_cursor":null}"""),
            };
            var records = await Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.QueryOperation, new()
            {
                ["database"] = $"https://www.notion.so/acme/Orders-{Database}?v=99999999999999999999999999999999",
                ["filter"] = """{"property":"Status","status":{"equals":"Open"}}""",
                ["sorts"] = """{"property":"Name","direction":"ascending"}""",
            }));

            Assert.Equal(2, records.Count);
            var first = notion.Requests[0];
            Assert.Equal((HttpMethod.Post, $"https://notion.example.test/v1/databases/{DatabaseDashed}/query", "Bearer ntn-1", NotionConnector.ApiVersion), (first.Method, first.Url, first.Authorization, first.Version));
            Assert.Equal("""{"page_size":100,"filter":{"property":"Status","status":{"equals":"Open"}},"sorts":[{"property":"Name","direction":"ascending"}]}""", first.Body);
            Assert.Equal("cursor-2", JsonNode.Parse(notion.Requests[1].Body!)!["start_cursor"]!.GetValue<string>()); // the next, by its cursor
            var ada = records[0].Fields;
            Assert.Equal((Page, "2026-10-01T08:00:00.000Z", "2026-10-02T08:00:00.000Z"), ((string?)ada["id"], (string?)ada["created_time"], (string?)ada["last_edited_time"]));
            Assert.Equal(("Ada Lovelace", "", 12L, 2.5, true, "Open", (string?)null), ((string?)ada["Name"], (string?)ada["Notes"], (long?)ada["Total"], (double?)ada["Rate"], (bool?)ada["Paid"], (string?)ada["Status"], (string?)ada["Kind"]));
            Assert.Equal(("[\"a\",\"b\"]", "2026-10-12/2026-10-14", "https://example.com", "[\"Grace\"]", "[\"r1\",\"r2\"]"), ((string?)ada["Tags"], (string?)ada["Due"], (string?)ada["Link"], (string?)ada["Owner"], (string?)ada["Orders"]));
            Assert.Equal((7L, 30L, "ORD-42", "Lin"), ((long?)ada["Score"], (long?)ada["Sum"], (string?)ada["Ref"], (string?)ada["By"]));
            Assert.Equal((false, "Lin Lovelace"), ((bool?)records[1].Fields["Paid"], (string?)records[1].Fields["Name"]));
        }

        [Fact]
        public async Task Pages_are_created_their_fields_typed_by_the_databases_properties_what_none_takes_left_out_and_said()
        {
            var notion = new Notion
            {
                Answer = (method, url, body) => method == HttpMethod.Get ? (HttpStatusCode.OK, Schema) : (HttpStatusCode.OK, PageJson(Page, "Ada", paid: true)),
            };
            var notes = new string('n', 2000) + "x"; // Notion takes 2000 characters in one piece of text
            var created = await Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.CreateOperation, new()
            {
                ["properties"] = $$"""[{"title":"Ada","Notes":"{{notes}}","Total":"12","Paid":"true","Status":"Open","Tags":"a, b","Due":"2026-10-12/2026-10-14","Link":"https://example.com","Score":3,"Unknown":"?"},{"Name":"Lin","Paid":null}]""",
            }, settings: new() { ["database"] = DatabaseDashed }));

            Assert.Equal(2, created.Count);
            Assert.Equal((HttpMethod.Get, $"https://notion.example.test/v1/databases/{DatabaseDashed}"), (notion.Requests[0].Method, notion.Requests[0].Url)); // the schema first
            Assert.All(notion.Requests.Skip(1), request => Assert.Equal((HttpMethod.Post, "https://notion.example.test/v1/pages"), (request.Method, request.Url)));
            var sent = JsonNode.Parse(notion.Requests[1].Body!)!;
            Assert.Equal(DatabaseDashed, sent["parent"]!["database_id"]!.GetValue<string>());
            var properties = sent["properties"]!;
            Assert.Equal("""{"title":[{"text":{"content":"Ada"}}]}""", properties["Name"]!.ToJsonString()); // "title" fills the title property
            Assert.Equal(new[] { 2000, 1 }, properties["Notes"]!["rich_text"]!.AsArray().Select(piece => piece!["text"]!["content"]!.GetValue<string>().Length)); // in pieces Notion takes
            Assert.Equal(("""{"number":12}""", """{"checkbox":true}""", """{"status":{"name":"Open"}}"""), (properties["Total"]!.ToJsonString(), properties["Paid"]!.ToJsonString(), properties["Status"]!.ToJsonString()));
            Assert.Equal(("""{"multi_select":[{"name":"a"},{"name":"b"}]}""", """{"date":{"start":"2026-10-12","end":"2026-10-14"}}""", """{"url":"https://example.com"}"""), (properties["Tags"]!.ToJsonString(), properties["Due"]!.ToJsonString(), properties["Link"]!.ToJsonString()));
            Assert.Null(properties["Score"]); // Notion computes it
            Assert.Equal(("Score, Unknown", ""), ((string?)created[0].Fields["left_out"], (string?)created[1].Fields["left_out"]));
            Assert.Equal("""{"checkbox":false}""", JsonNode.Parse(notion.Requests[2].Body!)!["properties"]!["Paid"]!.ToJsonString()); // nothing unticks it

            var refusing = new Notion { Answer = notion.Answer };
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(refusing, NotionConnector.CreateOperation, new()
            {
                ["database"] = Database,
                ["properties"] = """[{"Name":"Ada"},{"Name":"Lin","Total":"twelve"}]""",
            })));
            Assert.Equal("'twelve' is not a number - for Total.", refused.Message);
            Assert.Single(refusing.Requests); // the schema only: no page created before the second was typed
        }

        [Fact]
        public async Task A_page_is_updated_typed_by_its_own_properties_named_by_its_address_a_throttled_call_sent_again()
        {
            var notion = new Notion { Answer = (_, _, _) => (HttpStatusCode.OK, PageJson(Page, "Ada", paid: false)), Throttled = 1 };
            var updated = await Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.UpdateOperation, new()
            {
                ["pages"] = $$"""{"id":"https://www.notion.so/Ada-{{Page.Replace("-", "")}}","Paid":true,"Kind":"Rush","Due":null}""",
            }));

            var calls = notion.Requests.Select(request => (request.Method, request.Url)).ToList();
            var at = $"https://notion.example.test/v1/pages/{Page}";
            Assert.Equal(new[] { (HttpMethod.Get, at), (HttpMethod.Get, at), (HttpMethod.Patch, at) }, calls); // throttled once: asked again
            Assert.Equal("""{"properties":{"Paid":{"checkbox":true},"Kind":{"select":{"name":"Rush"}},"Due":{"date":null}}}""", notion.Requests[2].Body);
            Assert.Equal((Page, ""), ((string?)Assert.Single(updated).Fields["id"], (string?)updated[0].Fields["left_out"]));
        }

        [Fact]
        public async Task An_id_that_could_reach_the_path_is_refused_before_anything_is_sent_and_notions_refusal_is_its_code()
        {
            var notion = new Notion();
            Assert.StartsWith("'../users' is not a Notion database id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.QueryOperation, new() { ["database"] = "../users" })))).Message);
            Assert.StartsWith($"'https://www.notion.so/abc{Database}' is not a Notion database id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.QueryOperation, new() { ["database"] = $"https://www.notion.so/abc{Database}" })))).Message); // a longer run: no id at all
            Assert.StartsWith("'https://evil.example/x' is not a Notion page id", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.UpdateOperation, new() { ["pages"] = """{"id":"https://evil.example/x","Paid":true}""" })))).Message);
            Assert.Equal("The filter is not JSON - Notion's filter object.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(notion, NotionConnector.QueryOperation, new() { ["database"] = Database, ["filter"] = "Status = Open" })))).Message);
            Assert.Empty(notion.Requests);

            var missing = new Notion { Answer = (_, _, _) => (HttpStatusCode.NotFound, """{"object":"error","status":404,"code":"object_not_found","message":"Could not find database"}""") };
            Assert.Equal("HTTP 404 object_not_found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new NotionConnector(), Context(missing, NotionConnector.QueryOperation, new() { ["database"] = Database })))).Message);
        }

        [Fact]
        public async Task A_test_asks_who_the_integration_is()
        {
            var notion = new Notion { Answer = (_, _, _) => (HttpStatusCode.OK, """{"object":"user","id":"b1","name":"Orders bot","type":"bot","bot":{"workspace_name":"Acme"}}""") };
            var tested = await new NotionConnector().TestConnectionAsync(Contexts.Test(new StubEgress(notion), "", credentials: Secret));
            Assert.Equal((true, "Signed in as Orders bot in Acme."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.notion.com/v1/users/me", "Bearer ntn-1", NotionConnector.ApiVersion), (notion.Requests[0].Url, notion.Requests[0].Authorization, notion.Requests[0].Version));
            var refused = await new NotionConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Notion { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, """{"object":"error","status":401,"code":"unauthorized"}""") }), "", credentials: Secret));
            Assert.Equal((false, "HTTP 401 unauthorized."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "notion"), (new NotionConnector().AuthType, new NotionConnector().ConnectorType));
            Assert.Equal(new[] { "query", "create", "update" }, new NotionConnector().Operations.Select(operation => operation.Name));
        }
    }
}
