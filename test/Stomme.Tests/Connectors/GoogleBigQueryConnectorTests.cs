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
using Stomme.Modules.Connectors.GoogleBigQuery;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google BigQuery (n8n's Google BigQuery node: a query run, rows inserted): the OAuth client's refresh token at
    /// Google's token endpoint, the BigQuery API v2 with the access token as a bearer; a query in a project with a default
    /// dataset, a location and named parameters, its job waited for no longer than asked, its rows read page by page by
    /// BigQuery's page token no further than asked and typed by the schema; rows inserted in batches of 500 with an insert id
    /// each, a refused row a failure naming it - or counted when invalid rows are skipped; what could go wrong refused before
    /// a token is asked for; Google's status and message on a failure; a test naming what the connection can see.
    /// </summary>
    public sealed class GoogleBigQueryConnectorTests
    {
        private const string Api = "https://bigquery.googleapis.com/bigquery/v2";

        private const string TokenUrl = "https://oauth2.googleapis.com/token";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url == TokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"ya29.bq\",\"expires_in\":3599,\"token_type\":\"Bearer\"}") : Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new GoogleBigQueryConnector(new OAuth2TokenProvider()), Contexts.Extract(new StubEgress(google), "", operation, parameters, settings, credentials ?? Credentials));

        private const string Schema = "\"schema\":{\"fields\":[{\"name\":\"name\",\"type\":\"STRING\",\"mode\":\"NULLABLE\"},{\"name\":\"orders\",\"type\":\"INTEGER\"},{\"name\":\"total\",\"type\":\"NUMERIC\"},{\"name\":\"share\",\"type\":\"FLOAT\"},{\"name\":\"active\",\"type\":\"BOOLEAN\"},{\"name\":\"seen\",\"type\":\"TIMESTAMP\"},{\"name\":\"tags\",\"type\":\"STRING\",\"mode\":\"REPEATED\"},{\"name\":\"address\",\"type\":\"RECORD\",\"fields\":[{\"name\":\"city\",\"type\":\"STRING\"},{\"name\":\"zip\",\"type\":\"INTEGER\"}]},{\"name\":\"day\",\"type\":\"DATE\"}]}";

        private static string Rows(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i =>
            "{\"f\":[{\"v\":\"Kafé " + i + "\"},{\"v\":\"" + i + "\"},{\"v\":\"129.123456789012345678\"},{\"v\":\"0.25\"},{\"v\":\"true\"},{\"v\":\"1.7289E9\"},{\"v\":[{\"v\":\"fika\"},{\"v\":\"bröd\"}]},{\"v\":{\"f\":[{\"v\":\"Stockholm\"},{\"v\":\"11122\"}]}},{\"v\":null}]}")) + "]";

        [Fact]
        public async Task A_query_is_run_waited_for_and_read_page_by_page_its_values_typed_by_the_schema()
        {
            var google = new Google
            {
                Answer = (method, url, _) => method == HttpMethod.Post
                    ? (HttpStatusCode.OK, "{\"jobReference\":{\"projectId\":\"kafe-analytics\",\"jobId\":\"job_1\",\"location\":\"EU\"},\"jobComplete\":false}")
                    : url.Contains("pageToken=p2", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(1001, 30) + "}")
                    : url.Contains("pageToken=p1", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(1, 1000) + ",\"pageToken\":\"p2\"}")
                    : (HttpStatusCode.OK, "{\"jobComplete\":true," + Schema + ",\"rows\":[],\"pageToken\":\"p1\",\"totalRows\":\"1030\"}"),
            };

            var rows = await Run(google, "query", new()
            {
                ["project"] = "kafe-analytics", ["query"] = "SELECT * FROM orders WHERE day = @day AND city = @city", ["dataset"] = "sales", ["location"] = "EU",
                ["@day"] = "2026-10-14", ["@city"] = "Malmö", ["@min"] = "3", ["max"] = "1010",
            });
            Assert.Equal(1010, rows.Count); // no further than asked
            Assert.Equal(TokenUrl, google.Requests[0].Url);
            Assert.Contains("grant_type=refresh_token", google.Requests[0].Body);
            Assert.Equal(("POST", $"{Api}/projects/kafe-analytics/queries", (string?)"{\"query\":\"SELECT * FROM orders WHERE day = @day AND city = @city\",\"useLegacySql\":false,\"maxResults\":1000,\"timeoutMs\":10000,\"defaultDataset\":{\"projectId\":\"kafe-analytics\",\"datasetId\":\"sales\"},\"location\":\"EU\",\"parameterMode\":\"NAMED\",\"queryParameters\":[{\"name\":\"day\",\"parameterType\":{\"type\":\"STRING\"},\"parameterValue\":{\"value\":\"2026-10-14\"}},{\"name\":\"city\",\"parameterType\":{\"type\":\"STRING\"},\"parameterValue\":{\"value\":\"Malmö\"}},{\"name\":\"min\",\"parameterType\":{\"type\":\"STRING\"},\"parameterValue\":{\"value\":\"3\"}}]}"), (google.Requests[1].Method.Method, google.Requests[1].Url, google.Requests[1].Body));
            Assert.Equal(new[]
            {
                $"{Api}/projects/kafe-analytics/queries/job_1?timeoutMs=10000&maxResults=1000&location=EU", // the job waited for
                $"{Api}/projects/kafe-analytics/queries/job_1?timeoutMs=10000&maxResults=1000&location=EU&pageToken=p1",
                $"{Api}/projects/kafe-analytics/queries/job_1?timeoutMs=10000&maxResults=1000&location=EU&pageToken=p2",
            }, google.Requests.Skip(2).Select(request => request.Url));
            Assert.All(google.Requests.Skip(1), request => Assert.Equal("Bearer ya29.bq", request.Authorization));
            var first = rows[0].Fields;
            Assert.Equal(("Kafé 1", 1L, "129.123456789012345678", 0.25, true, "2024-10-14T10:00:00Z", (object?)null), ((string?)first["name"], (long?)first["orders"], (string?)first["total"], (double?)first["share"], (bool?)first["active"], (string?)first["seen"], first["day"]));
            Assert.Equal(("[\"fika\",\"bröd\"]", "{\"city\":\"Stockholm\",\"zip\":11122}"), ((string?)first["tags"], (string?)first["address"])); // a repeated field and a record as JSON, typed within

            var small = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"jobReference\":{\"jobId\":\"job_2\"},\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(1, 3) + ",\"pageToken\":\"p9\"}") };
            Assert.Equal(2, (await Run(small, null!, new() { ["query"] = "SELECT 1", ["max"] = "2" }, new() { ["project"] = "kafe-analytics" })).Count); // no operation: a query; the connection's project
            Assert.Equal("{\"query\":\"SELECT 1\",\"useLegacySql\":false,\"maxResults\":2,\"timeoutMs\":10000}", small.Requests[1].Body); // a page no larger than asked
            Assert.Equal(2, small.Requests.Count); // complete at once, enough rows: nothing more asked
            var legacy = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"jobReference\":{\"jobId\":\"job_3\"},\"jobComplete\":true,\"schema\":{\"fields\":[{\"name\":\"n\",\"type\":\"INTEGER\"}]},\"rows\":[{\"f\":[{\"v\":\"99999999999999999999\"}]}]}") };
            var big = Assert.Single(await Run(legacy, "query", new() { ["project"] = "kafe-analytics", ["query"] = "SELECT n FROM [sales.orders]", ["legacy_sql"] = "True" })).Fields;
            Assert.Equal("99999999999999999999", (string?)big["n"]); // an integer too large for a number kept as text
            Assert.Contains("\"useLegacySql\":true", legacy.Requests[1].Body);

            var endless = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"jobReference\":{\"jobId\":\"job_6\"},\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(1, 500) + ",\"pageToken\":\"" + Guid.NewGuid().ToString("N") + "\"}") };
            Assert.Equal(1000, (await Run(endless, "query", new() { ["project"] = "kafe-analytics", ["query"] = "SELECT 1" })).Count); // 1,000 at most when no max is given
            var located = new Google { Answer = (method, _, _) => (HttpStatusCode.OK, method == HttpMethod.Post ? "{\"jobReference\":{\"jobId\":\"job_5\",\"location\":\"US\"},\"jobComplete\":false}" : "{\"jobComplete\":true,\"schema\":{\"fields\":[]}}") };
            Assert.Empty(await Run(located, "query", new() { ["project"] = "kafe-analytics", ["query"] = "SELECT 1" }));
            Assert.Equal($"{Api}/projects/kafe-analytics/queries/job_5?timeoutMs=10000&maxResults=1000&location=US", located.Requests[^1].Url); // the job waited for where BigQuery ran it
            var slow = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"jobReference\":{\"jobId\":\"job_4\"},\"jobComplete\":false}") };
            Assert.Equal("BigQuery had not finished the query in 1 seconds - its job job_4 goes on: give it longer (wait).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(slow, "query", new() { ["project"] = "kafe-analytics", ["query"] = "SELECT 1", ["wait"] = "1" }))).Message);
        }

        [Fact]
        public async Task Rows_are_inserted_in_batches_each_with_an_insert_id_a_refused_row_a_failure()
        {
            var google = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"kind\":\"bigquery#tableDataInsertAllResponse\"}") };
            var many = "[" + string.Join(",", Enumerable.Range(1, 1201).Select(i => "{\"name\": \"Kafé " + i + "\", \"total\": " + i + ".5}")) + "]";

            var result = Assert.Single(await Run(google, "insert_rows", new() { ["project"] = "kafe-analytics", ["dataset"] = "sales", ["table"] = "orders 2026", ["rows"] = many })).Fields;
            Assert.Equal(((object?)1201L, (object?)0L, (object?)"kafe-analytics.sales.orders 2026"), (result["inserted"], result["skipped"], result["table"]));
            var batches = google.Requests.Skip(1).ToList();
            Assert.Equal(new[] { 500, 500, 201 }, batches.Select(request => JsonNode.Parse(request.Body!)!["rows"]!.AsArray().Count));
            Assert.All(batches, request => Assert.Equal(("POST", $"{Api}/projects/kafe-analytics/datasets/sales/tables/orders%202026/insertAll"), (request.Method.Method, request.Url)));
            var sent = JsonNode.Parse(batches[0].Body!)!;
            Assert.Equal((false, false, "Kafé 1", 1.5), (sent["skipInvalidRows"]!.GetValue<bool>(), sent["ignoreUnknownValues"]!.GetValue<bool>(), sent["rows"]![0]!["json"]!["name"]!.GetValue<string>(), sent["rows"]![0]!["json"]!["total"]!.GetValue<double>()));
            Assert.Contains("\"json\":{\"name\":\"Kafé 1\",\"total\":1.5}", batches[0].Body, StringComparison.Ordinal); // sent as written, its letters as they are
            var ids = batches.SelectMany(request => JsonNode.Parse(request.Body!)!["rows"]!.AsArray().Select(row => row!["insertId"]!.GetValue<string>())).ToList();
            Assert.Equal(1201, ids.Distinct().Count()); // an insert id each

            var one = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{}") };
            await Run(one, "insert_rows", new() { ["project"] = "kafe-analytics", ["dataset"] = "sales", ["table"] = "orders", ["rows"] = "{\"name\": \"Kafé AB\"}", ["skip_invalid"] = "true", ["ignore_unknown"] = "TRUE" });
            Assert.Equal((true, true, 1), (JsonNode.Parse(one.Requests[^1].Body!)!["skipInvalidRows"]!.GetValue<bool>(), JsonNode.Parse(one.Requests[^1].Body!)!["ignoreUnknownValues"]!.GetValue<bool>(), JsonNode.Parse(one.Requests[^1].Body!)!["rows"]!.AsArray().Count));

            const string Refused = "{\"insertErrors\":[{\"index\":1,\"errors\":[{\"reason\":\"invalid\",\"location\":\"total\",\"message\":\"Cannot convert value to floating point.\"}]},{\"index\":0,\"errors\":[{\"reason\":\"stopped\",\"message\":\"\"}]},{\"index\":2,\"errors\":[{\"reason\":\"stopped\"}]}]}";
            var batch = 0;
            var refusing = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, ++batch == 2 ? Refused : "{}") };
            Assert.Equal("BigQuery refused row 502 (total: Cannot convert value to floating point.) - none of rows 501 to 503 was written, 500 before them were.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "insert_rows", new() { ["project"] = "kafe-analytics", ["dataset"] = "sales", ["table"] = "orders", ["rows"] = "[" + string.Join(",", Enumerable.Range(1, 503).Select(i => "{\"total\": " + i + "}")) + "]" }))).Message);
            var skipping = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"insertErrors\":[{\"index\":1,\"errors\":[{\"reason\":\"invalid\",\"location\":\"total\",\"message\":\"Cannot convert value to floating point.\"}]}]}") };
            var skipped = Assert.Single(await Run(skipping, "insert_rows", new() { ["project"] = "kafe-analytics", ["dataset"] = "sales", ["table"] = "orders", ["rows"] = "[{\"total\": 1}, {\"total\": \"x\"}, {\"total\": 3}]", ["skip_invalid"] = "true" })).Fields;
            Assert.Equal(((object?)2L, (object?)1L), (skipped["inserted"], skipped["skipped"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_a_token_is_asked_for_and_googles_words_on_a_failure()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters, settings))).Message;
            Dictionary<string, string> Query(params (string Name, string Value)[] more) => new[] { ("project", "kafe-analytics"), ("query", "SELECT 1") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);
            Dictionary<string, string> Insert(params (string Name, string Value)[] more) => new[] { ("project", "kafe-analytics"), ("dataset", "sales"), ("table", "orders"), ("rows", "{\"name\": \"Kafé\"}") }.Concat(more).GroupBy(pair => pair.Item1).ToDictionary(group => group.Key, group => group.Last().Item2);

            Assert.Equal("Google BigQuery has no operation 'tables' - query or insert_rows.", await Refused("tables", new()));
            Assert.Equal("Give the project - its id (project), or the connection's.", await Refused("query", new() { ["query"] = "SELECT 1" }));
            Assert.Equal("'Kafe Analytics' is not a project's id - kafe-analytics (project).", await Refused("query", Query(("project", "Kafe Analytics"))));
            Assert.Equal("'kafe/../other' is not a project's id - kafe-analytics (project).", await Refused("query", new() { ["query"] = "SELECT 1" }, new() { ["project"] = "kafe/../other" }));
            Assert.Equal("Give the query (query).", await Refused("query", new() { ["project"] = "kafe-analytics" }));
            Assert.Equal("'sales.orders' is not a dataset's id - letters, digits and _ (dataset).", await Refused("query", Query(("dataset", "sales.orders"))));
            Assert.Equal("'europe north' is not a location - EU, US, europe-north1 (location).", await Refused("query", Query(("location", "europe north"))));
            Assert.Equal("'yes' is not true or false (legacy_sql).", await Refused("query", Query(("legacy_sql", "yes"))));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("query", Query(("max", "0"))));
            Assert.Equal("'601' is not a number from 1 to 600 (wait).", await Refused("query", Query(("wait", "601"))));
            Assert.Equal("'@day of week' is not a parameter's name - @ and letters, digits and _ (parameters).", await Refused("query", Query(("@day of week", "1"))));
            Assert.Equal("'@1day' is not a parameter's name - @ and letters, digits and _ (parameters).", await Refused("query", Query(("@1day", "1"))));
            Assert.Equal("Legacy SQL takes no named parameters - leave legacy_sql out, or the @ parameters.", await Refused("query", Query(("legacy_sql", "true"), ("@day", "x"))));
            Assert.Equal("Give the dataset - its id (dataset).", await Refused("insert_rows", Insert().Where(pair => pair.Key != "dataset").ToDictionary(pair => pair.Key, pair => pair.Value)));
            Assert.Equal("Give the table - its id (table).", await Refused("insert_rows", Insert().Where(pair => pair.Key != "table").ToDictionary(pair => pair.Key, pair => pair.Value)));
            Assert.Equal("'orders/../x' is not a table's id - letters, digits, _, - and spaces (table).", await Refused("insert_rows", Insert(("table", "orders/../x"))));
            Assert.Equal("Give the rows - a JSON object or a list of them (rows).", await Refused("insert_rows", Insert().Where(pair => pair.Key != "rows").ToDictionary(pair => pair.Key, pair => pair.Value)));
            const string NotRows = "The rows are not a JSON object or a list of them - {\"name\": \"Kafé AB\"} (rows).";
            Assert.Equal(NotRows, await Refused("insert_rows", Insert(("rows", "name = Kafé"))));
            Assert.Equal(NotRows, await Refused("insert_rows", Insert(("rows", "[{\"name\": \"Kafé\"}, 3]"))));
            Assert.Equal("Give from 1 to 10,000 rows (rows).", await Refused("insert_rows", Insert(("rows", "[]"))));
            Assert.Equal("Give from 1 to 10,000 rows (rows).", await Refused("insert_rows", Insert(("rows", "[" + string.Join(",", Enumerable.Repeat("{}", 10001)) + "]"))));
            Assert.Equal("'maybe' is not true or false (skip_invalid).", await Refused("insert_rows", Insert(("skip_invalid", "maybe"))));
            Assert.Empty(google.Requests); // nothing sent - not even the token asked for

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Google { Answer = (_, _, _) => (status, answer) }, "query", Query()))).Message;
            Assert.Equal("HTTP 400 INVALID_ARGUMENT: Syntax error: Unexpected keyword FROM at [1:8]", await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"Syntax error: Unexpected keyword FROM at [1:8]\",\"status\":\"INVALID_ARGUMENT\"}}"));
            Assert.Equal("HTTP 403 PERMISSION_DENIED", await Failed(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\"}}"));
            Assert.Equal("HTTP 400 INVALID_ARGUMENT: " + new string('e', 300 - "INVALID_ARGUMENT: ".Length), await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"" + new string('e', 400) + "\"}}")); // a long one cut
            Assert.Equal("Google BigQuery answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("BigQuery answered the query with no job.", await Failed(HttpStatusCode.OK, "{\"jobComplete\":true}"));
            Assert.Equal("BigQuery answered the query with nothing.", await Failed(HttpStatusCode.OK, ""));
        }

        [Fact]
        public async Task A_test_names_what_the_connection_can_see()
        {
            Task<ConnectionTestResult> Test(Google google, Dictionary<string, string>? settings = null) =>
                new GoogleBigQueryConnector(new OAuth2TokenProvider()).TestConnectionAsync(Contexts.Test(new StubEgress(google), "", settings, Credentials));
            var projects = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"projects\":[{\"id\":\"kafe-analytics\"}]}") };
            Assert.Equal((true, "Google BigQuery: signed in - kafe-analytics among the projects it can see."), ((await Test(projects)).Success, (await Test(projects)).Message));
            Assert.Equal($"{Api}/projects?maxResults=1", projects.Requests[1].Url);
            Assert.Equal("Google BigQuery: signed in - no project it can see yet.", (await Test(new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{}") })).Message);
            var datasets = new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"datasets\":[{\"datasetReference\":{\"datasetId\":\"sales\"}}]}") };
            Assert.Equal("Google BigQuery: signed in to kafe-analytics - sales among its datasets.", (await Test(datasets, new() { ["project"] = "kafe-analytics" })).Message);
            Assert.Equal($"{Api}/projects/kafe-analytics/datasets?maxResults=1", datasets.Requests[1].Url);
            Assert.Equal("Google BigQuery: signed in to kafe-analytics - no dataset yet.", (await Test(new Google { Answer = (_, _, _) => (HttpStatusCode.OK, "{}") }, new() { ["project"] = "kafe-analytics" })).Message);
            var refused = await Test(new Google { Answer = (_, _, _) => (HttpStatusCode.Forbidden, "{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"BigQuery API has not been used in project 1 before.\"}}") });
            Assert.Equal((false, "HTTP 403 PERMISSION_DENIED: BigQuery API has not been used in project 1 before."), (refused.Success, refused.Message));
            var connector = new GoogleBigQueryConnector();
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googlebigquery", 2), (connector.AuthType, connector.ConnectorType, connector.Operations.Count));
        }
    }
}
