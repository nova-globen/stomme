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
using Stomme.Modules.Connectors.Grist;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Grist (n8n's Grist node: a table's rows created, read, updated, created or updated, and deleted): every call
    /// Grist's REST API with the API key as a bearer token; a table's records filtered by a JSON object (one value a list of one),
    /// sorted, no more than asked; a record got by its row id; created (one, or a list) with the ids Grist gave, updated,
    /// upserted by a match (on many as asked), deleted by their ids; a SELECT with its arguments; the documents, a document's
    /// tables and a table's columns; what could go wrong refused before anything is sent; Grist's word on a failure; a test
    /// naming the sites seen.
    /// </summary>
    public sealed class GristConnectorTests
    {
        private const string Api = "https://grist.example.test";

        private const string Doc = "fSLRDYaF3BWXAE6kNXF4bS";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "grist-key-1" };

        private sealed class Grist : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Grist grist, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(grist), Api, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        [Fact]
        public async Task Records_are_read_filtered_sorted_got_and_queried()
        {
            var grist = new Grist
            {
                Answer = (_, _) => (HttpStatusCode.OK, "{\"records\":[{\"id\":1,\"fields\":{\"Name\":\"Café 1\",\"Total\":99.5,\"Paid\":true,\"Tags\":[\"L\",\"a\",\"b\"],\"Owner\":{\"name\":\"Café\"},\"Note\":null,\"Count\":3}},{\"id\":2,\"fields\":{}},{\"id\":3,\"fields\":{}}]}"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new GristConnector(), Context(grist, operation, parameters));

            var records = await Run(GristConnector.RecordsOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["filter"] = "{\"Status\": [\"Paid\", \"Sent\"], \"Total\": 100}", ["sort"] = "Status, -Total:naturalSort", ["max"] = "2" });
            Assert.Equal(2, records.Count); // no more than asked
            Assert.Equal(("GET", $"{Api}/api/docs/{Doc}/tables/Orders/records?filter=%7B%22Status%22%3A%5B%22Paid%22%2C%22Sent%22%5D%2C%22Total%22%3A%5B100%5D%7D&sort=Status%2C-Total%3AnaturalSort&limit=2", "Bearer grist-key-1"),
                (grist.Requests[^1].Method.Method, grist.Requests[^1].Url, grist.Requests[^1].Authorization)); // one value a list of one
            var first = records[0].Fields;
            Assert.Equal((1L, "Café 1", 99.5, true, "[\"L\",\"a\",\"b\"]", "{\"name\":\"Café\"}", 3L), ((long?)first["id"], (string?)first["Name"], (double?)first["Total"], (bool?)first["Paid"], (string?)first["Tags"], (string?)first["Owner"], (long?)first["Count"]));
            Assert.Null(first["Note"]);
            Assert.Equal(3, (await Run(GristConnector.RecordsOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["filter"] = "{}" })).Count);
            Assert.Equal($"{Api}/api/docs/{Doc}/tables/Orders/records?limit=1000", grist.Requests[^1].Url); // an empty filter none

            grist.Answer = (_, _) => (HttpStatusCode.OK, "{\"records\":[{\"id\":42,\"fields\":{\"Name\":\"Café Ada\"}}]}");
            var got = Assert.Single(await Run(GristConnector.GetRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["id"] = "42", ["sort"] = "Name" })).Fields;
            Assert.Equal($"{Api}/api/docs/{Doc}/tables/Orders/records?filter=%7B%22id%22%3A%5B42%5D%7D", grist.Requests[^1].Url); // no sort and no limit on a get
            Assert.Equal((42L, "Café Ada"), ((long?)got["id"], (string?)got["Name"]));

            grist.Answer = (_, _) => (HttpStatusCode.OK, "{\"statement\":\"select Name from Orders\",\"records\":[{\"fields\":{\"Name\":\"Ada\"}},{\"fields\":{\"Name\":\"Bo\"}}]}");
            var queried = await Run(GristConnector.SqlOperation, new() { ["doc"] = Doc, ["query"] = "select Name from Orders where Status = ? and Total > ?", ["args"] = "[\"Paid\", 100]", ["max"] = "1" });
            Assert.Equal(("POST", $"{Api}/api/docs/{Doc}/sql", (string?)"{\"sql\":\"select Name from Orders where Status = ? and Total > ?\",\"args\":[\"Paid\",100]}"), (grist.Requests[^1].Method.Method, grist.Requests[^1].Url, grist.Requests[^1].Body));
            Assert.Equal(new[] { "Ada" }, queried.Select(record => (string?)record.Fields["Name"])); // no more than asked, no row id
            Assert.False(queried[0].Fields.ContainsKey("id"));

            grist.Answer = (_, _) => (HttpStatusCode.OK, "{\"records\":[]}");
            Assert.Equal("Orders has no record 42.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(GristConnector.GetRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["id"] = "42" }))).Message);
        }

        [Fact]
        public async Task A_record_is_created_updated_upserted_and_deleted()
        {
            var grist = new Grist { Answer = (_, _) => (HttpStatusCode.OK, "{\"records\":[{\"id\":43}]}") };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new GristConnector(), Context(grist, operation, parameters));
            (string, string, string?) Last() => (grist.Requests[^1].Method.Method, grist.Requests[^1].Url, grist.Requests[^1].Body);
            var path = $"{Api}/api/docs/{Doc}/tables/Orders/records";

            var made = Assert.Single(await Run(GristConnector.CreateRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["fields"] = "{\"Name\": \"Café Ada\", \"Total\": 99.5}" })).Fields;
            Assert.Equal(("POST", path, (string?)"{\"records\":[{\"fields\":{\"Name\":\"Café Ada\",\"Total\":99.5}}]}"), Last());
            Assert.Equal((43L, "Café Ada"), ((long?)made["id"], (string?)made["Name"])); // the id Grist gave
            grist.Answer = (_, _) => (HttpStatusCode.OK, "{\"records\":[{\"id\":44},{\"id\":45}]}");
            var many = await Run(GristConnector.CreateRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["fields"] = "[{\"Name\": \"A\"}, {\"Name\": \"B\"}, {\"Name\": \"C\"}]" });
            Assert.Equal("{\"records\":[{\"fields\":{\"Name\":\"A\"}},{\"fields\":{\"Name\":\"B\"}},{\"fields\":{\"Name\":\"C\"}}]}", Last().Item3);
            Assert.Equal(new (string?, long?)[] { ("A", 44L), ("B", 45L), ("C", null) }, many.Select(record => ((string?)record.Fields["Name"], record.Fields.TryGetValue("id", out var id) ? (long?)id : null)));

            grist.Answer = (_, _) => (HttpStatusCode.OK, "");
            var changed = Assert.Single(await Run(GristConnector.UpdateRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["id"] = "42", ["fields"] = "{\"Paid\": true}" })).Fields;
            Assert.Equal(("PATCH", path, (string?)"{\"records\":[{\"id\":42,\"fields\":{\"Paid\":true}}]}"), Last());
            Assert.Equal((42L, true), ((long?)changed["id"], (bool?)changed["Paid"]));

            var upserted = Assert.Single(await Run(GristConnector.UpsertRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["match"] = "{\"Email\": \"ada@example.com\"}", ["fields"] = "{\"Name\": \"Ada\"}" })).Fields;
            Assert.Equal(("PUT", path, (string?)"{\"records\":[{\"require\":{\"Email\":\"ada@example.com\"},\"fields\":{\"Name\":\"Ada\"}}]}"), Last());
            Assert.Equal(("ada@example.com", "Ada"), ((string?)upserted["Email"], (string?)upserted["Name"])); // the record as matched and written
            await Run(GristConnector.UpsertRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["match"] = "{\"Email\": \"ada@example.com\"}", ["on_many"] = "All" });
            Assert.Equal(("PUT", path + "?onmany=all", (string?)"{\"records\":[{\"require\":{\"Email\":\"ada@example.com\"},\"fields\":{}}]}"), Last()); // no fields the match alone
            await Run(GristConnector.UpsertRecordOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["match"] = "{\"Email\": \"ada@example.com\"}", ["on_many"] = "first" });
            Assert.Equal(path, Last().Item2); // first, Grist's own, left out

            var gone = await Run(GristConnector.DeleteRecordsOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["ids"] = "4, 7, 4" });
            Assert.Equal(("POST", path + "/delete", (string?)"[4,7]"), Last());
            Assert.Equal(new (string?, long?, bool?)[] { ("Orders", 4L, true), ("Orders", 7L, true) }, gone.Select(record => ((string?)record.Fields["table"], (long?)record.Fields["id"], (bool?)record.Fields["deleted"])));
        }

        [Fact]
        public async Task The_documents_tables_and_columns_are_listed()
        {
            var grist = new Grist
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/workspaces", StringComparison.Ordinal) ? "[{\"id\":97,\"name\":\"Home\",\"docs\":[{\"id\":\"d1\",\"name\":\"Shop\"},{\"id\":\"d2\",\"name\":\"CRM\"}]},{\"id\":98,\"name\":\"Team\",\"docs\":[{\"id\":\"d3\",\"name\":\"Plans\"}]}]"
                    : url.EndsWith("/columns", StringComparison.Ordinal) ? "{\"columns\":[{\"id\":\"Name\",\"fields\":{\"label\":\"Customer name\",\"type\":\"Text\"}},{\"id\":\"Total\",\"fields\":{\"label\":\"Total\",\"type\":\"Numeric\"}}]}"
                    : "{\"tables\":[{\"id\":\"Orders\",\"fields\":{\"tableRef\":1}},{\"id\":\"People\",\"fields\":{\"tableRef\":2}}]}"),
            };
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(new GristConnector(), Context(grist, operation, parameters));

            var docs = await Run(GristConnector.DocsOperation, new());
            Assert.Equal($"{Api}/api/orgs/current/workspaces", grist.Requests[^1].Url);
            Assert.Equal(new (string?, string?, string?)[] { ("d1", "Shop", "Home"), ("d2", "CRM", "Home"), ("d3", "Plans", "Team") }, docs.Select(item => ((string?)item.Fields["id"], (string?)item.Fields["name"], (string?)item.Fields["workspace"])));
            var tables = await Run(GristConnector.TablesOperation, new() { ["doc"] = Doc, ["table"] = "x y" });
            Assert.Equal($"{Api}/api/docs/{Doc}/tables", grist.Requests[^1].Url); // no table asked
            Assert.Equal(new[] { "Orders", "People" }, tables.Select(item => (string?)item.Fields["id"]));
            var columns = await Run(GristConnector.ColumnsOperation, new() { ["doc"] = Doc, ["table"] = "Orders" });
            Assert.Equal($"{Api}/api/docs/{Doc}/tables/Orders/columns", grist.Requests[^1].Url);
            Assert.Equal(("Name", "Customer name", "Text"), ((string?)columns[0].Fields["id"], (string?)columns[0].Fields["label"], (string?)columns[0].Fields["type"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_grists_word_on_a_failure()
        {
            var grist = new Grist();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GristConnector(), Context(grist, operation, parameters)))).Message;
            Dictionary<string, string> At(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["doc"] = Doc, ["table"] = "Orders" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.Equal("Give the document - its id.", await Refused(GristConnector.TablesOperation, new()));
            Assert.Equal("'../orgs' is not a document's id - letters, digits, _, - and ~.", await Refused(GristConnector.RecordsOperation, new() { ["doc"] = "../orgs", ["table"] = "Orders" }));
            Assert.Equal("Give the table - its id.", await Refused(GristConnector.ColumnsOperation, new() { ["doc"] = Doc }));
            Assert.Equal("'Orders table' is not a table's id - letters, digits and _.", await Refused(GristConnector.RecordsOperation, At(("table", "Orders table"))));
            Assert.Equal("Give the record - its row id.", await Refused(GristConnector.GetRecordOperation, At()));
            Assert.Equal("Give the record - its row id.", await Refused(GristConnector.UpdateRecordOperation, At(("fields", "{}"))));
            Assert.Equal("'0' is not a record's row id - a number above 0.", await Refused(GristConnector.GetRecordOperation, At(("id", "0"))));
            Assert.Equal("'4/2' is not a record's row id - a number above 0.", await Refused(GristConnector.GetRecordOperation, At(("id", "4/2"))));
            Assert.Equal("Give the records - their row ids.", await Refused(GristConnector.DeleteRecordsOperation, At()));
            Assert.Equal("'' is not a record's row id - a number above 0.", await Refused(GristConnector.DeleteRecordsOperation, At(("ids", "4,,7"))));
            Assert.Equal("Give up to 500 records at once.", await Refused(GristConnector.DeleteRecordsOperation, At(("ids", string.Join(",", Enumerable.Range(1, 501))))));
            Assert.Equal("The filter is not a JSON object - {\"Status\": [\"Paid\", \"Sent\"]}.", await Refused(GristConnector.RecordsOperation, At(("filter", "Status=Paid"))));
            Assert.Equal("'Paid?' is not a column's id (filter).", await Refused(GristConnector.RecordsOperation, At(("filter", "{\"Paid?\": [true]}"))));
            Assert.Equal("'Name,,Total' is not a list of columns - Status,-Total.", await Refused(GristConnector.RecordsOperation, At(("sort", "Name,,Total"))));
            Assert.Equal("'Name:upward' is not a list of columns - Status,-Total.", await Refused(GristConnector.RecordsOperation, At(("sort", "Name:upward"))));
            Assert.Equal("Give the fields - a JSON object.", await Refused(GristConnector.CreateRecordOperation, At()));
            Assert.Equal("The fields are not a JSON object - {\"Name\": \"Ada\"}.", await Refused(GristConnector.CreateRecordOperation, At(("fields", "Name=Ada"))));
            Assert.Equal("Give one to 500 records - JSON objects.", await Refused(GristConnector.CreateRecordOperation, At(("fields", "[1, 2]"))));
            Assert.Equal("Give one to 500 records - JSON objects.", await Refused(GristConnector.CreateRecordOperation, At(("fields", "[]"))));
            Assert.Equal("Give one to 500 records - JSON objects.", await Refused(GristConnector.CreateRecordOperation, At(("fields", "[" + string.Join(",", Enumerable.Range(1, 501).Select(_ => "{}")) + "]"))));
            Assert.Equal("Give one record's fields - a JSON object.", await Refused(GristConnector.UpdateRecordOperation, At(("id", "1"), ("fields", "[{\"a\": 1}]"))));
            Assert.Equal("Give the match - a JSON object of the fields a record is found by.", await Refused(GristConnector.UpsertRecordOperation, At()));
            Assert.Equal("The match is not a JSON object of fields - {\"Email\": \"ada@example.com\"}.", await Refused(GristConnector.UpsertRecordOperation, At(("match", "{}"))));
            Assert.Equal("'some' is not first, all or none (on many).", await Refused(GristConnector.UpsertRecordOperation, At(("match", "{\"Email\": \"a@example.com\"}"), ("on_many", "some"))));
            Assert.Equal("Give the query - one SELECT.", await Refused(GristConnector.SqlOperation, new() { ["doc"] = Doc }));
            Assert.Equal("The arguments are not a JSON list of texts and numbers - [\"Paid\", 100].", await Refused(GristConnector.SqlOperation, new() { ["doc"] = Doc, ["query"] = "select 1", ["args"] = "[true]" }));
            Assert.Equal("The arguments are not a JSON list of texts and numbers - [\"Paid\", 100].", await Refused(GristConnector.SqlOperation, new() { ["doc"] = Doc, ["query"] = "select 1", ["args"] = "\"Paid\"" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(GristConnector.RecordsOperation, At(("max", "0"))));
            Assert.StartsWith("Grist has no operation 'upload' - records, get_record,", await Refused("upload", new()));
            Assert.Empty(grist.Requests); // nothing sent

            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GristConnector(), Contexts.Extract(new StubEgress(grist), Api, GristConnector.DocsOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>())));
            Assert.Equal("The Grist connection has no API key.", keyless.Message);

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GristConnector(), Context(new Grist { Answer = (_, _) => (status, answer) }, GristConnector.RecordsOperation, At())))).Message;
            Assert.Equal("HTTP 404: Table not found \"Orders\"", await Failed(HttpStatusCode.NotFound, "{\"error\":\"Table not found \\\"Orders\\\"\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Grist answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no records
            Assert.Equal("HTTP 401.", await Failed(HttpStatusCode.Unauthorized, ""));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_sites_seen()
        {
            var grist = new Grist { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":42,\"name\":\"Personal\",\"domain\":\"docs-42\"},{\"id\":43,\"name\":\"Shop\",\"domain\":\"shop\"}]") };
            var tested = await new GristConnector().TestConnectionAsync(Contexts.Test(new StubEgress(grist), "", credentials: Credentials));
            Assert.Equal((true, "Grist: 2 sites seen."), (tested.Success, tested.Message));
            Assert.Equal(("https://docs.getgrist.com/api/orgs", "Bearer grist-key-1"), (grist.Requests[0].Url, grist.Requests[0].Authorization)); // Grist's cloud when no address is given
            var one = new Grist { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":42}]") };
            Assert.Equal("Grist: one site seen.", (await new GristConnector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", credentials: Credentials))).Message);
            var none = new Grist { Answer = (_, _) => (HttpStatusCode.OK, "[]") };
            var lonely = await new GristConnector().TestConnectionAsync(Contexts.Test(new StubEgress(none), "", credentials: Credentials));
            Assert.Equal((false, "Grist: the key sees no site."), (lonely.Success, lonely.Message));
            var refused = new Grist { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"Bad API key\"}") };
            var denied = await new GristConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), "", credentials: Credentials));
            Assert.Equal((false, "HTTP 401: Bad API key"), (denied.Success, denied.Message));
            Assert.Equal((AuthenticationType.ApiKey, "grist"), (new GristConnector().AuthType, new GristConnector().ConnectorType));
            Assert.Equal(10, new GristConnector().Operations.Count);
        }
    }
}
