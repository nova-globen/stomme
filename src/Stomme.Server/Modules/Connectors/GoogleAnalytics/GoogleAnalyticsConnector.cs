#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.GoogleAnalytics
{
    /// <summary>
    /// Google Analytics (BL-229, n8n's Google Analytics node) over the GA4 Data API
    /// (<c>https://analyticsdata.googleapis.com/v1beta</c>) as a person signed in - an OAuth client's refresh token granted the
    /// analytics.readonly scope, exchanged at Google's token endpoint through the shared <see cref="OAuth2TokenProvider"/>, as
    /// Gmail's is. A test lists the dimensions and metrics every property has.
    /// </summary>
    /// <remarks>
    /// <c>report</c> (a property's report over a date range - n8n's presets, or a start and an end - by its metrics and
    /// dimensions, filtered by GA4's own filter expressions, ordered, page by page up to <c>max</c>: each row a record of its
    /// dimensions' values and its metrics' numbers - n8n's Report Get, simplified as its node does by default); <c>metadata</c>
    /// (the dimensions and metrics a property has, its custom ones among them). Every property, date, name, currency and filter
    /// held to GA4's shape before a token is asked for; Google's status and message on a failure, a 401 or 403 pointing at the
    /// token's scopes. Universal Analytics, which Google switched off in 2024, is not here.
    /// </remarks>
    public sealed class GoogleAnalyticsConnector(OAuth2TokenProvider? tokenProvider = null, TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://analyticsdata.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const int DefaultMax = 1000;

        private const int PageSize = 10_000;

        private const int MostMetrics = 10; // GA4's most in one report

        private const int MostDimensions = 9;

        private static readonly string[] Known = { "report", "metadata" };

        // n8n's date range presets, by its own names - and a quarter, a year and 365 days besides.
        private static readonly string[] Ranges = { "last7days", "last30days", "today", "yesterday", "lastCalendarWeek", "lastCalendarMonth", "lastCalendarQuarter", "lastCalendarYear", "last365days", "custom" };

        private static readonly Regex Date = new("^([0-9]{4}-[0-9]{2}-[0-9]{2}|today|yesterday|[0-9]{1,4}daysAgo)$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^[a-zA-Z][a-zA-Z0-9_]*(:[a-zA-Z0-9_]+)?$", RegexOptions.CultureInvariant);

        private static readonly Regex Property = new("^(?:properties/)?([0-9]{1,20})$|/p([0-9]{1,20})(?:/|$)", RegexOptions.CultureInvariant);

        private static readonly Regex Currency = new("^[A-Z]{3}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "googleanalytics";

        public string DisplayName => "Google Analytics";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted analytics.readonly)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://marketingplatform.google.com/about/analytics/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/analytics/devguides/reporting/data/v1/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("property", "Property", ConnectorConfigFieldType.Text, Required: false, Description: "The GA4 property a step names none of - its id, 213025502 (Admin > Property details)."),
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField PropertyField = Field("property", "Property", "Its id - 213025502. Empty: the connection's.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("report", "Get a report", "A property's report - n8n's Report Get.")
            {
                Parameters = new(new[]
                {
                    PropertyField,
                    Field("range", "Date range", "last7days (the default), last30days, today, yesterday, lastCalendarWeek, lastCalendarMonth, lastCalendarQuarter, lastCalendarYear, last365days - or custom, with a start and an end. The calendar ones in UTC."),
                    Field("start", "From", "2026-10-01, yesterday or 28daysAgo - a start makes the range custom."),
                    Field("end", "To", "2026-10-17 or today (the default)."),
                    Field("metrics", "Metrics", "Their API names, by commas - activeUsers, sessions. Empty: totalUsers. At most 10."),
                    Field("dimensions", "Dimensions", "Their API names, by commas - date, country. Empty: date. At most 9; none: give -."),
                    Field("order_by", "Order by", "Among the metrics and dimensions, by commas, - first for descending - -sessions, date."),
                    Field("dimension_filter", "Dimension filter", "GA4's filter expression as JSON - {\"filter\": {\"fieldName\": \"country\", \"stringFilter\": {\"value\": \"Sweden\"}}}."),
                    Field("metric_filter", "Metric filter", "GA4's filter expression as JSON, over the metrics."),
                    Field("keep_empty_rows", "Keep empty rows", "true: rows whose metrics are all 0 too."),
                    Field("currency", "Currency", "Revenue in this one - SEK. Empty: the property's."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many rows, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("metadata", "Get dimensions and metrics", "The dimensions and metrics a property has.")
            {
                Parameters = new(new[] { PropertyField, Field("kind", "Which", "dimensions, metrics or both (the default).") }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                // The connection's property's, or - property 0 - the ones every property has.
                var property = Values.Get(context.Settings, "property") is { } given ? PropertyId(given, "property") : "0";
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var answer = await session.JsonAsync(HttpMethod.Get, $"/properties/{property}/metadata", null, cancellationToken);
                var dimensions = (answer?["dimensions"] as JsonArray)?.Count ?? 0;
                var metrics = (answer?["metrics"] as JsonArray)?.Count ?? 0;
                return dimensions + metrics == 0
                    ? ConnectionTestResult.Fail("Google Analytics answered with no dimensions and no metrics - check the address.")
                    : ConnectionTestResult.Ok($"Google Analytics answers - {dimensions} dimensions and {metrics} metrics {(property == "0" ? "every property has" : "in property " + property)}.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "report").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Analytics has no operation '{context.OperationName}' - {string.Join(" or ", Known)}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var property = Values.Get(p, "property") is { } named ? PropertyId(named, "property")
                : Values.Get(context.Settings, "property") is { } fallback ? PropertyId(fallback, "the connection's property")
                : throw new InvalidOperationException("Give the GA4 property's id - 213025502 (property) - or set the connection's.");
            if (operation == "metadata")
            {
                var kind = (Values.Get(p, "kind") ?? "both").ToLowerInvariant() is ("dimensions" or "metrics" or "both") and var which ? which : throw new InvalidOperationException($"'{Values.Get(p, "kind")}' is not dimensions, metrics or both (kind).");
                using var metaSession = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var answer = await metaSession.JsonAsync(HttpMethod.Get, $"/properties/{property}/metadata", null, cancellationToken);
                foreach (var (list, single) in new[] { ("dimensions", "dimension"), ("metrics", "metric") }.Where(entry => kind == "both" || kind == entry.Item1))
                {
                    foreach (var item in (answer?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = single };
                        foreach (var (key, value) in item)
                        {
                            fields[key] = value is JsonValue plain ? plain.TryGetValue<string>(out var text) ? text : plain.TryGetValue<bool>(out var flag) ? flag : plain.ToJsonString() : value?.ToJsonString();
                        }

                        yield return new ConnectorRecord(fields);
                    }
                }

                yield break;
            }

            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var (start, end) = Range(p);
            var metrics = Names(p, "metrics", "totalUsers", MostMetrics, "metric");
            var dimensions = Values.Get(p, "dimensions") == "-" ? new List<string>() : Names(p, "dimensions", "date", MostDimensions, "dimension");
            var body = new JsonObject
            {
                ["dateRanges"] = new JsonArray(new JsonObject { ["startDate"] = start, ["endDate"] = end }),
                ["dimensions"] = new JsonArray(dimensions.Select(name => (JsonNode?)new JsonObject { ["name"] = name }).ToArray()),
                ["metrics"] = new JsonArray(metrics.Select(name => (JsonNode?)new JsonObject { ["name"] = name }).ToArray()),
            };
            if (Values.Get(p, "order_by") is { } orderBy)
            {
                body["orderBys"] = new JsonArray(orderBy.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(each =>
                {
                    var desc = each.StartsWith('-');
                    var by = desc ? each[1..] : each;
                    return (JsonNode?)(metrics.Contains(by) ? new JsonObject { ["metric"] = new JsonObject { ["metricName"] = by }, ["desc"] = desc }
                        : dimensions.Contains(by) ? new JsonObject { ["dimension"] = new JsonObject { ["dimensionName"] = by }, ["desc"] = desc }
                        : throw new InvalidOperationException($"'{by}' is none of the report's metrics and dimensions - GA4 orders by those alone (order_by)."));
                }).ToArray());
            }

            foreach (var (parameter, key) in new[] { ("dimension_filter", "dimensionFilter"), ("metric_filter", "metricFilter") })
            {
                if (Values.Get(p, parameter) is { } filter)
                {
                    body[key] = Expression(filter, parameter);
                }
            }

            if (Values.Get(p, "keep_empty_rows") is { } keep)
            {
                body["keepEmptyRows"] = bool.TryParse(keep, out var keepAll) ? keepAll : throw new InvalidOperationException($"'{keep}' is not true or false (keep_empty_rows).");
            }

            if (Values.Get(p, "currency") is { } currency)
            {
                body["currencyCode"] = Currency.IsMatch(currency.ToUpperInvariant()) ? currency.ToUpperInvariant() : throw new InvalidOperationException($"'{currency}' is not a currency's code - SEK, EUR (currency).");
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            var read = 0;
            while (true)
            {
                body["offset"] = read;
                body["limit"] = Math.Min(PageSize, max - read);
                var answer = await session.JsonAsync(HttpMethod.Post, $"/properties/{property}:runReport", body, cancellationToken);
                var dimensionNames = (answer?["dimensionHeaders"] as JsonArray ?? new JsonArray()).Select(header => Text(header?["name"]) ?? string.Empty).ToList();
                var metricHeaders = (answer?["metricHeaders"] as JsonArray ?? new JsonArray()).Select(header => (Name: Text(header?["name"]) ?? string.Empty, Type: Text(header?["type"]))).ToList();
                var rows = (answer?["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var row in rows)
                {
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var values = (row["dimensionValues"] as JsonArray ?? new JsonArray()).ToList();
                    for (var i = 0; i < dimensionNames.Count; i++)
                    {
                        fields[dimensionNames[i]] = i < values.Count ? Text(values[i]?["value"]) : null;
                    }

                    var numbers = (row["metricValues"] as JsonArray ?? new JsonArray()).ToList();
                    for (var i = 0; i < metricHeaders.Count; i++)
                    {
                        fields[metricHeaders[i].Name] = Number(i < numbers.Count ? Text(numbers[i]?["value"]) : null, metricHeaders[i].Type);
                    }

                    yield return new ConnectorRecord(fields);
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                var total = answer?["rowCount"] is JsonValue count && count.TryGetValue<int>(out var all) ? all : 0;
                if (rows.Count == 0 || read >= total)
                {
                    yield break;
                }
            }
        }

        // The range's start and end: given ones (a start makes it custom), or n8n's preset - its calendar ones in UTC.
        private (string Start, string End) Range(IReadOnlyDictionary<string, string> p)
        {
            var given = Values.Get(p, "range");
            var range = given is null ? (Values.Get(p, "start") is null ? "last7days" : "custom") : Ranges.FirstOrDefault(known => string.Equals(known, given, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not a date range - {string.Join(", ", Ranges)} (range).");
            if (range == "custom")
            {
                var start = Values.Get(p, "start") ?? throw new InvalidOperationException("Give the range's start - 2026-10-01, yesterday or 28daysAgo (start).");
                var end = Values.Get(p, "end") ?? "today";
                foreach (var (date, name) in new[] { (start, "start"), (end, "end") })
                {
                    if (!Date.IsMatch(date) || (date.Contains('-', StringComparison.Ordinal) && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                    {
                        throw new InvalidOperationException($"'{date}' is not a date GA4 reads - 2026-10-01, today, yesterday or 28daysAgo ({name}).");
                    }
                }

                return (start, end);
            }

            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            switch (range)
            {
                case "last7days":
                    return ("7daysAgo", "today");
                case "last30days":
                    return ("30daysAgo", "today");
                case "today" or "yesterday":
                    return (range, range);
                case "last365days":
                    return ("365daysAgo", "today");
                case "lastCalendarWeek":
                    {
                        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).AddDays(-7);
                        return (Iso(monday), Iso(monday.AddDays(6)));
                    }

                case "lastCalendarMonth":
                    {
                        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                        return (Iso(first), Iso(first.AddMonths(1).AddDays(-1)));
                    }

                case "lastCalendarQuarter":
                    {
                        var first = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1).AddMonths(-3);
                        return (Iso(first), Iso(first.AddMonths(3).AddDays(-1)));
                    }

                default: // lastCalendarYear
                    return (Iso(new DateOnly(today.Year - 1, 1, 1)), Iso(new DateOnly(today.Year - 1, 12, 31)));
            }
        }

        private static List<string> Names(IReadOnlyDictionary<string, string> p, string parameter, string fallback, int most, string what)
        {
            var names = (Values.Get(p, parameter) ?? fallback).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
            foreach (var name in names)
            {
                if (!Name.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a {what}'s API name - activeUsers, customEvent:plan ({parameter}).");
                }
            }

            return names.Count <= most ? names : throw new InvalidOperationException($"GA4 takes at most {most} {parameter} in one report - {names.Count} given ({parameter}).");
        }

        private static string PropertyId(string given, string name) =>
            Property.Match(given.Trim()) is { Success: true } match ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                : throw new InvalidOperationException($"'{given}' is not a GA4 property's id - 213025502 ({name}).");

        private static JsonObject Expression(string given, string name)
        {
            try
            {
                return JsonNode.Parse(given) as JsonObject is { Count: > 0 } expression ? expression : throw new JsonException();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name.Replace('_', ' ')} is GA4's filter expression as a JSON object - {{\"filter\": {{\"fieldName\": \"country\", \"stringFilter\": {{\"value\": \"Sweden\"}}}}}} ({name}).");
            }
        }

        // A metric's value as the number its type is: a whole one for TYPE_INTEGER, a decimal one otherwise.
        private static object? Number(string? value, string? type) =>
            value is null ? null
                : type == "TYPE_INTEGER" && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
                : value;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;

            private Session(HttpClient client, string token, string api)
            {
                this.client = client;
                this.token = token;
                this.api = api;
            }

            public static async Task<Session> OpenAsync(GoogleAnalyticsConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/v1beta", StringComparison.Ordinal))
                {
                    root = root[..^"/v1beta".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not the Google Analytics Data API's address - its own, {DefaultEndpoint}.");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, root + "/v1beta");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's JSON, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Google Analytics answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is JsonObject ? json : throw new InvalidOperationException("Google Analytics answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var error = json?["error"];
                var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                throw new InvalidOperationException($"Google Analytics answered HTTP {status}{(Text(error?["status"]) is { } kind ? " " + kind : string.Empty)}{(message is null ? "." : ": " + message)}"
                    + (status is 401 or 403 ? " - check the refresh token, its analytics.readonly scope and the property's access." : string.Empty));
            }
        }
    }
}
