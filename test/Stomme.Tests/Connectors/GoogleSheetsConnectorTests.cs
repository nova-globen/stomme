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
using Stomme.Modules.Connectors.GoogleSheets;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Sheets (n8n's Get Rows and Append Row): the refresh token exchanged at the token endpoint with the client's id
    /// and secret, the access token sent as a bearer; a sheet's rows read as records named by the header row, each its sheet row
    /// number - a range's own first row and column honoured, a blank header its column's letter, a repeated one numbered - or by
    /// column letters; records appended under the headers by name, a field no header names left out and said; an empty sheet given
    /// headers first; a list of lists written as it is; a spreadsheet id that could reach the path refused before anything is sent.
    /// </summary>
    public sealed class GoogleSheetsConnectorTests
    {
        private const string Sheet = "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgvE2upms";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        /// <summary>Google as a test sees it: the token endpoint, then the Sheets API, each request recorded with its body.</summary>
        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<string, (HttpStatusCode, string)> Sheets { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : Sheets(url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), "https://sheets.example.test/v4", operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static GoogleSheetsConnector Connector() => new(new OAuth2TokenProvider());

        [Fact]
        public async Task Rows_are_records_named_by_the_header_row_each_its_row_number_after_the_refresh_token_is_exchanged()
        {
            var google = new Google { Sheets = _ => (HttpStatusCode.OK, """{"range":"Sheet1!A1:D4","majorDimension":"ROWS","values":[["Name","Email","","Name"],["Ada","ada@example.com","x","Lovelace"],["Grace"],[]]}""") };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.ReadOperation, new() { ["spreadsheet"] = Sheet, ["sheet"] = "Sheet1" }));

            var token = google.Requests[0];
            Assert.Equal((HttpMethod.Post, "https://oauth2.example.test/token"), (token.Method, token.Url));
            Assert.Contains("grant_type=refresh_token", token.Body);
            Assert.Contains("refresh_token=refresh-1", token.Body);
            Assert.Contains("client_id=client-1", token.Body);
            var read = google.Requests[1];
            Assert.Equal(($"https://sheets.example.test/v4/spreadsheets/{Sheet}/values/Sheet1?majorDimension=ROWS", "Bearer access-1"), (read.Url, read.Authorization));

            Assert.Equal(3, records.Count);
            var ada = records[0].Fields;
            Assert.Equal((2L, "Ada", "ada@example.com", "x", "Lovelace"), ((long)ada["row_number"]!, (string?)ada["Name"], (string?)ada["Email"], (string?)ada["C"], (string?)ada["Name (2)"]));
            Assert.Equal((3L, "Grace", (string?)null), ((long)records[1].Fields["row_number"]!, (string?)records[1].Fields["Name"], (string?)records[1].Fields["Email"]));
            Assert.Equal(4L, (long)records[2].Fields["row_number"]!); // an empty row is still a row
        }

        [Fact]
        public async Task A_range_keeps_its_own_first_row_and_column_and_without_headers_columns_are_letters()
        {
            var google = new Google { Sheets = _ => (HttpStatusCode.OK, """{"range":"'My Sheet'!B3:C4","values":[["Total","Paid"],["12","yes"]]}""") };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.ReadOperation, new() { ["spreadsheet"] = Sheet, ["sheet"] = "My Sheet", ["range"] = "B3:C4", ["header_row"] = "false" }));

            Assert.EndsWith("/values/%27My%20Sheet%27%21B3%3AC4?majorDimension=ROWS", google.Requests[1].Url);
            Assert.Equal(2, records.Count);
            Assert.Equal((3L, "Total", "Paid"), ((long)records[0].Fields["row_number"]!, (string?)records[0].Fields["B"], (string?)records[0].Fields["C"]));
            Assert.Equal((4L, "12"), ((long)records[1].Fields["row_number"]!, (string?)records[1].Fields["B"]));
        }

        [Fact]
        public async Task Records_are_appended_under_the_headers_by_name_and_a_field_no_header_names_is_left_out_and_said()
        {
            var google = new Google
            {
                Sheets = url => url.Contains(":append", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"spreadsheetId":"x","updates":{"updatedRange":"Sheet1!A5:C6","updatedRows":2,"updatedCells":6}}""")
                    : (HttpStatusCode.OK, """{"range":"Sheet1!A1:Z1","values":[["Name","Email","Plan"]]}"""),
            };
            var record = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.AppendOperation, new()
            {
                ["spreadsheet"] = Sheet,
                ["values"] = """[{"Email":"ada@example.com","Name":"Ada","source":"web"},{"Name":"Grace","Plan":"Pro","Plan2":1}]""",
            }))).Fields;

            Assert.EndsWith("/values/%27Sheet1%27%211%3A1?majorDimension=ROWS", google.Requests[1].Url); // the headers read first
            var append = google.Requests[2];
            Assert.Equal((HttpMethod.Post, $"https://sheets.example.test/v4/spreadsheets/{Sheet}/values/Sheet1:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS"), (append.Method, append.Url));
            Assert.Equal("""{"values":[["Ada","ada@example.com",""],["Grace","","Pro"]]}""", append.Body);
            Assert.Equal(("Sheet1!A5:C6", 2L, "Plan2, source"), ((string?)record["updated_range"], (long)record["updated_rows"]!, (string?)record["left_out"]));
        }

        [Fact]
        public async Task An_empty_sheet_gets_the_first_records_fields_as_its_headers_and_a_list_of_lists_is_written_as_it_is()
        {
            var google = new Google
            {
                Sheets = url => url.Contains(":append", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"updates":{"updatedRange":"Sheet1!A1:B2","updatedRows":2}}""")
                    : (HttpStatusCode.OK, """{"range":"Sheet1!A1:Z1"}"""),
            };
            await Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.AppendOperation, new() { ["spreadsheet"] = Sheet, ["values"] = """{"Name":"Ada","Total":12.5}""" }));
            Assert.Equal("""{"values":[["Name","Total"],["Ada","12.5"]]}""", google.Requests.Last().Body);

            var lists = new Google { Sheets = _ => (HttpStatusCode.OK, """{"updates":{"updatedRows":1}}""") };
            await Contexts.DrainAsync(Connector(), Context(lists, GoogleSheetsConnector.AppendOperation, new() { ["spreadsheet"] = Sheet, ["values"] = """[["a", 1, true]]""" }));
            Assert.Equal("""{"values":[["a","1","true"]]}""", lists.Requests.Last().Body);
            Assert.DoesNotContain(lists.Requests, request => request.Url.Contains("1%3A1", StringComparison.Ordinal)); // no headers read for lists
        }

        [Fact]
        public async Task A_spreadsheet_id_that_could_reach_the_path_is_refused_before_anything_is_sent_and_googles_refusal_is_its_status()
        {
            var google = new Google();
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.ReadOperation, new() { ["spreadsheet"] = "../../drive/v3/files" })));
            Assert.StartsWith("'../../drive/v3/files' is not a spreadsheet id", refused.Message);
            Assert.Empty(google.Requests);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, GoogleSheetsConnector.AppendOperation, new() { ["spreadsheet"] = Sheet, ["values"] = "not json" })));

            var missing = new Google { Sheets = _ => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}""") };
            Assert.Equal("HTTP 404 NOT_FOUND.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, GoogleSheetsConnector.ReadOperation, new() { ["spreadsheet"] = Sheet })))).Message);
        }

        [Fact]
        public async Task A_test_signs_in_and_reads_the_named_spreadsheets_title()
        {
            var google = new Google { Sheets = _ => (HttpStatusCode.OK, """{"properties":{"title":"Orders 2026"}}""") };
            var settings = new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token", ["spreadsheet"] = Sheet };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), "", settings, Credentials));
            Assert.Equal((true, "Signed in; the spreadsheet is 'Orders 2026'."), (tested.Success, tested.Message));
            Assert.Equal($"https://sheets.googleapis.com/v4/spreadsheets/{Sheet}?fields=properties.title", google.Requests[1].Url); // no endpoint: Google's

            var bare = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google()), "", new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, Credentials));
            Assert.Equal((true, "Signed in to Google."), (bare.Success, bare.Message));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, ConnectorCategory.DataAndFiles, "googlesheets"), (Connector().AuthType, Assert.Single(Connector().Categories), Connector().ConnectorType));
            Assert.Equal(new[] { "read", "append" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
