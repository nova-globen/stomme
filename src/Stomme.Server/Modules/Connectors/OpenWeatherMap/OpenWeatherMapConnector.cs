#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.OpenWeatherMap
{
    /// <summary>
    /// OpenWeatherMap (BL-229, n8n's OpenWeatherMap node: a place's weather now and its five-day forecast) over its API 2.5
    /// (<c>https://api.openweathermap.org/data/2.5</c>) by an API key - in the address, as OpenWeatherMap takes it alone. A test
    /// reads the weather in London.
    /// </summary>
    /// <remarks>
    /// <c>current</c> (n8n's Current Weather: one record, OpenWeatherMap's answer with its temperature, feeling, humidity,
    /// pressure, description and wind beside it) and <c>forecast</c> (n8n's 5 Day Forecast: each three hours a record, the same
    /// beside each, its city and country - up to <c>max</c>, OpenWeatherMap's 40 at most). A place by its name (berlin,de), its
    /// city id, its coordinates or its postal code - one of them; metric (n8n's default), imperial or standard units; a language.
    /// Everything held to its shape before anything is sent; OpenWeatherMap's code and message on a failure.
    /// </remarks>
    public sealed class OpenWeatherMapConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.openweathermap.org";

        private const int MostEntries = 40; // five days in three-hour steps

        private static readonly string[] Known = { "current", "forecast" };

        private static readonly Regex Language = new("^[a-zA-Z]{2}(_[a-zA-Z]{2})?$", RegexOptions.CultureInvariant);

        private static readonly Regex Postal = new("^[A-Za-z0-9 -]{2,12}(,[A-Za-z]{2})?$", RegexOptions.CultureInvariant);

        public string ConnectorType => "openweathermap";

        public string DisplayName => "OpenWeatherMap";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://openweathermap.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://openweathermap.org/current");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description) => new(name, label, ConnectorConfigFieldType.Text, Required: false, Description: description);

        private static readonly ConnectorConfigField[] Place =
        {
            Field("city", "City", "Its name, and its country if two share it - berlin,de."),
            Field("city_id", "City id", "OpenWeatherMap's id for it - 2950159."),
            Field("latitude", "Latitude", "52.52 - with a longitude."),
            Field("longitude", "Longitude", "13.39 - with a latitude."),
            Field("zip", "Postal code", "Its postal code and country - 10115,de."),
            Field("units", "Units", "metric (the default: °C, m/s), imperial (°F, mph) or standard (K, m/s)."),
            Field("language", "Language", "The descriptions' language - sv, en."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("current", "Get the weather now", "n8n's Current Weather.") { Parameters = new(Place) },
            new ConnectorOperation("forecast", "Get the forecast", "n8n's 5 Day Forecast - each three hours a record.")
            {
                Parameters = new(Place.Append(new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many three-hour steps, at most - 40, five days.") { Default = MostEntries.ToString(CultureInfo.InvariantCulture) }).ToArray()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.GetAsync("/weather", new List<(string, string)> { ("q", "London,GB"), ("units", "metric") }, cancellationToken);
                var temperature = answer["main"]?["temp"] is JsonValue value && value.TryGetValue<double>(out var degrees) ? degrees.ToString("0.#", CultureInfo.InvariantCulture) + " °C" : "its weather read";
                return ConnectionTestResult.Ok($"OpenWeatherMap answers - {Text(answer["name"]) ?? "London"}, {temperature}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "current").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"OpenWeatherMap has no operation '{context.OperationName}' - current or forecast.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent: one place, and how to say it.
            var query = new List<(string Name, string Value)>();
            var given = new[] { "city", "city_id", "zip" }.Where(name => Values.Get(p, name) is not null).ToList();
            var latitude = Values.Get(p, "latitude");
            var longitude = Values.Get(p, "longitude");
            if (latitude is not null || longitude is not null)
            {
                given.Add("latitude and longitude");
            }

            if (given.Count != 1)
            {
                throw new InvalidOperationException(given.Count == 0
                    ? "Give the place - a city (berlin,de), a city id, a latitude and longitude, or a postal code (zip)."
                    : $"Give one place - {string.Join(", ", given)} were given.");
            }

            switch (given[0])
            {
                case "city":
                    query.Add(("q", Values.Get(p, "city")!));
                    break;
                case "city_id":
                    {
                        var id = Values.Get(p, "city_id")!;
                        query.Add(("id", long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? id : throw new InvalidOperationException($"'{id}' is not a city's id - 2950159 (city_id).")));
                        break;
                    }

                case "zip":
                    {
                        var zip = Values.Get(p, "zip")!;
                        query.Add(("zip", Postal.IsMatch(zip) ? zip : throw new InvalidOperationException($"'{zip}' is not a postal code and country - 10115,de (zip).")));
                        break;
                    }

                default:
                    query.Add(("lat", Coordinate(latitude, 90, "latitude")));
                    query.Add(("lon", Coordinate(longitude, 180, "longitude")));
                    break;
            }

            var units = (Values.Get(p, "units") ?? "metric").ToLowerInvariant();
            query.Add(("units", units is "metric" or "imperial" or "standard" ? units : throw new InvalidOperationException($"'{Values.Get(p, "units")}' is not metric, imperial or standard (units).")));
            if (Values.Get(p, "language") is { } language)
            {
                query.Add(("lang", Language.IsMatch(language) ? language.ToLowerInvariant() : throw new InvalidOperationException($"'{language}' is not a language's code - sv, en, zh_cn (language).")));
            }

            var max = MostEntries;
            if (operation == "forecast")
            {
                max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? Math.Min(n, MostEntries) : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : MostEntries;
                query.Add(("cnt", max.ToString(CultureInfo.InvariantCulture)));
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            if (operation == "current")
            {
                yield return Record(await session.GetAsync("/weather", query, cancellationToken), null);
                yield break;
            }

            var forecast = await session.GetAsync("/forecast", query, cancellationToken);
            var city = forecast["city"] as JsonObject;
            foreach (var entry in (forecast["list"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
            {
                yield return Record(entry, city);
            }
        }

        private static string Coordinate(string? given, int most, string name) =>
            given is null ? throw new InvalidOperationException("Give both the latitude and the longitude (latitude, longitude).")
                : double.TryParse(given, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) && Math.Abs(degrees) <= most ? given
                : throw new InvalidOperationException($"'{given}' is not a {name} - between -{most} and {most} ({name}).");

        // OpenWeatherMap's answer as a record: its fields as they are - a list or an object as JSON - and what is read most beside
        // them: the temperature, how it feels, humidity, pressure, the description and the wind's speed; a forecast's city too.
        private static ConnectorRecord Record(JsonObject answer, JsonObject? city)
        {
            var fields = answer.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                var other => (object?)other.ToJsonString(),
            }, StringComparer.Ordinal);
            static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
            fields["temperature"] = Number(answer["main"]?["temp"]);
            fields["feels_like"] = Number(answer["main"]?["feels_like"]);
            fields["humidity"] = Number(answer["main"]?["humidity"]);
            fields["pressure"] = Number(answer["main"]?["pressure"]);
            fields["description"] = Text((answer["weather"] as JsonArray)?.FirstOrDefault()?["description"]);
            fields["wind_speed"] = Number(answer["wind"]?["speed"]);
            if (city is not null)
            {
                fields["city"] = Text(city["name"]);
                fields["country"] = Text(city["country"]);
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string key;

            private Session(HttpClient client, string api, string key)
            {
                this.client = client;
                this.api = api;
                this.key = key;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/data/2.5", StringComparison.Ordinal))
                {
                    root = root[..^"/data/2.5".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not OpenWeatherMap's address - its own, {DefaultEndpoint}.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The OpenWeatherMap connection has no API key.");
                return new Session(egress.CreateHttpClient(timeout), root + "/data/2.5", key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A GET with the key in the address, as OpenWeatherMap takes it; its JSON object, or its code and message.</summary>
            public async Task<JsonObject> GetAsync(string route, List<(string Name, string Value)> query, CancellationToken cancellationToken)
            {
                var address = api + route + "?" + string.Join("&", query.Append(("appid", key)).Select(pair => Uri.EscapeDataString(pair.Item1) + "=" + Uri.EscapeDataString(pair.Item2)));
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, address), cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("OpenWeatherMap answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json as JsonObject ?? throw new InvalidOperationException("OpenWeatherMap answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                // Its message - never the address asked, which carries the key.
                var said = Text(json?["message"]);
                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"OpenWeatherMap answered HTTP {status}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}"
                    + (status == 401 ? " - check the API key (a new one takes a while to work)." : string.Empty));
            }
        }
    }
}
