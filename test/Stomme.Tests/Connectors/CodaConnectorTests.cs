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
using Stomme.Modules.Connectors.Coda;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Coda (n8n's Coda node: a table's or a view's rows read, got, created, upserted, updated and deleted, its buttons
    /// pushed, its columns, a doc's formulas and controls read): every call Coda's API v1 with the token as a bearer token; rows
    /// page by page by Coda's page token - a page no bigger than what is left - with a query, a sort, visible rows only and a
    /// value format, each value under its column's name or id; a row got; rows created (one or a list, with the ids Coda added)
    /// or upserted by key columns, a row updated, rows deleted, a button pushed - each with the request Coda gave, and waited for
    /// when asked; the docs, a doc's tables and views, a table's columns, a column, the formulas and controls and their values; a
    /// table, row or column by its name escaped in the path; what could go wrong refused before anything is sent; Coda's word
    /// on a failure; a test naming the token.
    /// </summary>
    public sealed class CodaConnectorTests
    {
        private const string Api = "https://coda.example.test";

        private const string Doc = "AbCDeFGH";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "coda-token-1" };

        private sealed class Coda : HttpMessageHandler
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

        private static ConnectorExtractContext Context(Coda coda, string operation, Dictionary<string, string> parameters, string endpoint = Api) =>
            Contexts.Extract(new StubEgress(coda), endpoint, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials);

        private static Task<List<ConnectorRecord>> Run(Coda coda, string operation, Dictionary<string, string> parameters, string endpoint = Api) =>
            Contexts.DrainAsync(new CodaConnector(TimeSpan.Zero), Context(coda, operation, parameters, endpoint));

        private static string Rows(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"i-" + i + "\",\"type\":\"row\",\"name\":\"Café " + i + "\",\"index\":" + i + ",\"values\":{\"Name\":\"Café " + i + "\",\"Total\":99.5,\"Paid\":true,\"Tags\":[\"a\",\"b\"],\"Owner\":{\"name\":\"Café\"},\"Note\":null,\"Count\":3}}")) + "]";

        [Fact]
        public async Task Rows_are_read_page_by_page_queried_sorted_and_got()
        {
            var coda = new Coda
            {
                Answer = (_, url) => (HttpStatusCode.OK, url.Contains("pageToken=", StringComparison.Ordinal) ? "{\"items\":" + Rows(101, 50) + "}" : "{\"items\":" + Rows(1, 100) + ",\"nextPageToken\":\"eyJ+/=\"}"),
            };
            var path = $"{Api}/apis/v1/docs/{Doc}/tables/grid-pqRst-U/rows";

            var rows = await Run(coda, CodaConnector.RowsOperation, new() { ["doc"] = Doc, ["table"] = "grid-pqRst-U", ["query"] = "Status:\"Paid\"", ["sort_by"] = "Natural", ["visible_only"] = "TRUE", ["value_format"] = "simplewitharrays", ["max"] = "1000" });
            Assert.Equal(150, rows.Count);
            Assert.Equal(new[]
            {
                $"{path}?useColumnNames=true&valueFormat=simpleWithArrays&query=Status%3A%22Paid%22&sortBy=natural&visibleOnly=true&limit=100",
                $"{path}?useColumnNames=true&valueFormat=simpleWithArrays&query=Status%3A%22Paid%22&sortBy=natural&visibleOnly=true&limit=100&pageToken=eyJ%2B%2F%3D",
            }, coda.Requests.Select(request => request.Url)); // a page token escaped, no token the end
            Assert.All(coda.Requests, request => Assert.Equal("Bearer coda-token-1", request.Authorization));
            var first = rows[0].Fields;
            Assert.Equal(("i-1", "Café 1", 99.5, true, "[\"a\",\"b\"]", "{\"name\":\"Café\"}", 3L), ((string?)first["id"], (string?)first["Name"], (double?)first["Total"], (bool?)first["Paid"], (string?)first["Tags"], (string?)first["Owner"], (long?)first["Count"]));
            Assert.Null(first["Note"]);
            Assert.Equal(150, (await Run(coda, CodaConnector.RowsOperation, new() { ["doc"] = Doc, ["table"] = "grid-pqRst-U" })).Count);
            Assert.Equal($"{path}?useColumnNames=true&limit=100&pageToken=eyJ%2B%2F%3D", coda.Requests[^1].Url); // a thousand at most by default, visible rows only when asked

            Assert.Equal(30, (await Run(coda, CodaConnector.RowsOperation, new() { ["doc"] = Doc, ["table"] = "Orders/list", ["column_names"] = "false", ["visible_only"] = "false", ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables/Orders%2Flist/rows?useColumnNames=false&visibleOnly=false&limit=30", coda.Requests[^1].Url); // a name escaped, a page no bigger than what is left

            coda.Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"i-7\",\"values\":{\"c-Name\":\"Ada\"}}");
            var got = Assert.Single(await Run(coda, CodaConnector.GetRowOperation, new() { ["doc"] = Doc, ["table"] = "grid-pqRst-U", ["row"] = "Ada / 7", ["column_names"] = "false", ["value_format"] = "rich", ["query"] = "x:1", ["max"] = "5" })).Fields;
            Assert.Equal($"{path}/Ada%20%2F%207?useColumnNames=false&valueFormat=rich", coda.Requests[^1].Url); // no query and no limit on a get
            Assert.Equal(("i-7", "Ada"), ((string?)got["id"], (string?)got["c-Name"]));
            await Run(coda, CodaConnector.GetRowOperation, new() { ["doc"] = Doc, ["table"] = "grid-pqRst-U", ["row"] = "i-7" }, endpoint: "https://coda.example.test/apis/v1/");
            Assert.Equal($"{path}/i-7?useColumnNames=true", coda.Requests[^1].Url); // the API's own address as well, names by default
        }

        [Fact]
        public async Task Rows_are_created_upserted_updated_deleted_and_a_button_pushed_waiting_when_asked()
        {
            var coda = new Coda { Answer = (_, _) => (HttpStatusCode.Accepted, "{\"requestId\":\"mutate:1\",\"addedRowIds\":[\"i-43\",\"i-44\"]}") };
            (string, string, string?) Last() => (coda.Requests[^1].Method.Method, coda.Requests[^1].Url, coda.Requests[^1].Body);
            var path = $"{Api}/apis/v1/docs/{Doc}/tables/grid-pqRst-U/rows";
            Dictionary<string, string> At(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["doc"] = Doc, ["table"] = "grid-pqRst-U" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            var made = await Run(coda, CodaConnector.CreateRowsOperation, At(("fields", "[{\"Name\": \"Café Ada\", \"Total\": 99.5}, {\"Name\": \"Bo\", \"Tags\": [\"a\"]}]")));
            Assert.Equal(("POST", path, (string?)"{\"rows\":[{\"cells\":[{\"column\":\"Name\",\"value\":\"Café Ada\"},{\"column\":\"Total\",\"value\":99.5}]},{\"cells\":[{\"column\":\"Name\",\"value\":\"Bo\"},{\"column\":\"Tags\",\"value\":[\"a\"]}]}]}"), Last());
            Assert.Equal(new (string?, string?, string?)[] { ("i-43", "Café Ada", "mutate:1"), ("i-44", "Bo", "mutate:1") }, made.Select(record => ((string?)record.Fields["id"], (string?)record.Fields["Name"], (string?)record.Fields["request_id"])));
            Assert.Equal("[\"a\"]", made[1].Fields["Tags"]);

            coda.Answer = (_, _) => (HttpStatusCode.Accepted, "{\"requestId\":\"mutate:1\",\"addedRowIds\":[\"i-45\"]}");
            var upserted = Assert.Single(await Run(coda, CodaConnector.CreateRowsOperation, At(("fields", "{\"Email\": \"ada@example.com\", \"Name\": \"Ada\"}"), ("key_columns", "Email, c-Code"), ("parse", "false"))));
            Assert.Equal(("POST", path + "?disableParsing=true", (string?)"{\"rows\":[{\"cells\":[{\"column\":\"Email\",\"value\":\"ada@example.com\"},{\"column\":\"Name\",\"value\":\"Ada\"}]}],\"keyColumns\":[\"Email\",\"c-Code\"]}"), Last());
            Assert.False(upserted.Fields.ContainsKey("id")); // with key columns a row may have been updated, not added
            coda.Answer = (_, _) => (HttpStatusCode.Accepted, "{\"requestId\":\"mutate:2\",\"addedRowIds\":[\"i-45\"]}");
            Assert.All(await Run(coda, CodaConnector.CreateRowsOperation, At(("fields", "[{\"Name\": \"A\"}, {\"Name\": \"B\"}]"))), record => Assert.False(record.Fields.ContainsKey("id"))); // ids only when Coda named each

            coda.Answer = (_, _) => (HttpStatusCode.Accepted, "{\"requestId\":\"mutate:3\",\"id\":\"i-7\"}");
            var changed = Assert.Single(await Run(coda, CodaConnector.UpdateRowOperation, At(("row", "Ada"), ("fields", "{\"Paid\": true}"), ("parse", "false"))));
            Assert.Equal(("PUT", path + "/Ada?disableParsing=true", (string?)"{\"row\":{\"cells\":[{\"column\":\"Paid\",\"value\":true}]}}"), Last());
            Assert.Equal(("i-7", true, "mutate:3"), ((string?)changed.Fields["id"], (bool?)changed.Fields["Paid"], (string?)changed.Fields["request_id"]));

            var asks = 0;
            coda.Answer = (_, url) => (HttpStatusCode.Accepted, url.Contains("/mutationStatus/", StringComparison.Ordinal) ? (++asks < 3 ? "{\"completed\":false}" : "{\"completed\":true}") : "{\"requestId\":\"mutate:4\",\"rowIds\":[\"i-4\",\"i-7\"]}");
            var gone = await Run(coda, CodaConnector.DeleteRowsOperation, At(("rows", "i-4, i-7, i-4"), ("wait", "true")));
            Assert.Equal(new[] { $"DELETE {path} {{\"rowIds\":[\"i-4\",\"i-7\"]}}", $"GET {Api}/apis/v1/mutationStatus/mutate%3A4 ", $"GET {Api}/apis/v1/mutationStatus/mutate%3A4 ", $"GET {Api}/apis/v1/mutationStatus/mutate%3A4 " }, coda.Requests.TakeLast(4).Select(request => $"{request.Method} {request.Url} {request.Body}")); // asked until applied
            Assert.Equal(new (string?, string?, bool?, string?)[] { ("grid-pqRst-U", "i-4", true, "mutate:4"), ("grid-pqRst-U", "i-7", true, "mutate:4") }, gone.Select(record => ((string?)record.Fields["table"], (string?)record.Fields["id"], (bool?)record.Fields["deleted"], (string?)record.Fields["request_id"])));

            coda.Answer = (_, url) => (HttpStatusCode.Accepted, url.Contains("/mutationStatus/", StringComparison.Ordinal) ? "{\"completed\":false}" : "{\"requestId\":\"mutate:5\",\"rowId\":\"i-7\",\"columnId\":\"c-Ship\"}");
            Assert.Equal("Coda had not applied the change after 60 tries (request mutate:5).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(coda, CodaConnector.PushButtonOperation, At(("row", "i-7"), ("column", "Shipped?"), ("wait", "true"))))).Message);
            Assert.Equal(61, coda.Requests.Count(request => request.Url.Contains("mutate%3A5", StringComparison.Ordinal) || request.Url.EndsWith("/buttons/Shipped%3F", StringComparison.Ordinal)));
            var pushed = Assert.Single(await Run(coda, CodaConnector.PushButtonOperation, At(("row", "Ada"), ("column", "Shipped?"))));
            Assert.Equal(("POST", path + "/Ada/buttons/Shipped%3F", (string?)null), Last()); // no wait, no ask
            Assert.Equal(("i-7", "c-Ship", "mutate:5"), ((string?)pushed.Fields["row"], (string?)pushed.Fields["column"], (string?)pushed.Fields["request_id"])); // the row and column as Coda named them
        }

        [Fact]
        public async Task The_docs_tables_columns_formulas_and_controls_are_read()
        {
            var coda = new Coda
            {
                Answer = (_, url) => (HttpStatusCode.OK,
                    url.Contains("/columns/", StringComparison.Ordinal) ? "{\"id\":\"c-Total\",\"name\":\"Total\",\"format\":{\"type\":\"currency\"},\"calculated\":true,\"formula\":\"thisRow.Price * 2\"}"
                    : url.Contains("/columns", StringComparison.Ordinal) ? "{\"items\":[{\"id\":\"c-Name\",\"name\":\"Name\",\"display\":true,\"format\":{\"type\":\"text\"}},{\"id\":\"c-Total\",\"name\":\"Total\",\"display\":false,\"format\":{\"type\":\"currency\"},\"calculated\":true,\"formula\":\"thisRow.Price * 2\"}]}"
                    : url.Contains("/formulas/", StringComparison.Ordinal) ? "{\"id\":\"f-1\",\"name\":\"Sum\",\"value\":1234.5}"
                    : url.Contains("/formulas", StringComparison.Ordinal) ? "{\"items\":[{\"id\":\"f-1\",\"name\":\"Sum\"}]}"
                    : url.Contains("/controls/", StringComparison.Ordinal) ? "{\"id\":\"ctrl-1\",\"name\":\"Region\",\"controlType\":\"select\",\"value\":[\"North\"]}"
                    : url.Contains("/controls", StringComparison.Ordinal) ? "{\"items\":[{\"id\":\"ctrl-1\",\"name\":\"Region\",\"controlType\":\"select\"}]}"
                    : url.Contains("/tables", StringComparison.Ordinal) ? "{\"items\":[{\"id\":\"grid-1\",\"name\":\"Orders\",\"tableType\":\"table\"},{\"id\":\"table-2\",\"name\":\"Paid orders\",\"tableType\":\"view\"}]}"
                    : "{\"items\":[{\"id\":\"AbCDeFGH\",\"name\":\"Shop\"}]}"),
            };
            var answer = coda.Answer;
            Task<List<ConnectorRecord>> Read(string operation, Dictionary<string, string> parameters) => Run(coda, operation, parameters);
            (string?, string?) Name(ConnectorRecord record) => ((string?)record.Fields["id"], (string?)record.Fields["name"]);

            Assert.Equal(("AbCDeFGH", "Shop"), Name(Assert.Single(await Read(CodaConnector.DocsOperation, new() { ["max"] = "5" }))));
            Assert.Equal($"{Api}/apis/v1/docs?limit=5", coda.Requests[^1].Url);
            var tables = await Read(CodaConnector.TablesOperation, new() { ["doc"] = Doc, ["types"] = "View, table, view", ["table"] = "x\ny" });
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables?tableTypes=view,table&limit=100", coda.Requests[^1].Url); // no table asked
            Assert.Equal(new (string?, string?)[] { ("grid-1", "table"), ("table-2", "view") }, tables.Select(record => ((string?)record.Fields["id"], (string?)record.Fields["type"])));
            await Read(CodaConnector.TablesOperation, new() { ["doc"] = Doc });
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables?limit=100", coda.Requests[^1].Url);
            coda.Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"table-2\",\"name\":\"Paid orders\",\"tableType\":\"view\",\"rowCount\":42}");
            var view = Assert.Single(await Read(CodaConnector.GetTableOperation, new() { ["doc"] = Doc, ["table"] = "Paid orders" })).Fields;
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables/Paid%20orders", coda.Requests[^1].Url);
            Assert.Equal(("table-2", "Paid orders", "view", 42L), ((string?)view["id"], (string?)view["name"], (string?)view["type"], (long?)view["rows"]));
            coda.Answer = answer;

            var columns = await Read(CodaConnector.ColumnsOperation, new() { ["doc"] = Doc, ["table"] = "Orders" });
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables/Orders/columns?limit=100", coda.Requests[^1].Url);
            Assert.Equal(("text", true, false, (string?)null), ((string?)columns[0].Fields["type"], (bool?)columns[0].Fields["display"], (bool?)columns[0].Fields["calculated"], (string?)columns[0].Fields["formula"]));
            Assert.Equal(false, (bool?)columns[1].Fields["display"]);
            var column = Assert.Single(await Read(CodaConnector.GetColumnOperation, new() { ["doc"] = Doc, ["table"] = "Orders", ["column"] = "Total?" })).Fields;
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/tables/Orders/columns/Total%3F", coda.Requests[^1].Url);
            Assert.Equal(("currency", false, true, "thisRow.Price * 2"), ((string?)column["type"], (bool?)column["display"], (bool?)column["calculated"], (string?)column["formula"]));

            Assert.Equal(("f-1", "Sum"), Name(Assert.Single(await Read(CodaConnector.FormulasOperation, new() { ["doc"] = Doc }))));
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/formulas?limit=100", coda.Requests[^1].Url);
            Assert.Equal(1234.5, (double?)Assert.Single(await Read(CodaConnector.GetFormulaOperation, new() { ["doc"] = Doc, ["formula"] = "Sum?" })).Fields["value"]);
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/formulas/Sum%3F", coda.Requests[^1].Url);
            Assert.Equal("select", (string?)Assert.Single(await Read(CodaConnector.ControlsOperation, new() { ["doc"] = Doc })).Fields["type"]);
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/controls?limit=100", coda.Requests[^1].Url);
            var control = Assert.Single(await Read(CodaConnector.GetControlOperation, new() { ["doc"] = Doc, ["control"] = "Region" })).Fields;
            Assert.Equal($"{Api}/apis/v1/docs/{Doc}/controls/Region", coda.Requests[^1].Url);
            Assert.Equal(("ctrl-1", "Region", "select", "[\"North\"]"), ((string?)control["id"], (string?)control["name"], (string?)control["type"], (string?)control["value"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_codas_word_on_a_failure()
        {
            var coda = new Coda();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(coda, operation, parameters))).Message;
            Dictionary<string, string> At(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["doc"] = Doc, ["table"] = "Orders" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.Equal("Give the doc - its id.", await Refused(CodaConnector.TablesOperation, new()));
            Assert.Equal("'../docs' is not a doc's id - letters, digits, _ and -.", await Refused(CodaConnector.TablesOperation, new() { ["doc"] = "../docs" }));
            Assert.Equal("Give the table - its id or its name.", await Refused(CodaConnector.RowsOperation, new() { ["doc"] = Doc }));
            Assert.Equal("'..' is not a table's id or name - one line, not dots alone.", await Refused(CodaConnector.ColumnsOperation, new() { ["doc"] = Doc, ["table"] = ".." }));
            Assert.Equal("'a\tb' is not a table's id or name - one line, not dots alone.", await Refused(CodaConnector.ColumnsOperation, new() { ["doc"] = Doc, ["table"] = "a\tb" }));
            Assert.Equal("Give the row - its id or its name.", await Refused(CodaConnector.GetRowOperation, At()));
            Assert.Equal("Give the column - its id or its name.", await Refused(CodaConnector.PushButtonOperation, At(("row", "i-7"))));
            Assert.Equal("Give the formula - its id or its name.", await Refused(CodaConnector.GetFormulaOperation, new() { ["doc"] = Doc }));
            Assert.Equal("Give the control - its id or its name.", await Refused(CodaConnector.GetControlOperation, new() { ["doc"] = Doc }));
            Assert.Equal("Give the rows - comma-separated.", await Refused(CodaConnector.DeleteRowsOperation, At()));
            Assert.Equal("'' is not an id or a name (rows).", await Refused(CodaConnector.DeleteRowsOperation, At(("rows", "i-4,,i-7"))));
            Assert.Equal("Give up to 500 rows at once.", await Refused(CodaConnector.DeleteRowsOperation, At(("rows", string.Join(",", Enumerable.Range(1, 501).Select(i => "i-" + i))))));
            Assert.Equal("'yes' is not true or false (wait).", await Refused(CodaConnector.DeleteRowsOperation, At(("rows", "i-4"), ("wait", "yes"))));
            Assert.Equal("'1' is not true or false (column names).", await Refused(CodaConnector.RowsOperation, At(("column_names", "1"))));
            Assert.Equal("'html' is not simple, simpleWithArrays or rich (value format).", await Refused(CodaConnector.GetRowOperation, At(("row", "i-7"), ("value_format", "html"))));
            Assert.Equal("The query is a column and a value on one line - Status:\"Paid\".", await Refused(CodaConnector.RowsOperation, At(("query", "Paid"))));
            Assert.Equal("The query is a column and a value on one line - Status:\"Paid\".", await Refused(CodaConnector.RowsOperation, At(("query", "Status:\"Paid\"\u0001"))));
            Assert.Equal("'newest' is not createdAt, natural or updatedAt (sort by).", await Refused(CodaConnector.RowsOperation, At(("sort_by", "newest"))));
            Assert.Equal("'maybe' is not true or false (visible only).", await Refused(CodaConnector.RowsOperation, At(("visible_only", "maybe"))));
            Assert.Equal("The types are table, view or both - table,view.", await Refused(CodaConnector.TablesOperation, new() { ["doc"] = Doc, ["types"] = "table,page" }));
            Assert.Equal("Give the fields - a JSON object.", await Refused(CodaConnector.CreateRowsOperation, At()));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"Name\": \"Ada\"}.", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "Name=Ada"))));
            Assert.Equal("The fields are not a JSON object with a field or more - {\"Name\": \"Ada\"}.", await Refused(CodaConnector.UpdateRowOperation, At(("row", "i-7"), ("fields", "{}"))));
            Assert.Equal("Give one to 500 rows - JSON objects with a field or more.", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "[{\"a\": 1}, {}]"))));
            Assert.Equal("Give one to 500 rows - JSON objects with a field or more.", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "[]"))));
            Assert.Equal("Give one to 500 rows - JSON objects with a field or more.", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "[" + string.Join(",", Enumerable.Range(1, 501).Select(_ => "{\"a\": 1}")) + "]"))));
            Assert.Equal("Give one row's fields - a JSON object.", await Refused(CodaConnector.UpdateRowOperation, At(("row", "i-7"), ("fields", "[{\"a\": 1}]"))));
            Assert.Equal("'no' is not true or false (parse).", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "{\"a\": 1}"), ("parse", "no"))));
            Assert.Equal("'' is not an id or a name (key_columns).", await Refused(CodaConnector.CreateRowsOperation, At(("fields", "{\"a\": 1}"), ("key_columns", "Email,"))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(CodaConnector.RowsOperation, At(("max", "0"))));
            Assert.StartsWith("Coda has no operation 'pages' - rows, get_row,", await Refused("pages", new()));
            Assert.Empty(coda.Requests); // nothing sent

            var tokenless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new CodaConnector(), Contexts.Extract(new StubEgress(coda), Api, CodaConnector.DocsOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>())));
            Assert.Equal("The Coda connection has no API token.", tokenless.Message);

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Coda { Answer = (_, _) => (status, answer) }, CodaConnector.RowsOperation, At()))).Message;
            Assert.Equal("HTTP 404: Could not find a table with that id or name", await Failed(HttpStatusCode.NotFound, "{\"statusCode\":404,\"statusMessage\":\"Not Found\",\"message\":\"Could not find a table with that id or name\"}"));
            Assert.Equal("HTTP 401: Unauthorized", await Failed(HttpStatusCode.Unauthorized, "{\"statusCode\":401,\"statusMessage\":\"Unauthorized\"}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Coda answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as no rows
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_token()
        {
            var coda = new Coda { Answer = (_, _) => (HttpStatusCode.OK, "{\"name\":\"Ada\",\"loginId\":\"ada@example.com\",\"type\":\"user\",\"scoped\":false,\"tokenName\":\"Pipelines\"}") };
            var tested = await new CodaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(coda), "", credentials: Credentials));
            Assert.Equal((true, "Coda: the token 'Pipelines' answers."), (tested.Success, tested.Message));
            Assert.Equal(("https://coda.io/apis/v1/whoami", "Bearer coda-token-1"), (coda.Requests[0].Url, coda.Requests[0].Authorization)); // Coda's own address when none is given
            var nameless = new Coda { Answer = (_, _) => (HttpStatusCode.OK, "{\"name\":\"Ada\"}") };
            Assert.Equal("Coda: the token answers.", (await new CodaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(nameless), "", credentials: Credentials))).Message);
            var refused = new Coda { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"statusCode\":401,\"statusMessage\":\"Unauthorized\",\"message\":\"The API token is invalid.\"}") };
            var denied = await new CodaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), "", credentials: Credentials));
            Assert.Equal((false, "HTTP 401: The API token is invalid."), (denied.Success, denied.Message));
            Assert.Equal((AuthenticationType.Bearer, "coda"), (new CodaConnector().AuthType, new CodaConnector().ConnectorType));
            Assert.Equal(15, new CodaConnector().Operations.Count);
        }
    }
}
