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
using Stomme.Modules.Connectors.GoogleAnalytics;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Google Analytics (n8n's Google Analytics node, its GA4 reports) without a server: every property, range, date,
    /// name, filter, currency and number held to GA4's shape before a token is asked for; a report over n8n's presets - the
    /// relative ones as GA4's own words, the calendar ones computed from the clock - or a start and an end, by its metrics and
    /// dimensions, ordered and filtered, each row a record of dimension values and metric numbers (whole for an integer metric),
    /// page by page by offset up to the most; the property from the step, the connection, or a GA4 address; the dimensions and
    /// metrics a property has; Google's status and message on a failure; a test over the metadata every property has.
    /// </summary>
    public sealed class GoogleAnalyticsConnectorTests
    {
        private const string Api = "https://analyticsdata.googleapis.com/v1beta";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret", ["refresh-token"] = "kafe-refresh" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class At(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed class Google : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == GoogleAnalyticsConnector.DefaultTokenUrl ? (HttpStatusCode.OK, "{\"access_token\":\"analytics-token\",\"expires_in\":3600}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static List<Sent> Calls(Google google) => google.Requests.Where(sent => sent.Url != GoogleAnalyticsConnector.DefaultTokenUrl).ToList();

        // A Thursday, 15 October 2026, late in the evening UTC.
        private static readonly DateTimeOffset Now = new(2026, 10, 15, 22, 30, 0, TimeSpan.Zero);

        private static Task<List<ConnectorRecord>> Run(Google google, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "") =>
            Contexts.DrainAsync(new GoogleAnalyticsConnector(new OAuth2TokenProvider(), new At(Now)), Contexts.Extract(new StubEgress(google), endpoint, operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        private static string Report(string[] dimensions, (string Name, string Type)[] metrics, IEnumerable<(string[] Dimensions, string[] Metrics)> rows, int rowCount) => new JsonObject
        {
            ["dimensionHeaders"] = new JsonArray(dimensions.Select(name => (JsonNode?)new JsonObject { ["name"] = name }).ToArray()),
            ["metricHeaders"] = new JsonArray(metrics.Select(metric => (JsonNode?)new JsonObject { ["name"] = metric.Name, ["type"] = metric.Type }).ToArray()),
            ["rows"] = new JsonArray(rows.Select(row => (JsonNode?)new JsonObject
            {
                ["dimensionValues"] = new JsonArray(row.Dimensions.Select(value => (JsonNode?)new JsonObject { ["value"] = value }).ToArray()),
                ["metricValues"] = new JsonArray(row.Metrics.Select(value => (JsonNode?)new JsonObject { ["value"] = value }).ToArray()),
            }).ToArray()),
            ["rowCount"] = rowCount,
            ["kind"] = "analyticsData#runReport",
        }.ToJsonString();

        [Fact]
        public void It_declares_itself_as_n8ns_google_analytics_node()
        {
            IConnector connector = new GoogleAnalyticsConnector();
            Assert.Equal(("googleanalytics", "Google Analytics", AuthenticationType.OAuth2RefreshToken), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "report", "metadata" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_a_token_is_asked_for()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var google = new Google();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(google, operation, parameters));
                Assert.Empty(google.Requests); // refused before a token was asked for
                return refusal.Message;
            }

            Dictionary<string, string> With(string name, string value) => new() { ["property"] = "213025502", [name] = value };
            Assert.Equal("Google Analytics has no operation 'realtime' - report or metadata.", await Refused("realtime", new()));
            Assert.Equal("Give the GA4 property's id - 213025502 (property) - or set the connection's.", await Refused("report", new()));
            Assert.Equal("'UA-1234-1' is not a GA4 property's id - 213025502 (property).", await Refused("report", new() { ["property"] = "UA-1234-1" }));
            Assert.Equal("'yesterweek' is not a date range - last7days, last30days, today, yesterday, lastCalendarWeek, lastCalendarMonth, lastCalendarQuarter, lastCalendarYear, last365days, custom (range).", await Refused("report", With("range", "yesterweek")));
            Assert.Equal("Give the range's start - 2026-10-01, yesterday or 28daysAgo (start).", await Refused("report", With("range", "custom")));
            Assert.Equal("'2026-02-30' is not a date GA4 reads - 2026-10-01, today, yesterday or 28daysAgo (start).", await Refused("report", With("start", "2026-02-30")));
            Assert.Equal("'tomorrow' is not a date GA4 reads - 2026-10-01, today, yesterday or 28daysAgo (end).", await Refused("report", new() { ["property"] = "1", ["start"] = "yesterday", ["end"] = "tomorrow" }));
            Assert.Equal("'active users' is not a metric's API name - activeUsers, customEvent:plan (metrics).", await Refused("report", With("metrics", "sessions, active users")));
            Assert.Equal("GA4 takes at most 10 metrics in one report - 11 given (metrics).", await Refused("report", With("metrics", string.Join(",", Enumerable.Range(0, 11).Select(i => "metric" + i)))));
            Assert.Equal("GA4 takes at most 9 dimensions in one report - 10 given (dimensions).", await Refused("report", With("dimensions", string.Join(",", Enumerable.Range(0, 10).Select(i => "dimension" + i)))));
            Assert.Equal("'country' is none of the report's metrics and dimensions - GA4 orders by those alone (order_by).", await Refused("report", With("order_by", "-totalUsers, country")));
            Assert.Equal("The dimension filter is GA4's filter expression as a JSON object - {\"filter\": {\"fieldName\": \"country\", \"stringFilter\": {\"value\": \"Sweden\"}}} (dimension_filter).", await Refused("report", With("dimension_filter", "country = Sweden")));
            Assert.Equal("The metric filter is GA4's filter expression as a JSON object - {\"filter\": {\"fieldName\": \"country\", \"stringFilter\": {\"value\": \"Sweden\"}}} (metric_filter).", await Refused("report", With("metric_filter", "{}")));
            Assert.Equal("'sometimes' is not true or false (keep_empty_rows).", await Refused("report", With("keep_empty_rows", "sometimes")));
            Assert.Equal("'kronor' is not a currency's code - SEK, EUR (currency).", await Refused("report", With("currency", "kronor")));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("report", With("max", "0")));
            Assert.Equal("'everything' is not dimensions, metrics or both (kind).", await Refused("metadata", With("kind", "everything")));
        }

        [Fact]
        public async Task A_report_is_each_row_as_its_dimensions_and_metrics_over_n8ns_ranges()
        {
            var google = new Google
            {
                Answer = sent => (HttpStatusCode.OK, Report(new[] { "date", "country" }, new[] { ("activeUsers", "TYPE_INTEGER"), ("engagementRate", "TYPE_FLOAT"), ("purchaseRevenue", "TYPE_CURRENCY") },
                    new[] { (new[] { "20261014", "Sweden" }, new[] { "1200", "0.61", "4321.5" }), (new[] { "20261014", "Norway" }, new[] { "-3", "0.4", "abc" }) }, 2)),
            };
            var rows = await Run(google, "report", new() { ["property"] = "properties/213025502", ["metrics"] = "activeUsers, engagementRate, purchaseRevenue", ["dimensions"] = "date,country", ["order_by"] = "-activeUsers, country", ["dimension_filter"] = "{\"filter\": {\"fieldName\": \"country\", \"inListFilter\": {\"values\": [\"Sweden\", \"Norway\"]}}}", ["keep_empty_rows"] = "true", ["currency"] = "sek" });
            Assert.Equal(new Dictionary<string, object?> { ["date"] = "20261014", ["country"] = "Sweden", ["activeUsers"] = 1200L, ["engagementRate"] = 0.61, ["purchaseRevenue"] = 4321.5 }, rows[0].Fields);
            Assert.Equal(new Dictionary<string, object?> { ["date"] = "20261014", ["country"] = "Norway", ["activeUsers"] = -3L, ["engagementRate"] = 0.4, ["purchaseRevenue"] = "abc" }, rows[1].Fields); // what is no number stays as GA4 said it
            var sent = Assert.Single(Calls(google));
            Assert.Equal(("POST", Api + "/properties/213025502:runReport", "Bearer analytics-token"), (sent.Method, sent.Url, sent.Authorization));
            var body = JsonNode.Parse(sent.Body!)!;
            Assert.Equal("[{\"startDate\":\"7daysAgo\",\"endDate\":\"today\"}]", body["dateRanges"]!.ToJsonString()); // n8n's default, in GA4's own words
            Assert.Equal("[{\"name\":\"date\"},{\"name\":\"country\"}]", body["dimensions"]!.ToJsonString());
            Assert.Equal("[{\"name\":\"activeUsers\"},{\"name\":\"engagementRate\"},{\"name\":\"purchaseRevenue\"}]", body["metrics"]!.ToJsonString());
            Assert.Equal("[{\"metric\":{\"metricName\":\"activeUsers\"},\"desc\":true},{\"dimension\":{\"dimensionName\":\"country\"},\"desc\":false}]", body["orderBys"]!.ToJsonString());
            Assert.Equal("{\"filter\":{\"fieldName\":\"country\",\"inListFilter\":{\"values\":[\"Sweden\",\"Norway\"]}}}", body["dimensionFilter"]!.ToJsonString());
            Assert.Equal((true, "SEK", 0, 1000), (body["keepEmptyRows"]!.GetValue<bool>(), body["currencyCode"]!.GetValue<string>(), body["offset"]!.GetValue<int>(), body["limit"]!.GetValue<int>()));

            async Task<string> Dates(Dictionary<string, string> parameters)
            {
                google.Requests.Clear();
                await Run(google, "report", parameters);
                return JsonNode.Parse(Calls(google).Single().Body!)!["dateRanges"]!.ToJsonString();
            }

            // n8n's presets: the relative ones GA4 reads in the property's time zone, the calendar ones from the clock (UTC).
            Assert.Equal("[{\"startDate\":\"30daysAgo\",\"endDate\":\"today\"}]", await Dates(new() { ["property"] = "1", ["range"] = "last30days" }));
            Assert.Equal("[{\"startDate\":\"365daysAgo\",\"endDate\":\"today\"}]", await Dates(new() { ["property"] = "1", ["range"] = "LAST365DAYS" }));
            Assert.Equal("[{\"startDate\":\"yesterday\",\"endDate\":\"yesterday\"}]", await Dates(new() { ["property"] = "1", ["range"] = "yesterday" }));
            Assert.Equal("[{\"startDate\":\"today\",\"endDate\":\"today\"}]", await Dates(new() { ["property"] = "1", ["range"] = "Today" }));
            Assert.Equal("[{\"startDate\":\"2026-10-05\",\"endDate\":\"2026-10-11\"}]", await Dates(new() { ["property"] = "1", ["range"] = "lastCalendarWeek" })); // Monday to Sunday
            Assert.Equal("[{\"startDate\":\"2026-09-01\",\"endDate\":\"2026-09-30\"}]", await Dates(new() { ["property"] = "1", ["range"] = "lastCalendarMonth" }));
            Assert.Equal("[{\"startDate\":\"2026-07-01\",\"endDate\":\"2026-09-30\"}]", await Dates(new() { ["property"] = "1", ["range"] = "lastCalendarQuarter" }));
            Assert.Equal("[{\"startDate\":\"2025-01-01\",\"endDate\":\"2025-12-31\"}]", await Dates(new() { ["property"] = "1", ["range"] = "lastCalendarYear" }));
            var november = new Google { Answer = google.Answer }; // mid-quarter: the quarter before it, not the three months before
            await Contexts.DrainAsync(new GoogleAnalyticsConnector(new OAuth2TokenProvider(), new At(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero))), Contexts.Extract(new StubEgress(november), "", "report", new Dictionary<string, string> { ["property"] = "1", ["range"] = "lastCalendarQuarter" }, new Dictionary<string, string>(), Credentials));
            Assert.Equal("[{\"startDate\":\"2026-07-01\",\"endDate\":\"2026-09-30\"}]", JsonNode.Parse(Calls(november).Single().Body!)!["dateRanges"]!.ToJsonString());
            Assert.Equal("[{\"startDate\":\"2026-10-01\",\"endDate\":\"today\"}]", await Dates(new() { ["property"] = "1", ["start"] = "2026-10-01" })); // a start makes it custom
            Assert.Equal("[{\"startDate\":\"28daysAgo\",\"endDate\":\"yesterday\"}]", await Dates(new() { ["property"] = "1", ["range"] = "custom", ["start"] = "28daysAgo", ["end"] = "yesterday" }));
            Assert.Equal("[{\"startDate\":\"365daysAgo\",\"endDate\":\"2026-10-14\"}]", await Dates(new() { ["property"] = "1", ["start"] = "365daysAgo", ["end"] = "2026-10-14" }));

            // n8n's defaults - totalUsers by date - and none of the options unasked for; no dimension at all with -.
            google.Requests.Clear();
            await Run(google, "report", new() { ["property"] = "1" });
            var plain = JsonNode.Parse(Calls(google).Single().Body!)!.AsObject();
            Assert.Equal(("[{\"name\":\"date\"}]", "[{\"name\":\"totalUsers\"}]"), (plain["dimensions"]!.ToJsonString(), plain["metrics"]!.ToJsonString()));
            Assert.False(plain.ContainsKey("orderBys") || plain.ContainsKey("dimensionFilter") || plain.ContainsKey("metricFilter") || plain.ContainsKey("keepEmptyRows") || plain.ContainsKey("currencyCode"));
            google.Requests.Clear();
            await Run(google, "report", new() { ["property"] = "1", ["dimensions"] = "-", ["metric_filter"] = "{\"filter\": {\"fieldName\": \"sessions\", \"numericFilter\": {\"operation\": \"GREATER_THAN\", \"value\": {\"int64Value\": \"10\"}}}}" });
            var none = JsonNode.Parse(Calls(google).Single().Body!)!;
            Assert.Equal(("[]", "GREATER_THAN"), (none["dimensions"]!.ToJsonString(), none["metricFilter"]!["filter"]!["numericFilter"]!["operation"]!.GetValue<string>()));
        }

        [Fact]
        public async Task A_report_is_read_page_by_page_up_to_the_most_and_its_property_found_where_it_is()
        {
            var google = new Google
            {
                Answer = sent =>
                {
                    var request = JsonNode.Parse(sent.Body!)!;
                    var offset = request["offset"]!.GetValue<int>();
                    var limit = request["limit"]!.GetValue<int>();
                    var count = Math.Max(0, Math.Min(limit, 25_000 - offset));
                    return (HttpStatusCode.OK, Report(new[] { "date" }, new[] { ("sessions", "TYPE_INTEGER") }, Enumerable.Range(offset, count).Select(i => (new[] { "d" + i }, new[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture) })), 25_000));
                },
            };
            var all = await Run(google, "report", new() { ["max"] = "100000" }, new() { ["property"] = "https://analytics.google.com/analytics/web/#/p213025502/reports/intelligenthome" });
            Assert.Equal(25_000, all.Count);
            Assert.Equal((24_999L, "d24999"), ((long)all[^1].Fields["sessions"]!, (string?)all[^1].Fields["date"]));
            Assert.Equal(new[] { "0/10000", "10000/10000", "20000/10000" }, Calls(google).Select(sent => JsonNode.Parse(sent.Body!)!["offset"] + "/" + JsonNode.Parse(sent.Body!)!["limit"]));
            Assert.All(Calls(google), sent => Assert.Equal(Api + "/properties/213025502:runReport", sent.Url)); // the connection's, from a GA4 address

            google.Requests.Clear();
            Assert.Equal(15, (await Run(google, "report", new() { ["property"] = "7", ["max"] = "15" }, new() { ["property"] = "213025502" })).Count); // the step's wins
            Assert.Equal((Api + "/properties/7:runReport", 15), (Calls(google).Single().Url, JsonNode.Parse(Calls(google).Single().Body!)!["limit"]!.GetValue<int>())); // a page no larger than what is wanted

            var empty = new Google { Answer = _ => (HttpStatusCode.OK, "{\"dimensionHeaders\":[{\"name\":\"date\"}],\"metricHeaders\":[{\"name\":\"sessions\",\"type\":\"TYPE_INTEGER\"}],\"kind\":\"analyticsData#runReport\"}") };
            Assert.Empty(await Run(empty, "report", new() { ["property"] = "1" }));
            Assert.Single(Calls(empty));
        }

        [Fact]
        public async Task A_propertys_dimensions_and_metrics_are_listed_and_a_failure_is_googles_words()
        {
            const string Metadata = "{\"name\":\"properties/1/metadata\",\"dimensions\":[{\"apiName\":\"date\",\"uiName\":\"Date\",\"description\":\"The date.\",\"category\":\"Time\"},{\"apiName\":\"customEvent:plan\",\"uiName\":\"Plan\",\"customDefinition\":true,\"category\":\"Custom\"}],\"metrics\":[{\"apiName\":\"sessions\",\"uiName\":\"Sessions\",\"type\":\"TYPE_INTEGER\",\"category\":\"Session\"}]}";
            var google = new Google { Answer = _ => (HttpStatusCode.OK, Metadata) };
            var all = await Run(google, "metadata", new() { ["property"] = "213025502" });
            Assert.Equal(new[] { "dimension:date", "dimension:customEvent:plan", "metric:sessions" }, all.Select(found => found.Fields["kind"] + ":" + found.Fields["apiName"]));
            Assert.Equal((true, "TYPE_INTEGER"), ((bool)all[1].Fields["customDefinition"]!, (string?)all[2].Fields["type"]));
            Assert.Equal(("GET", Api + "/properties/213025502/metadata"), (Calls(google)[0].Method, Calls(google)[0].Url));
            Assert.Equal(new[] { "sessions" }, (await Run(google, "metadata", new() { ["property"] = "1", ["kind"] = "Metrics" })).Select(found => found.Fields["apiName"]));
            Assert.Equal(2, (await Run(google, "metadata", new() { ["property"] = "1", ["kind"] = "dimensions" })).Count);

            var refusing = new Google { Answer = _ => (HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"User does not have sufficient permissions for this property.\",\"status\":\"PERMISSION_DENIED\"}}") };
            Assert.Equal("Google Analytics answered HTTP 403 PERMISSION_DENIED: User does not have sufficient permissions for this property. - check the refresh token, its analytics.readonly scope and the property's access.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "report", new() { ["property"] = "1" }))).Message);
            var odd = new Google { Answer = _ => (HttpStatusCode.OK, "[]") };
            Assert.Equal("Google Analytics answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(odd, "metadata", new() { ["property"] = "1" }))).Message);
            var bad = new Google { Answer = _ => (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"Field kafe is not a valid metric.\",\"status\":\"INVALID_ARGUMENT\"}}") };
            Assert.Equal("Google Analytics answered HTTP 400 INVALID_ARGUMENT: Field kafe is not a valid metric.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(bad, "report", new() { ["property"] = "1", ["metrics"] = "kafe" }))).Message);

            // A test reads what every property has - property 0 - or the connection's; Google's own address with its path is taken.
            google.Requests.Clear();
            var tested = await new GoogleAnalyticsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Google Analytics answers - 2 dimensions and 1 metrics every property has."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/properties/0/metadata", Calls(google).Single().Url);
            google.Requests.Clear();
            tested = await new GoogleAnalyticsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(google), "", new Dictionary<string, string> { ["property"] = "213025502" }, Credentials));
            Assert.Equal((true, "Google Analytics answers - 2 dimensions and 1 metrics in property 213025502."), (tested.Success, tested.Message));
            var nothing = await new GoogleAnalyticsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google()), "", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "Google Analytics answered with no dimensions and no metrics - check the address."), (nothing.Success, nothing.Message));
            var elsewhere = await new GoogleAnalyticsConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google()), "https://analyticsdata.googleapis.com/v1alpha", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "'https://analyticsdata.googleapis.com/v1alpha' is not the Google Analytics Data API's address - its own, https://analyticsdata.googleapis.com."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
