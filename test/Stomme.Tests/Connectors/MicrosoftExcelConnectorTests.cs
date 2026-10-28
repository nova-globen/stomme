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
using Stomme.Modules.Connectors.MicrosoftExcel;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft Excel 365 (n8n's Microsoft Excel 365 node: its workbooks, sheets and tables): an app-only token from the
    /// tenant's token endpoint, sent as a bearer, on a user's OneDrive or a library's drive; a workbook by its id or its path, a
    /// sheet by its name, escaped; a sheet's rows read under its headers with their row numbers; records appended under the headers
    /// after the last used row (an empty sheet given headers first); rows found by a column and changed - only the record's fields,
    /// a formula left alone kept - or appended; a sheet cleared; a table's rows read page by page, appended, looked up; sheets and
    /// tables added, deleted and converted; what could go wrong refused before anything is sent; Graph's refusal its code and
    /// message; a test naming the drive.
    /// </summary>
    public sealed class MicrosoftExcelConnectorTests
    {
        private const string Drive = "https://graph.example.test/v1.0/users/ada%40contoso.com/drive";

        private const string Book = Drive + "/root:/Reports/Sales%202026.xlsx:/workbook";

        private const string Sheet = Book + "/worksheets/My%20sheet";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = "https://login.example.test/tenant/oauth2/v2.0/token" };

        /// <summary>Microsoft as a test sees it: the token endpoint, then Graph, each request recorded with its body.</summary>
        private sealed class Microsoft : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Graph { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://login.example.test/", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : Graph(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> GraphRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://login.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Microsoft microsoft, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.Extract(new StubEgress(microsoft), "https://graph.example.test/v1.0", operation, parameters,
                settings: settings ?? new Dictionary<string, string> { ["user"] = "ada@contoso.com" }, credentials: Credentials);

        private static MicrosoftExcelConnector Connector() => new(new OAuth2TokenProvider());

        private static Dictionary<string, string> On(params (string Key, string Value)[] more)
        {
            var parameters = new Dictionary<string, string> { ["workbook"] = "/Reports/Sales 2026.xlsx", ["sheet"] = "My sheet" };
            foreach (var (key, value) in more)
            {
                parameters[key] = value;
            }

            return parameters;
        }

        [Fact]
        public async Task A_sheets_rows_are_read_under_its_headers_with_their_row_numbers_by_an_app_only_token()
        {
            var microsoft = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"address":"'My sheet'!B2:E5","values":[["Name","Qty","","Name"],["Café <b>","12","x",true],["Lin",3.5,"",false],["","","",""]]}""") };
            var records = await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftExcelConnector.ReadRowsOperation, On()));

            var token = microsoft.Requests[0];
            Assert.Equal((HttpMethod.Post, "https://login.example.test/tenant/oauth2/v2.0/token"), (token.Method, token.Url));
            Assert.Contains("grant_type=client_credentials", token.Body);
            Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", token.Body);
            var call = Assert.Single(microsoft.GraphRequests);
            Assert.Equal((Sheet + "/usedRange(valuesOnly=true)?$select=address,values", "Bearer access-1"), (call.Url, call.Authorization));
            Assert.Equal(3, records.Count);
            Assert.Equal(new[] { "row_number", "Name", "Qty", "D", "Name (2)" }, records[0].Fields.Keys); // a blank header its column's letter, a repeated one numbered
            Assert.Equal((3L, "Café <b>", "12", "x", (object?)true), ((long?)records[0].Fields["row_number"], (string?)records[0].Fields["Name"], (string?)records[0].Fields["Qty"], (string?)records[0].Fields["D"], records[0].Fields["Name (2)"]));
            Assert.Equal((4L, (object?)3.5), ((long?)records[1].Fields["row_number"], records[1].Fields["Qty"]));
            Assert.Equal(5L, (long?)records[2].Fields["row_number"]);

            var plain = new Microsoft { Graph = microsoft.Graph };
            var lettered = await Contexts.DrainAsync(Connector(), Context(plain, MicrosoftExcelConnector.ReadRowsOperation, On(("range", "b2:e5"), ("header_row", "FALSE"))));
            Assert.Equal(Sheet + "/range(address='B2:E5')?$select=address,values", Assert.Single(plain.GraphRequests).Url);
            Assert.Equal(new[] { "row_number", "B", "C", "D", "E" }, lettered[0].Fields.Keys);
            Assert.Equal((2L, "Name"), ((long?)lettered[0].Fields["row_number"], (string?)lettered[0].Fields["B"]));

            var empty = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"address":"Sheet1!A1","values":[[""]]}""") };
            Assert.Empty(await Contexts.DrainAsync(Connector(), Context(empty, MicrosoftExcelConnector.ReadRowsOperation, new() { ["workbook"] = "01ABCDEF!12" })));
            Assert.Equal(Drive + "/items/01ABCDEF%2112/workbook/worksheets/Sheet1/usedRange(valuesOnly=true)?$select=address,values", Assert.Single(empty.GraphRequests).Url); // Sheet1 by default; an id escaped
            var emptyLettered = new Microsoft { Graph = empty.Graph };
            Assert.Empty(await Contexts.DrainAsync(Connector(), Context(emptyLettered, MicrosoftExcelConnector.ReadRowsOperation, new() { ["workbook"] = "01ABC", ["header_row"] = "false" }))); // an empty sheet: no rows, not one blank one
            var odd = new Microsoft { Graph = empty.Graph };
            await Contexts.DrainAsync(Connector(), Context(odd, MicrosoftExcelConnector.ReadRowsOperation, new() { ["workbook"] = "/R&D/Plan+1.xlsx", ["sheet"] = "Q#1 & Q2" }));
            Assert.Equal(Drive + "/root:/R%26D/Plan%2B1.xlsx:/workbook/worksheets/Q%231%20%26%20Q2/usedRange(valuesOnly=true)?$select=address,values", Assert.Single(odd.GraphRequests).Url); // every part escaped
            var byId = new Microsoft { Graph = empty.Graph };
            await Contexts.DrainAsync(Connector(), Context(byId, MicrosoftExcelConnector.ReadRowsOperation, new() { ["workbook"] = "01ABC", ["sheet"] = "{00000000-0001-0000-0000-000000000000}" }));
            Assert.Equal(Drive + "/items/01ABC/workbook/worksheets/%7B00000000-0001-0000-0000-000000000000%7D/usedRange(valuesOnly=true)?$select=address,values", Assert.Single(byId.GraphRequests).Url); // a sheet by its id

            var library = new Microsoft { Graph = empty.Graph };
            await Contexts.DrainAsync(Connector(), Context(library, MicrosoftExcelConnector.ReadRowsOperation, new() { ["workbook"] = "01ABC" }, new Dictionary<string, string> { ["drive"] = "b!xYz-1_2", ["user"] = "ada@contoso.com" }));
            Assert.StartsWith("https://graph.example.test/v1.0/drives/b%21xYz-1_2/items/01ABC/workbook/", Assert.Single(library.GraphRequests).Url); // a drive named wins
        }

        [Fact]
        public async Task Records_are_appended_under_the_headers_after_the_last_used_row_and_an_empty_sheet_gets_headers_first()
        {
            var microsoft = new Microsoft { Graph = (method, _) => (HttpStatusCode.OK, method == HttpMethod.Get ? """{"address":"'My sheet'!B1:D3","values":[["Name","Qty","Note"],["Ada",12,""],["Lin",3,""]]}""" : """{"address":"'My sheet'!B4:D5"}""") };
            var written = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftExcelConnector.AppendRowsOperation, On(("values", """[{"Name":"Grace","Qty":7,"Extra":"x"},{"Name":"Café <b>","Note":{"a":1},"Other":null}]""")))));
            var calls = microsoft.GraphRequests;
            Assert.Equal(Sheet + "/usedRange(valuesOnly=true)?$select=address,values", calls[0].Url);
            Assert.Equal(("PATCH", Sheet + "/range(address='B4:D5')", """{"values":[["Grace",7,""],["Café <b>","","{\"a\":1}"]]}"""), (calls[1].Method.Method, calls[1].Url, calls[1].Body));
            Assert.Equal(("'My sheet'!B4:D5", 2L, "Extra, Other"), ((string?)written.Fields["updated_range"], (long?)written.Fields["updated_rows"], (string?)written.Fields["left_out"]));

            var lists = new Microsoft { Graph = microsoft.Graph };
            await Contexts.DrainAsync(Connector(), Context(lists, MicrosoftExcelConnector.AppendRowsOperation, On(("values", """[["a",1],["b"]]"""))));
            Assert.Equal((Sheet + "/range(address='B4:C5')", """{"values":[["a",1],["b",""]]}"""), (lists.GraphRequests[1].Url, lists.GraphRequests[1].Body)); // written as they are

            var empty = new Microsoft { Graph = (method, _) => (HttpStatusCode.OK, method == HttpMethod.Get ? """{"address":"'My sheet'!A1","values":[[""]]}""" : "{}") };
            var first = Assert.Single(await Contexts.DrainAsync(Connector(), Context(empty, MicrosoftExcelConnector.AppendRowsOperation, On(("values", """{"Name":"Grace","Qty":7}""")))));
            Assert.Equal((Sheet + "/range(address='A1:B2')", """{"values":[["Name","Qty"],["Grace",7]]}"""), (empty.GraphRequests[1].Url, empty.GraphRequests[1].Body));
            Assert.Equal(("A1:B2", 2L, ""), ((string?)first.Fields["updated_range"], (long?)first.Fields["updated_rows"], (string?)first.Fields["left_out"]));
        }

        private const string Ledger = """{"address":"'My sheet'!A1:C4","values":[["Id","Name","Total"],[1,"Ada",10],[2,"Lin",20],["2.0","Lin again",30]],"formulas":[["Id","Name","Total"],[1,"Ada","=5*2"],[2,"Lin",20],["2.0","Lin again","=10*3"]]}""";

        [Fact]
        public async Task Rows_are_found_by_a_column_and_only_the_records_fields_change_or_are_appended()
        {
            var microsoft = new Microsoft { Graph = (method, _) => (HttpStatusCode.OK, method == HttpMethod.Get ? Ledger : "{}") };
            var results = await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Id"), ("values", """[{"Id":"2","Name":"Lin B","Extra":1},{"Id":9,"Name":"Nobody"}]"""))));
            var calls = microsoft.GraphRequests;
            Assert.Equal(Sheet + "/usedRange(valuesOnly=true)?$select=address,values,formulas", calls[0].Url);
            Assert.Equal(2, calls.Count); // the first match only, nothing appended
            Assert.Equal(("PATCH", Sheet + "/range(address='A3:C3')", """{"formulas":[[2,"Lin B",20]]}"""), (calls[1].Method.Method, calls[1].Url, calls[1].Body));
            Assert.Equal(2, results.Count);
            Assert.Equal((2L, "Lin B", 20L, 3L, "updated", "Extra"), ((long?)results[0].Fields["Id"], (string?)results[0].Fields["Name"], (long?)results[0].Fields["Total"], (long?)results[0].Fields["row_number"], (string?)results[0].Fields["action"], (string?)results[0].Fields["left_out"]));
            Assert.Equal((9L, "Nobody", (long?)null, "not found"), ((long?)results[1].Fields["Id"], (string?)results[1].Fields["Name"], (long?)results[1].Fields["row_number"], (string?)results[1].Fields["action"]));

            var every = new Microsoft { Graph = microsoft.Graph };
            await Contexts.DrainAsync(Connector(), Context(every, MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Id"), ("all_matches", "true"), ("values", """{"Id":2,"Name":"Lin B"}"""))));
            Assert.Equal(new[] { (Sheet + "/range(address='A3:C3')", """{"formulas":[[2,"Lin B",20]]}"""), (Sheet + "/range(address='A4:C4')", """{"formulas":[["2.0","Lin B","=10*3"]]}""") },
                every.GraphRequests.Skip(1).Select(request => (request.Url, request.Body!))); // the same number matches; a formula it leaves alone kept

            var upsert = new Microsoft { Graph = microsoft.Graph };
            var upserted = await Contexts.DrainAsync(Connector(), Context(upsert, MicrosoftExcelConnector.UpsertRowsOperation, On(("match_column", "Id"), ("values", """[{"Id":1,"Total":11},{"Id":9,"Name":"Nobody"},{"Id":10}]"""))));
            Assert.Equal(new[] { (Sheet + "/range(address='A2:C2')", """{"formulas":[[1,"Ada",11]]}"""), (Sheet + "/range(address='A5:C6')", """{"values":[[9,"Nobody",""],[10,"",""]]}""") },
                upsert.GraphRequests.Skip(1).Select(request => (request.Url, request.Body!)));
            Assert.Equal(new[] { ("updated", 2L), ("appended", 5L), ("appended", 6L) }, upserted.Select(record => ((string)record.Fields["action"]!, (long)record.Fields["row_number"]!)));

            var blanks = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"address":"'My sheet'!A1:B3","values":[["Id","Name"],["","Blank"],[5,"Five"]],"formulas":[["Id","Name"],["","Blank"],[5,"Five"]]}""") };
            var unmatched = Assert.Single(await Contexts.DrainAsync(Connector(), Context(blanks, MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Id"), ("values", """{"Id":"","Name":"x"}""")))));
            Assert.Equal("not found", (string?)unmatched.Fields["action"]); // an empty cell matches nothing
            Assert.Single(blanks.GraphRequests); // nothing written
        }

        [Fact]
        public async Task A_tables_rows_are_read_page_by_page_appended_and_looked_up()
        {
            const string table = Book + "/tables/Orders";
            string Rows(int from, int count) => "{\"value\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"index\":" + n + ",\"values\":[[\"Customer " + n + "\"," + n + "]]}")) + "]}";
            (HttpStatusCode, string) Answer(HttpMethod method, string url) =>
                url.Contains("/columns", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"value":[{"id":"1","name":"Name","index":0},{"id":"2","name":"Qty","index":1}]}""")
                : url.Contains("$skip=100", StringComparison.Ordinal) ? (HttpStatusCode.OK, Rows(100, 20))
                : url.Contains("/rows?", StringComparison.Ordinal) ? (HttpStatusCode.OK, Rows(0, 100))
                : (HttpStatusCode.OK, """{"index":120}""");
            var microsoft = new Microsoft { Graph = Answer };
            var records = await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftExcelConnector.TableRowsOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["max"] = "150" }));
            Assert.Equal(new[] { table + "/columns?$select=id,name,index", table + "/rows?$top=100&$skip=0", table + "/rows?$top=50&$skip=100" }, microsoft.GraphRequests.Select(request => request.Url));
            Assert.Equal(120, records.Count); // the last page short: no more asked for
            Assert.Equal(new[] { "row_index", "Name", "Qty" }, records[0].Fields.Keys);
            Assert.Equal((7L, "Customer 7", 7L), ((long?)records[7].Fields["row_index"], (string?)records[7].Fields["Name"], (long?)records[7].Fields["Qty"]));

            var looking = new Microsoft { Graph = Answer };
            var found = await Contexts.DrainAsync(Connector(), Context(looking, MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["column"] = "Qty", ["value"] = "105" }));
            Assert.Equal(("Customer 105", 105L), ((string?)Assert.Single(found).Fields["Name"], (long?)found[0].Fields["row_index"])); // its exact text; the first
            var none = new Microsoft { Graph = Answer };
            Assert.Empty(await Contexts.DrainAsync(Connector(), Context(none, MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["column"] = "Name", ["value"] = "customer 7", ["all_matches"] = "true" }))); // case counts
            Assert.Equal(new[] { table + "/columns?$select=id,name,index", table + "/rows?$top=100&$skip=0", table + "/rows?$top=100&$skip=100" }, none.GraphRequests.Select(request => request.Url)); // every page read
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(new Microsoft { Graph = Answer }, MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["column"] = "Email", ["value"] = "x" })));
            Assert.Equal("The table has no column 'Email' - its columns: Name, Qty.", missing.Message);

            (HttpStatusCode, string) Twice(HttpMethod method, string url) => url.Contains("/columns", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"value":[{"id":"1","name":"Name","index":0},{"id":"2","name":"Qty","index":1}]}""")
                : (HttpStatusCode.OK, """{"value":[{"index":0,"values":[["A",1]]},{"index":1,"values":[["B",1]]},{"index":2,"values":[["C",2]]}]}""");
            var first = await Contexts.DrainAsync(Connector(), Context(new Microsoft { Graph = Twice }, MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "01ABC", ["table"] = "Orders", ["column"] = "Qty", ["value"] = "1" }));
            Assert.Equal(new[] { "A" }, first.Select(row => (string)row.Fields["Name"]!)); // the first match
            var both = await Contexts.DrainAsync(Connector(), Context(new Microsoft { Graph = Twice }, MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "01ABC", ["table"] = "Orders", ["column"] = "Qty", ["value"] = "1", ["all_matches"] = "true" }));
            Assert.Equal(new[] { "A", "B" }, both.Select(row => (string)row.Fields["Name"]!)); // every match

            var adding = new Microsoft { Graph = Answer };
            var added = Assert.Single(await Contexts.DrainAsync(Connector(), Context(adding, MicrosoftExcelConnector.AppendTableRowsOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["values"] = """[{"Qty":7,"Name":"Grace","Extra":1}]""", ["index"] = "0" })));
            Assert.Equal(("POST", table + "/rows/add", """{"values":[["Grace",7]],"index":0}"""), (adding.GraphRequests[1].Method.Method, adding.GraphRequests[1].Url, adding.GraphRequests[1].Body)); // under the columns by name; the top asked for
            Assert.Equal((120L, 1L, "Extra"), ((long?)added.Fields["index"], (long?)added.Fields["added"], (string?)added.Fields["left_out"]));
            var atEnd = new Microsoft { Graph = Answer };
            await Contexts.DrainAsync(Connector(), Context(atEnd, MicrosoftExcelConnector.AppendTableRowsOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders", ["values"] = """[["Ada",1],["Lin",2]]""" }));
            Assert.Equal("""{"values":[["Ada",1],["Lin",2]]}""", atEnd.GraphRequests[1].Body); // no position: the end

            var columns = await Contexts.DrainAsync(Connector(), Context(new Microsoft { Graph = Answer }, MicrosoftExcelConnector.TableColumnsOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders" }));
            Assert.Equal(new[] { ("1", "Name", 0L), ("2", "Qty", 1L) }, columns.Select(column => ((string)column.Fields["id"]!, (string)column.Fields["name"]!, (long)column.Fields["index"]!)));
        }

        [Fact]
        public async Task Workbooks_sheets_and_tables_are_found_added_cleared_converted_and_deleted()
        {
            var microsoft = new Microsoft
            {
                Graph = (method, url) => url.Contains("skiptoken", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"value":[{"id":"01C","name":"Budget.xlsm","parentReference":{"path":"/drive/root:"}}],"@odata.nextLink":"https://evil.example/steal"}""")
                    : (HttpStatusCode.OK, """{"value":[{"id":"01A","name":"Sales 2026.xlsx","webUrl":"https://contoso.sharepoint.com/x","size":2048,"lastModifiedDateTime":"2026-10-01T09:00:00Z","parentReference":{"path":"/drive/root:/Reports"}},{"id":"01B","name":"notes.txt"}],"@odata.nextLink":"https://graph.example.test/v1.0/users/ada%40contoso.com/drive/root/search(q='.xlsx')?$skiptoken=p2"}"""),
            };
            var books = await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftExcelConnector.WorkbooksOperation, new()));
            Assert.Equal(new[] { Drive + "/root/search(q='.xlsx')?$top=100&$select=id,name,webUrl,size,lastModifiedDateTime,parentReference,file", Drive + "/root/search(q='.xlsx')?$skiptoken=p2" }, microsoft.GraphRequests.Select(request => request.Url)); // the next link that leaves the drive never followed
            Assert.Equal(new[] { ("01A", "/Reports/Sales 2026.xlsx"), ("01C", "/Budget.xlsm") }, books.Select(book => ((string)book.Fields["id"]!, (string)book.Fields["path"]!))); // a search finds more than workbooks
            Assert.Equal(("https://contoso.sharepoint.com/x", 2048L, "2026-10-01T09:00:00Z"), ((string?)books[0].Fields["url"], (long?)books[0].Fields["size"], (string?)books[0].Fields["modified"]));
            var searching = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"value":[]}""") };
            await Contexts.DrainAsync(Connector(), Context(searching, MicrosoftExcelConnector.WorkbooksOperation, new() { ["query"] = "Ada's budget", ["max"] = "5" }));
            Assert.Equal(Drive + "/root/search(q='Ada%27%27s%20budget')?$top=5&$select=id,name,webUrl,size,lastModifiedDateTime,parentReference,file", Assert.Single(searching.GraphRequests).Url);

            var sheets = new Microsoft { Graph = (method, url) => (method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, method == HttpMethod.Delete ? "" : url.EndsWith("/worksheets/add", StringComparison.Ordinal) ? """{"id":"{00000000-0001-0000-0300-000000000000}","name":"Q4","position":3,"visibility":"Visible"}""" : """{"value":[{"id":"{1}","name":"My sheet","position":0,"visibility":"Visible"}],"name":"Orders","id":"{2}","address":"'My sheet'!A1:B5"}""") };
            (string, string, string?) Last() => (sheets.GraphRequests[^1].Method.Method, sheets.GraphRequests[^1].Url, sheets.GraphRequests[^1].Body);
            var listed = await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.WorksheetsOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx" }));
            Assert.Equal(("GET", Book + "/worksheets?$select=id,name,position,visibility", (string?)null), Last());
            Assert.Equal(("My sheet", 0L, "Visible"), ((string?)Assert.Single(listed).Fields["name"], (long?)listed[0].Fields["position"], (string?)listed[0].Fields["visibility"]));
            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.AddWorksheetOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["name"] = "Q4" })));
            Assert.Equal(("POST", Book + "/worksheets/add", "{\"name\":\"Q4\"}"), Last());
            Assert.Equal(("Q4", 3L), ((string?)made.Fields["name"], (long?)made.Fields["position"]));
            await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.AddWorksheetOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx" }));
            Assert.Equal("{}", Last().Item3); // Excel names it
            await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.ClearOperation, On(("apply_to", "contents"))));
            Assert.Equal(("POST", Sheet + "/range/clear", "{\"applyTo\":\"Contents\"}"), Last());
            var cleared = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.ClearOperation, On(("range", "A1:B2")))));
            Assert.Equal(("POST", Sheet + "/range(address='A1:B2')/clear", "{\"applyTo\":\"All\"}"), Last());
            Assert.Equal(("My sheet", "A1:B2", "All"), ((string?)cleared.Fields["sheet"], (string?)cleared.Fields["range"], (string?)cleared.Fields["cleared"]));
            var table = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.AddTableOperation, On(("range", "A1:B5"), ("has_headers", "false")))));
            Assert.Equal(("POST", Book + "/tables/add", "{\"address\":\"'My sheet'!A1:B5\",\"hasHeaders\":false}"), Last());
            Assert.Equal(("{2}", "Orders"), ((string?)table.Fields["id"], (string?)table.Fields["name"]));
            await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.AddTableOperation, On()));
            Assert.Equal((Sheet + "/usedRange?$select=address", "{\"address\":\"'My sheet'!A1:B5\",\"hasHeaders\":true}"), (sheets.GraphRequests[^2].Url, Last().Item3)); // no range: the sheet's used range
            var plain = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.ConvertTableOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders" })));
            Assert.Equal(("POST", Book + "/tables/Orders/convertToRange", (string?)null), Last());
            Assert.Equal("'My sheet'!A1:B5", (string?)plain.Fields["address"]);
            await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.DeleteTableOperation, new() { ["workbook"] = "/Reports/Sales 2026.xlsx", ["table"] = "Orders" }));
            Assert.Equal(("DELETE", Book + "/tables/Orders", (string?)null), Last());
            var dropped = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.DeleteWorksheetOperation, On())));
            Assert.Equal(("DELETE", Sheet, (string?)null), Last());
            Assert.Equal(("My sheet", true), ((string?)dropped.Fields["sheet"], (bool?)dropped.Fields["deleted"]));
            var gone = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sheets, MicrosoftExcelConnector.DeleteWorkbookOperation, new() { ["workbook"] = "01ABC" })));
            Assert.Equal(("DELETE", Drive + "/items/01ABC", (string?)null), Last());
            Assert.Equal(("01ABC", true), ((string?)gone.Fields["workbook"], (bool?)gone.Fields["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_graphs_refusal_is_its_words()
        {
            var microsoft = new Microsoft();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(microsoft, operation, parameters, settings)))).Message;
            const string read = MicrosoftExcelConnector.ReadRowsOperation;

            Assert.Equal("Name whose OneDrive holds the workbooks (the connection's user) or a document library's drive (its drive).", await Refused(read, On(), new Dictionary<string, string>()));
            Assert.Equal("'ada lovelace' is not a user - an address or a user id.", await Refused(read, On(), new Dictionary<string, string> { ["user"] = "ada lovelace" }));
            Assert.Equal("'b!x/../y' is not a drive's id.", await Refused(read, On(), new Dictionary<string, string> { ["drive"] = "b!x/../y" }));
            Assert.Equal("Name the workbook - its id, or its path in the drive (/Reports/Sales.xlsx).", await Refused(read, new()));
            Assert.Equal("'Sales.xlsx' is not a workbook - its id, or its path in the drive (/Reports/Sales.xlsx).", await Refused(read, new() { ["workbook"] = "Sales.xlsx" }));
            Assert.Equal("'/Reports/../Secrets.xlsx' is not a workbook's path - /Reports/Sales.xlsx.", await Refused(read, new() { ["workbook"] = "/Reports/../Secrets.xlsx" }));
            Assert.Equal("'/Reports:/x.xlsx' is not a workbook's path - /Reports/Sales.xlsx.", await Refused(read, new() { ["workbook"] = "/Reports:/x.xlsx" }));
            Assert.Equal("'Q1/Q2' is not a sheet's name - at most 31 characters, none of : \\ / ? * [ ].", await Refused(read, On(("sheet", "Q1/Q2"))));
            Assert.Equal("'A:B' is not a range - A1:D20 (a whole column, A:B, is not).", await Refused(read, On(("range", "A:B"))));
            Assert.Equal("'Sheet1!A1:B2' is not a range - A1:D20 (a whole column, A:B, is not).", await Refused(read, On(("range", "Sheet1!A1:B2"))));
            Assert.Equal("'yes' is not true or false (header row).", await Refused(read, On(("header_row", "yes"))));
            Assert.Equal("'Order Lines' is not a table's name or id - Table1.", await Refused(MicrosoftExcelConnector.TableRowsOperation, new() { ["workbook"] = "01ABC", ["table"] = "Order Lines" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(MicrosoftExcelConnector.TableRowsOperation, new() { ["workbook"] = "01ABC", ["table"] = "Orders", ["max"] = "0" }));
            Assert.Equal("Give the values - a record or a list of records as JSON.", await Refused(MicrosoftExcelConnector.AppendRowsOperation, On()));
            Assert.Equal("The values are not JSON - a record or a list of records.", await Refused(MicrosoftExcelConnector.AppendRowsOperation, On(("values", "Name=Ada"))));
            Assert.Equal("The values are all records or all lists, not both.", await Refused(MicrosoftExcelConnector.AppendRowsOperation, On(("values", """[{"a":1},["b"]]"""))));
            Assert.Equal("The values are records - each a JSON object.", await Refused(MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Id"), ("values", """[["b"]]"""))));
            Assert.Equal("Name the column to match on.", await Refused(MicrosoftExcelConnector.UpsertRowsOperation, On(("values", """{"Id":1}"""))));
            Assert.Equal("A record gives no 'Id' - the column to match on.", await Refused(MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Id"), ("values", """[{"Id":1},{"Name":"x"}]"""))));
            Assert.Equal("'Everything' is not what to apply - All, Contents or Formats.", await Refused(MicrosoftExcelConnector.ClearOperation, On(("apply_to", "Everything"))));
            Assert.Equal("'' is not a sheet's name - at most 31 characters, none of : \\ / ? * [ ].", await Refused(MicrosoftExcelConnector.ClearOperation, new() { ["workbook"] = "01ABC" })); // no default sheet to clear
            Assert.Equal("'-1' is not a position - 0 the top.", await Refused(MicrosoftExcelConnector.AppendTableRowsOperation, new() { ["workbook"] = "01ABC", ["table"] = "Orders", ["values"] = "{}", ["index"] = "-1" }));
            Assert.Equal("Name the column to look in.", await Refused(MicrosoftExcelConnector.LookupOperation, new() { ["workbook"] = "01ABC", ["table"] = "Orders", ["value"] = "x" }));
            Assert.StartsWith("Microsoft Excel 365 has no operation 'pivot' - workbooks, worksheets,", await Refused("pivot", On()));
            Assert.Empty(microsoft.Requests); // not even a token asked for

            var headless = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, Ledger) };
            Assert.Equal("The sheet has no column 'Email' - its headers: Id, Name, Total.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(headless, MicrosoftExcelConnector.UpdateRowsOperation, On(("match_column", "Email"), ("values", """{"Email":"x"}""")))))).Message);
            Assert.Single(headless.GraphRequests); // nothing written
            var refusing = new Microsoft { Graph = (_, _) => (HttpStatusCode.NotFound, """{"error":{"code":"ItemNotFound","message":"The resource could not be found."}}""") };
            Assert.Equal("HTTP 404 ItemNotFound: The resource could not be found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, read, On())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_drive()
        {
            var microsoft = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"name":"OneDrive","driveType":"business"}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(microsoft), "https://graph.example.test/v1.0", settings: new Dictionary<string, string> { ["user"] = "ada@contoso.com" }, credentials: Credentials));
            Assert.Equal((true, "Signed in - the drive OneDrive (business)."), (tested.Success, tested.Message));
            Assert.Equal((Drive + "?$select=name,driveType", "Bearer access-1"), (microsoft.GraphRequests[0].Url, microsoft.GraphRequests[0].Authorization));
            var nobody = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Microsoft()), "https://graph.example.test/v1.0", settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.False(nobody.Success);
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "microsoftexcel", ConnectorCategory.DataAndFiles), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(17, Connector().Operations.Count);
        }
    }
}
