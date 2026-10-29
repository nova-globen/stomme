#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Snowflake;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Snowflake (n8n's Snowflake node: a query run, rows inserted, rows updated): the account's SQL API v2 with a
    /// programmatic access token as a bearer; a statement sent asynchronously and its status asked of the account's own address;
    /// a query's values bound - @name and ? - never spliced in, outside text, quoted names and comments, a stage left alone;
    /// every part of a result read, a gzipped one too, no further than asked, each value typed by its column; rows inserted
    /// and updated by unquoted names, an object or a list parsed as JSON; what could go wrong refused before anything is sent;
    /// Snowflake's code, message and SQL state on a failure; a test naming who the token signs in as.
    /// </summary>
    public sealed class SnowflakeConnectorTests
    {
        private const string Account = "https://kafe-analytics.snowflakecomputing.com";

        private const string Statements = Account + "/api/v2/statements";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "pat-kafe-1" };

        private sealed class Snowflake : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? TokenType, string? Agent, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string, bool)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, Result("[]", "[]"), false);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-Snowflake-Authorization-Token-Type", out var type) ? string.Join(",", type) : null, request.Headers.UserAgent.ToString(), body));
                var (status, answer, zipped) = Answer(request.Method, url, body);
                if (!zipped)
                {
                    return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                }

                using var buffer = new MemoryStream();
                using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                {
                    zip.Write(Encoding.UTF8.GetBytes(answer));
                }

                var content = new ByteArrayContent(buffer.ToArray());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add("gzip");
                return new HttpResponseMessage(status) { Content = content };
            }
        }

        private static string Result(string rowType, string data, int partitions = 1, string handle = "01b0-0001") =>
            "{\"resultSetMetaData\":{\"numRows\":3,\"format\":\"jsonv2\",\"rowType\":" + rowType + ",\"partitionInfo\":[" + string.Join(",", Enumerable.Repeat("{\"rowCount\":2}", partitions)) + "]},\"data\":" + data + ",\"code\":\"090001\",\"statementHandle\":\"" + handle + "\",\"message\":\"Statement executed successfully.\"}";

        private static string Running(string handle = "01b0-0001", string statusUrl = "/api/v2/statements/01b0-0001") =>
            "{\"code\":\"333334\",\"message\":\"Asynchronous execution in progress.\",\"statementHandle\":\"" + handle + "\",\"statementStatusUrl\":\"" + statusUrl + "\"}";

        private static string Counted(string name, params string[] counts) =>
            Result("[{\"name\":\"" + name + "\",\"type\":\"fixed\",\"scale\":0}]", "[[" + string.Join(",", counts.Select(count => "\"" + count + "\"")) + "]]");

        private static Task<List<ConnectorRecord>> Run(Snowflake snowflake, string operation, Dictionary<string, string> parameters, string endpoint = Account, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new SnowflakeConnector(), Contexts.Extract(new StubEgress(snowflake), endpoint, operation, parameters, settings, credentials ?? Credentials));

        private const string RowType = "[{\"name\":\"ID\",\"type\":\"fixed\",\"scale\":0},{\"name\":\"NAME\",\"type\":\"text\"},{\"name\":\"TOTAL\",\"type\":\"fixed\",\"scale\":2},{\"name\":\"SHARE\",\"type\":\"real\"},{\"name\":\"PAID\",\"type\":\"boolean\"},"
            + "{\"name\":\"DAY\",\"type\":\"date\"},{\"name\":\"AT\",\"type\":\"timestamp_ntz\"},{\"name\":\"SEEN\",\"type\":\"timestamp_ltz\"},{\"name\":\"LOCAL\",\"type\":\"timestamp_tz\"},{\"name\":\"OPENS\",\"type\":\"time\"},{\"name\":\"EXTRA\",\"type\":\"variant\"}]";

        [Fact]
        public async Task A_query_is_bound_waited_for_and_read_part_by_part_typed_by_its_columns()
        {
            var snowflake = new Snowflake
            {
                Answer = (method, url, _) => method == HttpMethod.Post ? (HttpStatusCode.Accepted, Running(statusUrl: "https://elsewhere.example.test/steal"), false)
                    : url.EndsWith("?partition=1", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"data\":[[\"3\",\"Cy\",null,null,null,null,null,null,null,null,null]]}", true)
                    : (HttpStatusCode.OK, Result(RowType, "[[\"1\",\"Kafé AB\",\"129.50\",\"0.25\",\"true\",\"20740\",\"1791964800.000000000\",\"1791964800.500000000\",\"1791964800.000000000 1560\",\"32400.000000000\",\"{\\n  \\\"by\\\": \\\"Ada\\\"\\n}\"],"
                        + "[\"2\",\"Bo\",\"12.00\",\"1\",\"false\",\"0\",\"0.000000000\",\"0.000000000\",\"0.000000000 1440\",\"0.000000000\",\"[1,2]\"]]", partitions: 2), false),
            };
            const string Sql = "SELECT * FROM ORDERS WHERE CITY = @city AND ID > ? -- @city ? in a comment\n AND NOTE <> '@city ?' AND NOTE <> 'it''s @city' AND NOTE <> 'a\\'@city' AND \"@city\" IS NOT NULL /* @city ? */ AND X = $$@city ?$$ // @city ? too\n AND Y = 1";

            var rows = await Run(snowflake, "query", new() { ["query"] = Sql, ["@city"] = "Malmö", ["@1"] = "5", ["database"] = "SALES" }, settings: new() { ["warehouse"] = "KASSA_WH", ["database"] = "OTHER", ["role"] = "ANALYST" });
            Assert.Equal(3, rows.Count);
            var post = snowflake.Requests[0];
            Assert.Equal("POST", post.Method.Method);
            Assert.StartsWith(Statements + "?requestId=", post.Url, StringComparison.Ordinal);
            Assert.EndsWith("&async=true", post.Url, StringComparison.Ordinal);
            var body = JsonNode.Parse(post.Body!)!;
            Assert.Equal("SELECT * FROM ORDERS WHERE CITY = ? AND ID > ? -- @city ? in a comment\n AND NOTE <> '@city ?' AND NOTE <> 'it''s @city' AND NOTE <> 'a\\'@city' AND \"@city\" IS NOT NULL /* @city ? */ AND X = $$@city ?$$ // @city ? too\n AND Y = 1", body["statement"]!.GetValue<string>());
            Assert.Equal("{\"1\":{\"type\":\"TEXT\",\"value\":\"Malmö\"},\"2\":{\"type\":\"TEXT\",\"value\":\"5\"}}", body["bindings"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.Equal(("KASSA_WH", "SALES", "ANALYST", (JsonNode?)null), (body["warehouse"]!.GetValue<string>(), body["database"]!.GetValue<string>(), body["role"]!.GetValue<string>(), body["schema"])); // the step's database over the connection's
            Assert.Equal(new[] { $"{Statements}/01b0-0001", $"{Statements}/01b0-0001?partition=1" }, snowflake.Requests.Skip(1).Select(request => request.Url)); // its status asked of the account - never the address the answer named
            Assert.All(snowflake.Requests, request => Assert.Equal(("Bearer pat-kafe-1", "PROGRAMMATIC_ACCESS_TOKEN", "Stomme/1.0"), (request.Authorization, request.TokenType, request.Agent)));
            var first = rows[0].Fields;
            Assert.Equal((1L, "Kafé AB", "129.50", 0.25, true, "2026-10-14", "2026-10-14T08:00:00", "2026-10-14T08:00:00.5Z", "2026-10-14T10:00:00+02:00", "09:00:00", "{\n  \"by\": \"Ada\"\n}"),
                ((long?)first["ID"], (string?)first["NAME"], (string?)first["TOTAL"], (double?)first["SHARE"], (bool?)first["PAID"], (string?)first["DAY"], (string?)first["AT"], (string?)first["SEEN"], (string?)first["LOCAL"], (string?)first["OPENS"], (string?)first["EXTRA"]));
            var second = rows[1].Fields;
            Assert.Equal((false, "1970-01-01", "1970-01-01T00:00:00", "1970-01-01T00:00:00+00:00", "00:00:00", "[1,2]"), ((bool?)second["PAID"], (string?)second["DAY"], (string?)second["AT"], (string?)second["LOCAL"], (string?)second["OPENS"], (string?)second["EXTRA"]));
            Assert.Equal((3L, "Cy", (object?)null), ((long?)rows[2].Fields["ID"], (string?)rows[2].Fields["NAME"], rows[2].Fields["TOTAL"])); // the gzipped second part

            snowflake.Requests.Clear();
            Assert.Equal(2, (await Run(snowflake, "query", new() { ["query"] = "SELECT 1", ["max"] = "2" })).Count); // no further than asked: no second part
            Assert.DoesNotContain(snowflake.Requests, request => request.Url.Contains("partition", StringComparison.Ordinal));
            Assert.Null(JsonNode.Parse(snowflake.Requests[0].Body!)!["bindings"]);

            var stage = new Snowflake { Answer = (_, _, _) => (HttpStatusCode.OK, Counted("status", "LOADED"), false) };
            await Run(stage, null!, new() { ["query"] = "COPY INTO ORDERS FROM @kassa_stage/2026 FILE_FORMAT = (TYPE = CSV)" }); // no operation: a query
            Assert.Equal("COPY INTO ORDERS FROM @kassa_stage/2026 FILE_FORMAT = (TYPE = CSV)", JsonNode.Parse(stage.Requests[0].Body!)!["statement"]!.GetValue<string>()); // a stage left as it is
            Assert.Single(stage.Requests); // answered at once: nothing asked after it
        }

        [Fact]
        public async Task Rows_are_inserted_and_updated_by_unquoted_names_an_object_or_a_list_parsed_as_json()
        {
            var snowflake = new Snowflake { Answer = (_, _, body) => (HttpStatusCode.OK, body!.Contains("INSERT", StringComparison.Ordinal) ? Counted("number of rows inserted", "2") : Counted("number of rows updated", "1", "0"), false) };
            static (string Sql, string Bindings) Sent(string body) => (JsonNode.Parse(body)!["statement"]!.GetValue<string>(), JsonNode.Parse(body)!["bindings"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            var inserted = Assert.Single(await Run(snowflake, "insert_rows", new() { ["table"] = "SALES.PUBLIC.ORDERS", ["rows"] = "[{\"NAME\": \"Kafé AB\", \"TOTAL\": 129.5, \"PAID\": true, \"TAGS\": [\"fika\"]}, {\"name\": \"Bo\", \"TOTAL\": 12, \"PAID\": null}]" })).Fields;
            Assert.Equal(("INSERT INTO SALES.PUBLIC.ORDERS (NAME, TOTAL, PAID, TAGS) SELECT $1, $2, $3, PARSE_JSON($4) FROM VALUES (?, ?, ?, ?), (?, ?, NULL, NULL)",
                "{\"1\":{\"type\":\"TEXT\",\"value\":\"Kafé AB\"},\"2\":{\"type\":\"REAL\",\"value\":\"129.5\"},\"3\":{\"type\":\"BOOLEAN\",\"value\":\"true\"},\"4\":{\"type\":\"TEXT\",\"value\":\"[\\\"fika\\\"]\"},\"5\":{\"type\":\"TEXT\",\"value\":\"Bo\"},\"6\":{\"type\":\"FIXED\",\"value\":\"12\"}}"), Sent(snowflake.Requests[0].Body!));
            Assert.Equal(((object?)2L, (object?)"SALES.PUBLIC.ORDERS"), (inserted["inserted"], inserted["table"]));
            await Run(snowflake, "insert_rows", new() { ["table"] = "orders", ["rows"] = "{\"NAME\": \"Kafé AB\", \"TOTAL\": 129.5}", ["columns"] = "name" });
            Assert.Equal(("INSERT INTO orders (name) SELECT $1 FROM VALUES (?)", "{\"1\":{\"type\":\"TEXT\",\"value\":\"Kafé AB\"}}"), Sent(snowflake.Requests[^1].Body!)); // the columns listed alone

            snowflake.Requests.Clear();
            var updated = Assert.Single(await Run(snowflake, "update_rows", new() { ["table"] = "ORDERS", ["key"] = "order_id", ["rows"] = "[{\"ORDER_ID\": 8, \"STATUS\": \"Paid\", \"META\": {\"by\": \"Ada\"}}, {\"order_id\": \"9\", \"STATUS\": null}]" })).Fields;
            Assert.Equal(new[]
            {
                ("UPDATE ORDERS SET STATUS = ?, META = PARSE_JSON(?) WHERE order_id = ?", "{\"1\":{\"type\":\"TEXT\",\"value\":\"Paid\"},\"2\":{\"type\":\"TEXT\",\"value\":\"{\\\"by\\\":\\\"Ada\\\"}\"},\"3\":{\"type\":\"FIXED\",\"value\":\"8\"}}"),
                ("UPDATE ORDERS SET STATUS = NULL WHERE order_id = ?", "{\"1\":{\"type\":\"TEXT\",\"value\":\"9\"}}"),
            }, snowflake.Requests.Select(request => Sent(request.Body!)));
            Assert.Equal(((object?)2L, (object?)2L), (updated["updated"], updated["rows"]));
            await Run(snowflake, "update_rows", new() { ["table"] = "ORDERS", ["rows"] = "{\"ID\": 8, \"STATUS\": \"Paid\", \"NOTE\": \"x\"}", ["columns"] = "STATUS" });
            Assert.Equal("UPDATE ORDERS SET STATUS = ? WHERE ID = ?", Sent(snowflake.Requests[^1].Body!).Sql); // ID the key when none is named; the columns listed alone

            var many = new Snowflake { Answer = (_, _, body) => (HttpStatusCode.OK, Counted("number of rows inserted", body!.Split("(?)").Length > 150 ? "200" : "1"), false) };
            var rows = "[" + string.Join(",", Enumerable.Range(1, 201).Select(i => "{\"ID\": " + i + "}")) + "]";
            Assert.Equal(201L, Assert.Single(await Run(many, "insert_rows", new() { ["table"] = "ORDERS", ["rows"] = rows })).Fields["inserted"]);
            Assert.Equal(2, many.Requests.Count); // two statements of 200 rows at most
            var broken = new Snowflake { Answer = (_, _, body) => body!.Split("(?)").Length > 150 ? (HttpStatusCode.OK, Counted("number of rows inserted", "200"), false) : (HttpStatusCode.UnprocessableEntity, "{\"code\":\"100038\",\"message\":\"Numeric value 'x' is not recognized\",\"sqlState\":\"22018\"}", false) };
            Assert.Equal("HTTP 422 100038: Numeric value 'x' is not recognized (SQL state 22018) - rows 201 on were not written, 200 before them were.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(broken, "insert_rows", new() { ["table"] = "ORDERS", ["rows"] = rows }))).Message);
            var halfway = new Snowflake { Answer = (_, _, body) => body!.Contains("= ?\"", StringComparison.Ordinal) && body.Contains("\"value\":\"9\"", StringComparison.Ordinal) ? (HttpStatusCode.UnprocessableEntity, "{\"code\":\"002003\",\"message\":\"Object does not exist\"}", false) : (HttpStatusCode.OK, Counted("number of rows updated", "1", "0"), false) };
            Assert.Equal("HTTP 422 002003: Object does not exist - row 2 and those after it were not written; rows 1 to 1 were.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(halfway, "update_rows", new() { ["table"] = "ORDERS", ["rows"] = "[{\"ID\": 8, \"STATUS\": \"Paid\"}, {\"ID\": 9, \"STATUS\": \"Paid\"}, {\"ID\": 10, \"STATUS\": \"Paid\"}]" }))).Message);
            Assert.Equal(2, halfway.Requests.Count); // nothing after the failure
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_snowflakes_words_on_a_failure()
        {
            var snowflake = new Snowflake();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Account, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(snowflake, operation, parameters, endpoint, settings, credentials))).Message;
            Dictionary<string, string> Orders(params (string Name, string Value)[] more) => new[] { ("table", "ORDERS"), ("rows", "{\"ID\": 8, \"STATUS\": \"Paid\"}") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);

            Assert.Equal("Snowflake has no operation 'delete' - query, insert_rows or update_rows.", await Refused("delete", new()));
            foreach (var endpoint in new[] { "", "kafe-analytics.snowflakecomputing.com", Account + "/api/v2/statements", Account + "?account=x", "https://ada:x@kafe-analytics.snowflakecomputing.com" })
            {
                Assert.Equal("Give the account's address - https://kafe-analytics.snowflakecomputing.com.", await Refused("query", new() { ["query"] = "SELECT 1" }, endpoint));
            }

            foreach (var token in new[] { "", "pat with spaces" })
            {
                Assert.Equal("Give the programmatic access token (token) - Snowsight > your profile > Settings > Authentication.", await Refused("query", new() { ["query"] = "SELECT 1" }, credentials: new() { ["token"] = token }));
            }

            Assert.Equal("'KASSA WH' is not a warehouse's name - letters, digits, _ and $ (warehouse).", await Refused("query", new() { ["query"] = "SELECT 1" }, settings: new() { ["warehouse"] = "KASSA WH" }));
            Assert.Equal("'SALES;DROP' is not a database's name - letters, digits, _ and $ (database).", await Refused("query", new() { ["query"] = "SELECT 1", ["database"] = "SALES;DROP" }));
            Assert.Equal("Give the query (query).", await Refused("query", new()));
            Assert.Equal("'@city-x' is not a parameter's name - @ and letters, digits and _, or @1, @2 for each ? (parameters).", await Refused("query", new() { ["query"] = "SELECT 1", ["@city-x"] = "x" }));
            Assert.Equal("The query's ? number 2 has no value - give it as '@2 = value' (parameters).", await Refused("query", new() { ["query"] = "SELECT ? , ?", ["@1"] = "x" }));
            Assert.Equal("The query has no ? number 2 - it has 1 (parameters).", await Refused("query", new() { ["query"] = "SELECT ?", ["@1"] = "x", ["@2"] = "y" }));
            Assert.Equal("The query has no @city - give a parameter only for a value it names (parameters).", await Refused("query", new() { ["query"] = "SELECT '@city' -- @city", ["@city"] = "x" }));
            Assert.Equal("The query has no @kassa_stage - give a parameter only for a value it names (parameters).", await Refused("query", new() { ["query"] = "LIST @kassa_stage/2026", ["@kassa_stage"] = "x" })); // a stage is never a value
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("query", new() { ["query"] = "SELECT 1", ["max"] = "0" }));
            Assert.Equal("'601' is not a number from 1 to 600 (wait).", await Refused("query", new() { ["query"] = "SELECT 1", ["wait"] = "601" }));
            Assert.Equal("Give the table - ORDERS, or SALES.PUBLIC.ORDERS (table).", await Refused("insert_rows", new() { ["rows"] = "{\"ID\": 1}" }));
            foreach (var table in new[] { "ORDERS; DROP TABLE X", "\"Orders\"", "A.B.C.D", "SALES..ORDERS" })
            {
                Assert.Equal($"'{table}' is not a table's name - ORDERS, or SALES.PUBLIC.ORDERS, unquoted (table).", await Refused("insert_rows", Orders(("table", table))));
            }

            Assert.Equal("Give the rows - a JSON object or a list of them (rows).", await Refused("insert_rows", new() { ["table"] = "ORDERS" }));
            Assert.Equal("The rows are not a JSON object or a list of them - {\"NAME\": \"Kafé AB\"} (rows).", await Refused("insert_rows", Orders(("rows", "ID = 1"))));
            Assert.Equal("The rows are not a JSON object or a list of them - {\"NAME\": \"Kafé AB\"} (rows).", await Refused("insert_rows", Orders(("rows", "[{\"ID\": 1}, 2]"))));
            foreach (var rows in new[] { "[]", "[{}]", "{}" })
            {
                Assert.Equal("Give from 1 to 10,000 rows, each with a column (rows).", await Refused("insert_rows", Orders(("rows", rows))));
            }

            Assert.Equal("Give from 1 to 1,000 rows, each with a column (rows).", await Refused("update_rows", Orders(("rows", "[" + string.Join(",", Enumerable.Range(1, 1001).Select(i => "{\"ID\": " + i + "}")) + "]"))));
            Assert.Equal("'TOTAL AMOUNT' is not a column's name - letters, digits, _ and $ (rows).", await Refused("insert_rows", Orders(("rows", "{\"TOTAL AMOUNT\": 1}"))));
            foreach (var columns in new[] { "NAME, name", "NAME;X", ", ," })
            {
                Assert.Equal($"'{columns}' is not a list of columns - NAME, TOTAL (columns).", await Refused("insert_rows", Orders(("columns", columns))));
            }

            Assert.Equal("'ORDER ID' is not a column's name - letters, digits, _ and $ (key).", await Refused("update_rows", Orders(("key", "ORDER ID"))));
            Assert.Equal("Row 1 has no value of ID, a text or a number, to find it by (rows).", await Refused("update_rows", Orders(("rows", "{\"STATUS\": \"Paid\"}"))));
            Assert.Equal("Row 2 has no value of ID, a text or a number, to find it by (rows).", await Refused("update_rows", Orders(("rows", "[{\"ID\": 8, \"STATUS\": \"Paid\"}, {\"ID\": [9], \"STATUS\": \"Paid\"}]"))));
            Assert.Equal("Row 1 has nothing to set but its key (rows).", await Refused("update_rows", Orders(("rows", "{\"ID\": 8}"))));
            Assert.Equal("Row 1 has no NOTE (rows).", await Refused("update_rows", Orders(("columns", "STATUS, NOTE"))));
            Assert.Empty(snowflake.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "query") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Snowflake { Answer = (_, _, _) => (status, answer, false) }, operation, operation == "query" ? new() { ["query"] = "SELECT * FROM ORDERS" } : Orders()))).Message;
            Assert.Equal("HTTP 422 002003: SQL compilation error:\nObject 'ORDERS' does not exist or not authorized. (SQL state 42S02)", await Failed(HttpStatusCode.UnprocessableEntity, "{\"code\":\"002003\",\"message\":\"SQL compilation error:\\nObject 'ORDERS' does not exist or not authorized.\",\"sqlState\":\"42S02\",\"statementHandle\":\"01b0-0002\"}"));
            Assert.Equal("HTTP 401 390303: Invalid OAuth access token.", await Failed(HttpStatusCode.Unauthorized, "{\"code\":\"390303\",\"message\":\"Invalid OAuth access token.\"}"));
            Assert.Equal("HTTP 422 " + new string('e', 300), await Failed(HttpStatusCode.UnprocessableEntity, "{\"message\":\"" + new string('e', 400) + "\"}", "insert_rows")); // a long one cut
            Assert.Equal("Snowflake answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "update_rows"));
            Assert.Equal("Snowflake answered the statement with nothing.", await Failed(HttpStatusCode.OK, ""));
            Assert.Equal("Snowflake took the statement with no handle to ask after it by.", await Failed(HttpStatusCode.Accepted, "{\"code\":\"333334\"}"));
            Assert.Equal("Snowflake took the statement with no handle to ask after it by.", await Failed(HttpStatusCode.Accepted, Running(handle: "../../elsewhere")));
            var slow = new Snowflake { Answer = (_, _, _) => (HttpStatusCode.Accepted, Running(), false) };
            Assert.Equal("Snowflake had not finished the statement in 1 seconds - its statement 01b0-0001 goes on: give it longer (wait).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(slow, "query", new() { ["query"] = "SELECT 1", ["wait"] = "1" }))).Message);
            Assert.All(slow.Requests.Skip(1), request => Assert.Equal(("GET", $"{Statements}/01b0-0001"), (request.Method.Method, request.Url)));
        }

        [Fact]
        public async Task A_test_names_who_the_token_signs_in_as()
        {
            Task<ConnectionTestResult> Test(Snowflake snowflake, Dictionary<string, string>? settings = null) =>
                new SnowflakeConnector().TestConnectionAsync(Contexts.Test(new StubEgress(snowflake), Account, settings, Credentials));
            var signed = new Snowflake { Answer = (_, _, _) => (HttpStatusCode.OK, Result("[{\"name\":\"CURRENT_USER()\",\"type\":\"text\"},{\"name\":\"CURRENT_ROLE()\",\"type\":\"text\"},{\"name\":\"CURRENT_WAREHOUSE()\",\"type\":\"text\"}]", "[[\"ADA\",\"ANALYST\",\"KASSA_WH\"]]"), false) };
            Assert.Equal((true, "Snowflake: signed in as ADA with the role ANALYST - the warehouse KASSA_WH."), ((await Test(signed, new() { ["warehouse"] = "KASSA_WH", ["role"] = "ANALYST" })).Success, (await Test(signed)).Message));
            var sent = JsonNode.Parse(signed.Requests[0].Body!)!;
            Assert.Equal(("SELECT CURRENT_USER(), CURRENT_ROLE(), CURRENT_WAREHOUSE()", "KASSA_WH", "ANALYST"), (sent["statement"]!.GetValue<string>(), sent["warehouse"]!.GetValue<string>(), sent["role"]!.GetValue<string>()));
            var none = new Snowflake { Answer = (_, _, _) => (HttpStatusCode.OK, Result("[]", "[[\"ADA\",\"PUBLIC\",null]]"), false) };
            Assert.Equal("Snowflake: signed in as ADA with the role PUBLIC - the warehouse none.", (await Test(none)).Message);
            var refused = await Test(new Snowflake { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"code\":\"394400\",\"message\":\"Programmatic access token is invalid.\"}", false) });
            Assert.Equal((false, "HTTP 401 394400: Programmatic access token is invalid."), (refused.Success, refused.Message));
            var connector = new SnowflakeConnector();
            Assert.Equal((AuthenticationType.Bearer, "snowflake", 3), (connector.AuthType, connector.ConnectorType, connector.Operations.Count));
            var query = connector.Operations.Single(operation => operation.Name == "query").Parameters!.Fields;
            Assert.Equal(("1000", "60"), (query.Single(field => field.Name == "max").Default, query.Single(field => field.Name == "wait").Default));
        }
    }
}
