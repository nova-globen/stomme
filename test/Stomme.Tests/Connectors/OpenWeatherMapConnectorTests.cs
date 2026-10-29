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
using Stomme.Modules.Connectors.OpenWeatherMap;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// OpenWeatherMap (n8n's OpenWeatherMap node) without a server: one place - a city, a city id, coordinates within
    /// their range or a postal code - its units and language held to their shape before anything is sent; the weather now as
    /// one record, what is read most beside OpenWeatherMap's own fields; the forecast each three hours a record with its city,
    /// up to the most and OpenWeatherMap's 40; the key in the address alone and in no message; OpenWeatherMap's message on a
    /// failure; a test reading London's weather.
    /// </summary>
    public sealed class OpenWeatherMapConnectorTests
    {
        private const string Api = "https://api.openweathermap.org/data/2.5";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "owm-kafe-key" };

        private sealed class Weather(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
        {
            public List<string> Asked { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Asked.Add(url);
                var (status, body) = answer(url);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private const string Now = "{\"coord\":{\"lon\":11.97,\"lat\":57.71},\"weather\":[{\"id\":500,\"main\":\"Rain\",\"description\":\"lätt regn\",\"icon\":\"10d\"}],\"base\":\"stations\",\"main\":{\"temp\":8.4,\"feels_like\":5.9,\"pressure\":1009,\"humidity\":87},\"visibility\":10000,\"wind\":{\"speed\":5.1,\"deg\":230},\"dt\":1792400000,\"timezone\":7200,\"id\":2711537,\"name\":\"Göteborg\",\"cod\":200}";

        private static string Forecast(int count) => "{\"cod\":\"200\",\"cnt\":" + count + ",\"list\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"dt\":{1792400000 + i * 10800},\"main\":{{\"temp\":{7 + i},\"feels_like\":5,\"pressure\":1010,\"humidity\":80}},\"weather\":[{{\"description\":\"mulet\"}}],\"wind\":{{\"speed\":3.2}},\"dt_txt\":\"2026-10-18 {i * 3:00}:00:00\"}}")) + "],\"city\":{\"id\":2711537,\"name\":\"Göteborg\",\"country\":\"SE\",\"timezone\":7200}}";

        private static Task<List<ConnectorRecord>> Run(Weather weather, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new OpenWeatherMapConnector(), Contexts.Extract(new StubEgress(weather), "", operation, parameters, new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_openweathermap_node()
        {
            IConnector connector = new OpenWeatherMapConnector();
            Assert.Equal(("openweathermap", "OpenWeatherMap", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "current", "forecast" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task One_place_and_how_to_say_it_are_held_to_their_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var weather = new Weather(_ => (HttpStatusCode.OK, Now));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(weather, operation, parameters));
                Assert.Empty(weather.Asked);
                return refusal.Message;
            }

            Assert.Equal("OpenWeatherMap has no operation 'history' - current or forecast.", await Refused("history", new() { ["city"] = "berlin,de" }));
            Assert.Equal("Give the place - a city (berlin,de), a city id, a latitude and longitude, or a postal code (zip).", await Refused("current", new()));
            Assert.Equal("Give one place - city, zip were given.", await Refused("current", new() { ["city"] = "berlin,de", ["zip"] = "10115,de" }));
            Assert.Equal("Give one place - city_id, latitude and longitude were given.", await Refused("current", new() { ["city_id"] = "2950159", ["latitude"] = "52.52" }));
            Assert.Equal("Give both the latitude and the longitude (latitude, longitude).", await Refused("current", new() { ["latitude"] = "52.52" }));
            Assert.Equal("'91' is not a latitude - between -90 and 90 (latitude).", await Refused("current", new() { ["latitude"] = "91", ["longitude"] = "13.39" }));
            Assert.Equal("'-180.5' is not a longitude - between -180 and 180 (longitude).", await Refused("current", new() { ["latitude"] = "52.52", ["longitude"] = "-180.5" }));
            Assert.Equal("'berlin' is not a city's id - 2950159 (city_id).", await Refused("current", new() { ["city_id"] = "berlin" }));
            Assert.Equal("'10115/de' is not a postal code and country - 10115,de (zip).", await Refused("current", new() { ["zip"] = "10115/de" }));
            Assert.Equal("'kelvin' is not metric, imperial or standard (units).", await Refused("current", new() { ["city"] = "berlin,de", ["units"] = "kelvin" }));
            Assert.Equal("'svenska' is not a language's code - sv, en, zh_cn (language).", await Refused("current", new() { ["city"] = "berlin,de", ["language"] = "svenska" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("forecast", new() { ["city"] = "berlin,de", ["max"] = "0" }));
        }

        [Fact]
        public async Task The_weather_now_is_one_record_and_the_forecast_each_three_hours()
        {
            var weather = new Weather(url => (HttpStatusCode.OK, url.Contains("/forecast", StringComparison.Ordinal) ? Forecast(40) : Now));
            var now = Assert.Single(await Run(weather, "current", new() { ["city"] = "Göteborg,se", ["language"] = "SV" })).Fields;
            Assert.Equal(("Göteborg", 8.4, 5.9, 87.0, 1009.0, "lätt regn", 5.1), ((string?)now["name"], (double?)now["temperature"], (double?)now["feels_like"], (double?)now["humidity"], (double?)now["pressure"], (string?)now["description"], (double?)now["wind_speed"]));
            Assert.Equal(("{\"temp\":8.4,\"feels_like\":5.9,\"pressure\":1009,\"humidity\":87}", 7200L), ((string?)now["main"], (long?)now["timezone"])); // OpenWeatherMap's own fields as they are
            Assert.Equal(Api + "/weather?q=G%C3%B6teborg%2Cse&units=metric&lang=sv&appid=owm-kafe-key", weather.Asked.Single()); // n8n's metric by default; the key in the address, as OpenWeatherMap takes it

            weather.Asked.Clear();
            await Run(weather, "current", new() { ["city_id"] = "2711537", ["units"] = "Imperial" });
            await Run(weather, "current", new() { ["latitude"] = "57.71", ["longitude"] = "-11.97" });
            await Run(weather, "current", new() { ["zip"] = "411 01,se", ["units"] = "standard" });
            Assert.Equal(new[] { "id=2711537&units=imperial", "lat=57.71&lon=-11.97&units=metric", "zip=411%2001%2Cse&units=standard" }, weather.Asked.Select(url => url[(url.IndexOf('?') + 1)..url.IndexOf("&appid", StringComparison.Ordinal)]));

            weather.Asked.Clear();
            var steps = await Run(weather, "forecast", new() { ["city"] = "Göteborg,se" });
            Assert.Equal(40, steps.Count);
            Assert.Equal(("Göteborg", "SE", 7.0, "mulet", "2026-10-18 00:00:00"), ((string?)steps[0].Fields["city"], (string?)steps[0].Fields["country"], (double?)steps[0].Fields["temperature"], (string?)steps[0].Fields["description"], (string?)steps[0].Fields["dt_txt"]));
            Assert.Equal(Api + "/forecast?q=G%C3%B6teborg%2Cse&units=metric&cnt=40&appid=owm-kafe-key", weather.Asked.Single());
            Assert.Equal(8, (await Run(weather, "forecast", new() { ["city"] = "Göteborg,se", ["max"] = "8" })).Count); // a day; the stand-in gave 40
            Assert.Contains("cnt=8&", weather.Asked.Last(), StringComparison.Ordinal);
            await Run(weather, "forecast", new() { ["city"] = "Göteborg,se", ["max"] = "500" });
            Assert.Contains("cnt=40&", weather.Asked.Last(), StringComparison.Ordinal); // OpenWeatherMap's most
        }

        [Fact]
        public async Task A_failure_is_openweathermaps_message_never_the_key_and_a_test_reads_london()
        {
            var missing = new Weather(_ => (HttpStatusCode.NotFound, "{\"cod\":\"404\",\"message\":\"city not found\"}"));
            var notFound = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "current", new() { ["city"] = "Kassastad" }));
            Assert.Equal("OpenWeatherMap answered HTTP 404: city not found", notFound.Message);
            var wrongKey = new Weather(_ => (HttpStatusCode.Unauthorized, "{\"cod\":401,\"message\":\"Invalid API key. Please see https://openweathermap.org/faq#error401 for more info.\"}"));
            var unauthorized = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(wrongKey, "forecast", new() { ["city"] = "Kassastad" }));
            Assert.Equal("OpenWeatherMap answered HTTP 401: Invalid API key. Please see https://openweathermap.org/faq#error401 for more info. - check the API key (a new one takes a while to work).", unauthorized.Message);
            Assert.DoesNotContain("owm-kafe-key", notFound.Message + unauthorized.Message, StringComparison.Ordinal);
            var odd = new Weather(_ => (HttpStatusCode.OK, "[]"));
            Assert.Equal("OpenWeatherMap answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(odd, "current", new() { ["city"] = "berlin,de" }))).Message);

            var london = new Weather(_ => (HttpStatusCode.OK, "{\"main\":{\"temp\":12.34},\"name\":\"London\"}"));
            var tested = await new OpenWeatherMapConnector().TestConnectionAsync(Contexts.Test(new StubEgress(london), Api, new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "OpenWeatherMap answers - London, 12.3 °C."), (tested.Success, tested.Message));
            Assert.Equal(Api + "/weather?q=London%2CGB&units=metric&appid=owm-kafe-key", london.Asked.Single()); // its own address with its path taken
            var elsewhere = await new OpenWeatherMapConnector().TestConnectionAsync(Contexts.Test(new StubEgress(london), "https://api.openweathermap.org/data/3.0", new Dictionary<string, string>(), Credentials));
            Assert.Equal((false, "'https://api.openweathermap.org/data/3.0' is not OpenWeatherMap's address - its own, https://api.openweathermap.org."), (elsewhere.Success, elsewhere.Message));
        }
    }
}
