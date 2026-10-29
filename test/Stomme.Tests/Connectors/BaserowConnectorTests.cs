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
using Stomme.Modules.Connectors.Baserow;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Baserow (n8n's Baserow node): a database token as <c>Token ...</c>; a table's rows by their fields' names, page
    /// by page up to <c>max</c>, filtered by Baserow's own filter types (a field by its name or by its id, n8n's way, filtered by
    /// id), searched, sorted and some fields - a name with a comma quoted as Baserow reads one - from a view; a row got, created,
    /// updated and deleted, its fields by name or id; up to 200 at once in Baserow's batch; the tables and a table's fields;
    /// what could go wrong refused before anything is sent; Baserow's error and detail said with its status.
    /// </summary>
    public sealed class BaserowConnectorTests
    {
        private const string Api = "https://api.baserow.io";
        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "kafe-db-token" };
        private const string Fields = "[{\"id\":101,\"name\":\"Name\",\"type\":\"text\",\"primary\":true},{\"id\":102,\"name\":\"Status\",\"type\":\"single_select\"},{\"id\":103,\"name\":\"Total, SEK\",\"type\":\"number\"}]";

        private sealed record Sent(HttpMethod Method, string Url, string? Authorization, string Body);

        private sealed class Baserow : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, HttpResponseMessage> Answer { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                return sent.Url.EndsWith("/api/database/fields/table/42/", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, Fields) : Answer(sent);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static Task<List<ConnectorRecord>> Run(Baserow baserow, string operation, Dictionary<string, string> parameters, string endpoint = "") =>
            Contexts.DrainAsync(new BaserowConnector(), Contexts.Extract(new StubEgress(baserow), endpoint, operation, parameters, null, Credentials));

        private static async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null)
        {
            var baserow = new Baserow();
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new BaserowConnector(), Contexts.Extract(new StubEgress(baserow), endpoint, operation, parameters, null, credentials ?? Credentials)));
            Assert.Empty(baserow.Requests); // refused before anything was sent
            return refusal.Message;
        }

        private static string Page(bool more, params int[] ids) =>
            "{\"count\":5,\"next\":" + (more ? "\"https://api.baserow.io/api/database/rows/table/42/?page=2\"" : "null") + ",\"previous\":null,\"results\":["
            + string.Join(",", ids.Select(id => "{\"id\":" + id + ",\"order\":\"" + id + ".00000000000000000000\",\"Name\":\"Kafé " + id + "\",\"Status\":{\"id\":1,\"value\":\"Paid\",\"color\":\"green\"},\"Total, SEK\":\"99.50\",\"Paid\":true}")) + "]}";

        [Fact]
        public async Task Rows_come_page_by_page_by_their_fields_names_filtered_by_id_and_sorted_by_name()
        {
            var baserow = new Baserow { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("page=2", StringComparison.Ordinal) ? Page(false, 3) : Page(true, 1, 2)) };
            var rows = await Run(baserow, "rows", new()
            {
                ["table"] = "42",
                ["filters"] = "[{\"field\": \"Status\", \"type\": \"single_select_equal\", \"value\": \"1\"}, {\"field\": 103, \"type\": \"higher_than\", \"value\": 50}, {\"field\": \"field_101\", \"type\": \"contains\", \"value\": \"Kafé & co\"}]",
                ["filter_type"] = "or",
                ["search"] = "kafé",
                ["order_by"] = "-field_103, Name",
                ["include"] = "Name, \"Total, SEK\"",
                ["view"] = "77",
                ["max"] = "3",
            });
            Assert.Equal(new long?[] { 1, 2, 3 }, rows.Select(row => (long?)row.Fields["id"]));
            Assert.Equal(("Kafé 1", "99.50", true, "{\"id\":1,\"value\":\"Paid\",\"color\":\"green\"}"), ((string?)rows[0].Fields["Name"], (string?)rows[0].Fields["Total, SEK"], (bool?)rows[0].Fields["Paid"], (string?)rows[0].Fields["Status"]));
            var calls = baserow.Requests.Where(sent => !sent.Url.Contains("/fields/", StringComparison.Ordinal)).ToList();
            const string Query = "user_field_names=true&search=kaf%C3%A9&order_by=-%22Total%2C%20SEK%22%2CName&include=Name%2C%22Total%2C%20SEK%22&view_id=77&filter_type=OR"
                + "&filter__field_102__single_select_equal=1&filter__field_103__higher_than=50&filter__field_101__contains=Kaf%C3%A9%20%26%20co";
            Assert.Equal(new[] { $"{Api}/api/database/rows/table/42/?{Query}&size=3&page=1", $"{Api}/api/database/rows/table/42/?{Query}&size=3&page=2" }, calls.Select(sent => sent.Url));
            Assert.All(baserow.Requests, sent => Assert.Equal((HttpMethod.Get, "Token kafe-db-token"), (sent.Method, sent.Authorization)));
            Assert.Single(baserow.Requests, sent => sent.Url == $"{Api}/api/database/fields/table/42/"); // the table's fields read once

            var plain = new Baserow { Answer = _ => Json(HttpStatusCode.OK, Page(true, 1, 2)) };
            Assert.Single(await Run(plain, "rows", new() { ["table"] = "42", ["max"] = "1" })); // no further than asked, within a page
            Assert.Equal($"{Api}/api/database/rows/table/42/?user_field_names=true&size=1&page=1", Assert.Single(plain.Requests).Url); // names alone: no fields read

            var last = new Baserow { Answer = sent => Json(HttpStatusCode.OK, sent.Url.Contains("page=2", StringComparison.Ordinal) ? Page(false, 3) : Page(true, 1, 2)) };
            Assert.Equal(3, (await Run(last, "rows", new() { ["table"] = "42" })).Count);
            Assert.Equal(2, last.Requests.Count); // a page naming no next is the last, though fewer than asked came

            var empty = new Baserow { Answer = _ => Json(HttpStatusCode.OK, Page(true)) };
            Assert.Empty(await Run(empty, "rows", new() { ["table"] = "42" }));
            Assert.Single(empty.Requests); // an empty page is the last, whatever it names next
        }

        [Fact]
        public async Task A_row_is_got_created_updated_and_deleted_its_fields_by_name_or_by_id()
        {
            var baserow = new Baserow { Answer = sent => sent.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, "{\"id\":7,\"Name\":\"Kafé Ada\",\"Total, SEK\":\"99.50\"}") };
            var got = Assert.Single(await Run(baserow, "get_row", new() { ["table"] = "42", ["row"] = "7" })).Fields;
            Assert.Equal((7L, "Kafé Ada"), ((long?)got["id"], (string?)got["Name"]));
            Assert.Equal($"{Api}/api/database/rows/table/42/7/?user_field_names=true", baserow.Requests.Last().Url);

            await Run(baserow, "create_row", new() { ["table"] = "42", ["fields"] = "{\"Name\": \"Kafé Ada\", \"field_103\": 99.5, \"Status\": 1}" });
            var created = baserow.Requests.Last();
            Assert.Equal((HttpMethod.Post, $"{Api}/api/database/rows/table/42/?user_field_names=true", "{\"Name\":\"Kafé Ada\",\"Total, SEK\":99.5,\"Status\":1}"), (created.Method, created.Url, created.Body)); // an id written as its name

            await Run(baserow, "update_row", new() { ["table"] = "42", ["row"] = "7", ["fields"] = "{\"Status\": 2}" });
            Assert.Equal((HttpMethod.Patch, $"{Api}/api/database/rows/table/42/7/?user_field_names=true", "{\"Status\":2}"), (baserow.Requests.Last().Method, baserow.Requests.Last().Url, baserow.Requests.Last().Body));

            var deleted = Assert.Single(await Run(baserow, "delete_row", new() { ["table"] = "42", ["row"] = "7" })).Fields;
            Assert.Equal((HttpMethod.Delete, $"{Api}/api/database/rows/table/42/7/"), (baserow.Requests.Last().Method, baserow.Requests.Last().Url));
            Assert.Equal((7L, true), ((long?)deleted["id"], (bool?)deleted["deleted"]));
        }

        [Fact]
        public async Task Up_to_200_rows_at_once_in_baserows_batch()
        {
            var baserow = new Baserow { Answer = sent => sent.Url.EndsWith("batch-delete/", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, "{\"items\":[{\"id\":8,\"Name\":\"Bo\"},{\"id\":9,\"Name\":\"Cy\"}]}") };
            var created = await Run(baserow, "create_rows", new() { ["table"] = "42", ["rows"] = "[{\"Name\": \"Bo\"}, {\"field_101\": \"Cy\"}]" });
            Assert.Equal(new[] { "Bo", "Cy" }, created.Select(row => (string?)row.Fields["Name"]));
            var sent = baserow.Requests.Last();
            Assert.Equal((HttpMethod.Post, $"{Api}/api/database/rows/table/42/batch/?user_field_names=true", "{\"items\":[{\"Name\":\"Bo\"},{\"Name\":\"Cy\"}]}"), (sent.Method, sent.Url, sent.Body));

            await Run(baserow, "update_rows", new() { ["table"] = "42", ["rows"] = "[{\"id\": 8, \"Status\": 2}, {\"id\": \"9\", \"Status\": 3}]" });
            Assert.Equal((HttpMethod.Patch, "{\"items\":[{\"id\":8,\"Status\":2},{\"id\":\"9\",\"Status\":3}]}"), (baserow.Requests.Last().Method, baserow.Requests.Last().Body));

            var deleted = await Run(baserow, "delete_rows", new() { ["table"] = "42", ["rows"] = "8, 9" });
            Assert.Equal((HttpMethod.Post, $"{Api}/api/database/rows/table/42/batch-delete/", "{\"items\":[8,9]}"), (baserow.Requests.Last().Method, baserow.Requests.Last().Url, baserow.Requests.Last().Body));
            Assert.Equal(new long?[] { 8, 9 }, deleted.Select(row => (long?)row.Fields["id"]));
        }

        [Fact]
        public async Task The_tables_a_tables_fields_and_a_test_counting_the_tables()
        {
            var baserow = new Baserow { Answer = _ => Json(HttpStatusCode.OK, "[{\"id\":42,\"name\":\"Kassa\",\"order\":1,\"database_id\":5},{\"id\":43,\"name\":\"Kunder\",\"order\":2,\"database_id\":5}]") };
            var tables = await Run(baserow, "tables", new());
            Assert.Equal(new[] { "Kassa", "Kunder" }, tables.Select(table => (string?)table.Fields["name"]));
            Assert.Equal($"{Api}/api/database/tables/all-tables/", baserow.Requests.Last().Url);
            var fields = await Run(baserow, "fields", new() { ["table"] = "42" });
            Assert.Equal(new[] { "Name", "Status", "Total, SEK" }, fields.Select(field => (string?)field.Fields["name"]));

            var test = await new BaserowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(baserow), "https://baserow.kafe.example/", credentials: Credentials));
            Assert.Equal((true, "Baserow: 2 tables seen."), (test.Success, test.Message));
            Assert.Equal("https://baserow.kafe.example/api/database/tables/all-tables/", baserow.Requests.Last().Url); // a self-hosted Baserow's own address

            var refused = new Baserow { Answer = _ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"ERROR_INVALID_TOKEN\",\"detail\":\"The provided token is invalid.\"}") };
            var failed = await new BaserowConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), "", credentials: Credentials));
            Assert.Equal((false, "Baserow answered HTTP 401: ERROR_INVALID_TOKEN - The provided token is invalid."), (failed.Success, failed.Message));
        }

        [Fact]
        public async Task Baserows_error_and_detail_are_said_with_its_status()
        {
            var missing = new Baserow { Answer = _ => Json(HttpStatusCode.NotFound, "{\"error\":\"ERROR_ROW_DOES_NOT_EXIST\",\"detail\":\"The row 9 does not exist.\"}") };
            Assert.Equal("Baserow answered HTTP 404: ERROR_ROW_DOES_NOT_EXIST - The row 9 does not exist.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "get_row", new() { ["table"] = "42", ["row"] = "9" }))).Message);
            var invalid = new Baserow { Answer = _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"ERROR_REQUEST_BODY_VALIDATION\",\"detail\":{\"Total, SEK\":[{\"error\":\"A valid number is required.\",\"code\":\"invalid\"}]}}") };
            Assert.Equal("Baserow answered HTTP 400: ERROR_REQUEST_BODY_VALIDATION - {\"Total, SEK\":[{\"error\":\"A valid number is required.\",\"code\":\"invalid\"}]}", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(invalid, "create_row", new() { ["table"] = "42", ["fields"] = "{\"Total, SEK\": \"x\"}" }))).Message);
            var html = new Baserow { Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>") } };
            Assert.Equal("Baserow answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(html, "tables", new()))).Message);
            var unknown = new Baserow();
            Assert.Equal("'field_999' is not a field of table 42.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "create_row", new() { ["table"] = "42", ["fields"] = "{\"field_999\": 1}" }))).Message);
            Assert.Equal("'Kund' is not a field of table 42.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "rows", new() { ["table"] = "42", ["filters"] = "[{\"field\": \"Kund\", \"value\": \"Ada\"}]" }))).Message);
            Assert.DoesNotContain(unknown.Requests, sent => sent.Url.Contains("/rows/", StringComparison.Ordinal)); // no row asked for by a field that is not there
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent()
        {
            Assert.Equal("Baserow has no operation 'upsert' - rows, get_row, create_row, update_row, delete_row, create_rows, update_rows, delete_rows, tables or fields.", await Refused("upsert", new()));
            Assert.Equal("Give the table - its id.", await Refused("rows", new()));
            Assert.Equal("'kassa' is not a table's id - digits.", await Refused("rows", new() { ["table"] = "kassa" }));
            Assert.Equal("Give the row - its id.", await Refused("get_row", new() { ["table"] = "42" }));
            Assert.Equal("'7/../8' is not a row's id - digits.", await Refused("delete_row", new() { ["table"] = "42", ["row"] = "7/../8" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("rows", new() { ["table"] = "42", ["max"] = "0" }));
            Assert.Equal("'XOR' is not AND or OR (filter_type).", await Refused("rows", new() { ["table"] = "42", ["filter_type"] = "XOR" }));
            Assert.Equal("The filters are not JSON.", await Refused("rows", new() { ["table"] = "42", ["filters"] = "Status = Paid" }));
            Assert.Equal("The filters are not a JSON list of up to 100 conditions - [{\"field\": \"Status\", \"type\": \"equal\", \"value\": \"Paid\"}].", await Refused("rows", new() { ["table"] = "42", ["filters"] = "{\"field\": \"Status\"}" }));
            Assert.Equal("Each filter names its field - {\"field\": \"Status\", \"type\": \"equal\", \"value\": \"Paid\"}.", await Refused("rows", new() { ["table"] = "42", ["filters"] = "[{\"type\": \"equal\"}]" }));
            Assert.Equal("'equal&x=1' is not one of Baserow's filter types - equal, contains, higher_than...", await Refused("rows", new() { ["table"] = "42", ["filters"] = "[{\"field\": \"Status\", \"type\": \"equal&x=1\"}]" }));
            Assert.Equal("A filter's value is one value - text, a number or true/false.", await Refused("rows", new() { ["table"] = "42", ["filters"] = "[{\"field\": \"Status\", \"value\": [1]}]" }));
            Assert.Equal("'Name,,-' is not a list of fields - Status,-Total (order_by).", await Refused("rows", new() { ["table"] = "42", ["order_by"] = "Name,,-" }));
            Assert.Equal("'x' is not a view's id - digits.", await Refused("rows", new() { ["table"] = "42", ["view"] = "x" }));
            Assert.Equal("The search is one line of up to 1,000 characters.", await Refused("rows", new() { ["table"] = "42", ["search"] = "a\nb" }));
            Assert.Equal("Give the fields - a JSON object.", await Refused("create_row", new() { ["table"] = "42" }));
            Assert.Equal("The fields are not a JSON object - {\"Name\": \"Ada\"}.", await Refused("update_row", new() { ["table"] = "42", ["row"] = "7", ["fields"] = "[1]" }));
            Assert.Equal("Give one to 200 rows - a JSON list of objects.", await Refused("create_rows", new() { ["table"] = "42", ["rows"] = "[" + string.Join(",", Enumerable.Repeat("{}", 201)) + "]" }));
            Assert.Equal("Each row to update needs its id - {\"id\": 7, ...}.", await Refused("update_rows", new() { ["table"] = "42", ["rows"] = "[{\"Status\": 2}]" }));
            Assert.Equal("Give one to 200 rows' ids - digits, comma-separated.", await Refused("delete_rows", new() { ["table"] = "42", ["rows"] = "8, x" }));
            Assert.Equal("The Baserow connection has no database token.", await Refused("tables", new(), credentials: new()));
            Assert.Equal("The database token is one word of printable characters.", await Refused("tables", new(), credentials: new() { ["api-key"] = "abc\r\nX-Evil: 1" }));
            Assert.Equal("'https://baserow.kafe.example/?a=1' is not a Baserow's address - https://api.baserow.io, or a self-hosted Baserow's own.", await Refused("tables", new(), endpoint: "https://baserow.kafe.example/?a=1"));
        }
    }
}
