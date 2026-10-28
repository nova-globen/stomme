#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.QuickChart;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - QuickChart (n8n's QuickChart node) without a server: every type, label, number, colour, size and format held to
    /// what QuickChart draws before anything is sent; n8n's chart - its type, labels, one dataset, a line's fill and point style, a
    /// bar on its side - or a whole configuration, sent as JSON and never as text QuickChart would run; the size, ratio,
    /// background and format beside it; the chart saved into the run's folder under the name of what came back; QuickChart's error
    /// from its header, its picture of the error never saved; a test reading its health check.
    /// </summary>
    public sealed class QuickChartConnectorTests : IDisposable
    {
        private const string Address = "https://charts.kafe.example";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-quickchart-" + Guid.NewGuid().ToString("N"));

        public QuickChartConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed record Sent(string Method, string Url, string? Body);

        private sealed class QuickChart : HttpMessageHandler
        {
            private readonly Func<Sent, HttpResponseMessage> answer;

            public QuickChart(Func<Sent, HttpResponseMessage> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(sent);
                return answer(sent);
            }
        }

        private static HttpResponseMessage Image(string type, string bytes, HttpStatusCode status = HttpStatusCode.OK, string? error = null)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(bytes)) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            if (error is not null)
            {
                response.Headers.TryAddWithoutValidation("X-quickchart-error", error);
            }

            return response;
        }

        private Task<List<ConnectorRecord>> Run(QuickChart chart, Dictionary<string, string> parameters, string endpoint = Address, bool files = true, long cap = 1 << 20) =>
            Contexts.DrainAsync(new QuickChartConnector(), Contexts.Extract(new StubEgress(chart), endpoint, "chart", parameters, new Dictionary<string, string>(), new Dictionary<string, string>()) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        [Fact]
        public void It_declares_itself_as_n8ns_quickchart_node()
        {
            IConnector connector = new QuickChartConnector();
            Assert.Equal(("quickchart", "QuickChart", AuthenticationType.None), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "chart" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_what_quickchart_draws_before_anything_is_sent()
        {
            async Task<string> Refused(Dictionary<string, string> parameters, string endpoint = Address, bool files = true, string operation = "chart")
            {
                var chart = new QuickChart(_ => Image("image/png", "png"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new QuickChartConnector(), Contexts.Extract(new StubEgress(chart), endpoint, operation, parameters, new Dictionary<string, string>(), new Dictionary<string, string>()) with { Files = files ? PipelineConnectorFiles.Under(_root, 1 << 20) : null }));
                Assert.Empty(chart.Requests); // refused before anything was sent
                return refusal.Message;
            }

            var data = new Dictionary<string, string> { ["data"] = "12, 19" };
            Dictionary<string, string> With(params (string Name, string Value)[] more) => more.Aggregate(new Dictionary<string, string>(data), (all, pair) => { all[pair.Name] = pair.Value; return all; });

            Assert.Equal("QuickChart has no operation 'gauge' - chart.", await Refused(data, operation: "gauge"));
            Assert.Equal("Give the data - numbers by commas, 12, 19, 7, or a JSON list (data).", await Refused(new()));
            Assert.Equal("'tolv' is not a number (data).", await Refused(new() { ["data"] = "12, tolv" }));
            Assert.Equal("'[12, [19]]' is not a JSON list of plain values (data).", await Refused(new() { ["data"] = "[12, [19]]" }));
            Assert.Equal("3 labels for 2 numbers - give one label a number (labels).", await Refused(With(("labels", "Mån, Tis, Ons"))));
            Assert.Equal("A label is one line of at most 200 characters (labels).", await Refused(With(("labels", "[\"Mån\", \"Tis\\nOns\"]"))));
            Assert.Equal("'gauge' is not a chart QuickChart draws - bar, line, pie, doughnut, polarArea, radar, scatter, bubble, boxplot, violin (type).", await Refused(With(("type", "gauge"))));
            Assert.Equal("'blå;}' is not a colour - #36A2EB, rgba(54,162,235,0.5) or a colour's name (background_color).", await Refused(With(("background_color", "blå;}"))));
            Assert.Equal("'star5' is not a point style - circle, cross, crossRot, dash, line, rect, rectRot, rectRounded, star, triangle (point_style).", await Refused(With(("type", "line"), ("point_style", "star5"))));
            Assert.Equal("A pie chart is not drawn on its side - only bar, boxplot and violin (horizontal).", await Refused(With(("type", "pie"), ("horizontal", "true"))));
            Assert.Equal("'4000' is not 1 to 3000 pixels (width).", await Refused(With(("width", "4000"))));
            Assert.Equal("'0' is not 1 to 3000 pixels (height).", await Refused(With(("height", "0"))));
            Assert.Equal("'3' is not 1 or 2 (pixel_ratio).", await Refused(With(("pixel_ratio", "3"))));
            Assert.Equal("'gif' is not png, svg, pdf or webp (format).", await Refused(With(("format", "gif"))));
            Assert.Equal("A configuration is a Chart.js configuration as JSON - {\"type\": \"bar\", \"data\": {...}} - never script (config).", await Refused(new() { ["config"] = "{type: 'bar', data: {datasets: [{data: [(function(){ return 7 })()]}]}}" }));
            Assert.Equal("A configuration is a Chart.js configuration as JSON - {\"type\": \"bar\", \"data\": {...}} - never script (config).", await Refused(new() { ["config"] = "\"{type:'bar'}\"" }));
            Assert.Equal("A configuration is a Chart.js configuration as JSON - {\"type\": \"bar\", \"data\": {...}} - never script (config).", await Refused(new() { ["config"] = "{\"options\": {\"legend\": false}}" })); // an object, but no chart: no type, no data
            Assert.Equal("A chart is saved into the run's pipeline folder - run it from a pipeline.", await Refused(data, files: false));
            Assert.Equal("Give QuickChart's address - https://quickchart.io, or your own.", await Refused(data, ""));
            Assert.Equal("'https://charts.kafe.example/?key=x' is not QuickChart's address - https://quickchart.io, with no query.", await Refused(data, "https://charts.kafe.example/?key=x"));
        }

        [Fact]
        public async Task N8ns_chart_goes_as_json_and_is_saved_under_the_name_of_what_came_back()
        {
            var chart = new QuickChart(_ => Image("image/png", "PNG-BYTES"));
            var saved = Assert.Single(await Run(chart, new() { ["type"] = "line", ["labels"] = "Mån, \"Tis\"", ["data"] = "[12, 19.5]", ["label"] = "Bullar", ["background_color"] = "rgba(54,162,235,0.5)", ["border_color"] = "#36A2EB", ["fill"] = "false", ["point_style"] = "Star", ["width"] = "800", ["height"] = "400", ["pixel_ratio"] = "1", ["background"] = "white", ["file"] = "rapport/bullar.png" }, Address + "/chart")).Fields;
            var sent = Assert.Single(chart.Requests);
            Assert.Equal(("POST", Address + "/chart"), (sent.Method, sent.Url)); // the chart's own address given: QuickChart's
            Assert.Equal(
                JsonNode.Parse("{\"chart\":{\"type\":\"line\",\"data\":{\"labels\":[\"Mån\",\"\\\"Tis\\\"\"],\"datasets\":[{\"label\":\"Bullar\",\"data\":[12,19.5],\"backgroundColor\":\"rgba(54,162,235,0.5)\",\"borderColor\":\"#36A2EB\",\"fill\":false,\"pointStyle\":\"star\"}]}},\"width\":800,\"height\":400,\"devicePixelRatio\":1,\"format\":\"png\",\"backgroundColor\":\"white\"}")!.ToJsonString(),
                JsonNode.Parse(sent.Body!)!.ToJsonString()); // an object, never a string QuickChart would evaluate - a quote in a label stays a quote
            Assert.Equal("PNG-BYTES", File.ReadAllText(Path.Combine(_root, "rapport", "bullar.png")));
            Assert.Equal(("rapport/bullar.png", 9L, "image/png", "png", 800L), ((string?)saved["file"], (long?)saved["size"], (string?)saved["content_type"], (string?)saved["format"], (long?)saved["width"]));

            // n8n's defaults: a bar chart, 500 by 300 at twice the pixels, a dataset called Chart; a bar on its side.
            var side = new QuickChart(_ => Image("image/png", "png"));
            await Run(side, new() { ["data"] = "1,2", ["horizontal"] = "true" });
            Assert.Equal(JsonNode.Parse("{\"chart\":{\"type\":\"horizontalBar\",\"data\":{\"labels\":[],\"datasets\":[{\"label\":\"Chart\",\"data\":[1,2]}]}},\"width\":500,\"height\":300,\"devicePixelRatio\":2,\"format\":\"png\"}")!.ToJsonString(), JsonNode.Parse(side.Requests[0].Body!)!.ToJsonString());
            var line = new QuickChart(_ => Image("image/png", "png"));
            await Run(line, new() { ["type"] = "line", ["data"] = "1" });
            Assert.True(JsonNode.Parse(line.Requests[0].Body!)!["chart"]!["data"]!["datasets"]![0]!["fill"]!.GetValue<bool>()); // a line filled, as n8n

            // A webp asked for and a PNG answered: named .png, as it is.
            var webp = new QuickChart(_ => Image("image/png", "png"));
            var named = Assert.Single(await Run(webp, new() { ["data"] = "1", ["format"] = "webp", ["file"] = "diagram/" })).Fields;
            Assert.Equal(("diagram/chart.png", "png", "webp"), ((string?)named["file"], (string?)named["format"], (string?)named["requested_format"]));
            Assert.True(File.Exists(Path.Combine(_root, "diagram", "chart.png")));
            var svg = new QuickChart(_ => Image("image/svg+xml", "<svg/>"));
            Assert.Equal("chart.svg", (string?)Assert.Single(await Run(svg, new() { ["data"] = "1", ["format"] = "svg" })).Fields["file"]);
        }

        [Fact]
        public async Task A_whole_configuration_goes_as_it_is_given()
        {
            var chart = new QuickChart(_ => Image("application/pdf", "%PDF"));
            var config = "{\n  \"type\": \"radar\",\n  \"data\": {\"labels\": [\"a\"], \"datasets\": [{\"data\": [1]}]},\n  \"options\": {\"legend\": {\"display\": false}}\n}";
            var saved = Assert.Single(await Run(chart, new() { ["query"] = config, ["format"] = "pdf", ["type"] = "pie" })).Fields;
            Assert.Equal(JsonNode.Parse(config)!.ToJsonString(), JsonNode.Parse(chart.Requests[0].Body!)!["chart"]!.ToJsonString()); // from the Query box; type and data left for it
            Assert.Equal(("chart.pdf", "application/pdf"), ((string?)saved["file"], (string?)saved["content_type"]));
        }

        [Fact]
        public async Task Quickcharts_error_is_said_and_its_picture_of_it_never_saved()
        {
            async Task<string> Failed(HttpResponseMessage response) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new QuickChart(_ => response), new() { ["data"] = "1", ["file"] = "fel.png" }))).Message;

            Assert.Equal("QuickChart answered HTTP 500: Requested width exceeds maximum of 3000.", await Failed(Image("image/png", "ERROR-PICTURE", HttpStatusCode.InternalServerError, "Requested width exceeds maximum of 3000")));
            Assert.Equal("QuickChart answered HTTP 200: Invalid input.", await Failed(Image("image/png", "ERROR-PICTURE", HttpStatusCode.OK, "Invalid input")));
            Assert.Equal("QuickChart answered HTTP 502.", await Failed(Image("text/html", "<html>Bad gateway</html>", HttpStatusCode.BadGateway)));
            Assert.Equal("QuickChart answered with text/html, not a chart - check the address: a sign-in page or a proxy answers so.", await Failed(Image("text/html", "<html>Sign in</html>")));
            Assert.False(File.Exists(Path.Combine(_root, "fel.png")));

            var tooBig = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new QuickChart(_ => Image("image/png", new string('x', 64))), new() { ["data"] = "1" }, cap: 16));
            Assert.Equal("The chart is 64 bytes, over the pipeline folder's 16; it is not saved.", tooBig.Message);
        }

        [Fact]
        public async Task A_test_reads_quickcharts_health_check()
        {
            var connector = new QuickChartConnector();
            var healthy = new QuickChart(sent => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true,\"version\":\"1.8.1\"}", Encoding.UTF8, "application/json") });
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(healthy), Address, null, new Dictionary<string, string>()));
            Assert.Equal((true, "QuickChart 1.8.1 answers."), (ok.Success, ok.Message));
            Assert.Equal(Address + "/healthcheck", healthy.Requests[0].Url);
            var page = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new QuickChart(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>") })), Address, null, new Dictionary<string, string>()));
            Assert.Equal((false, "QuickChart did not answer its health check (HTTP 200) - check the address."), (page.Success, page.Message));
        }
    }

    /// <summary>
    /// BL-229 - QuickChart LIVE, against this machine's throwaway QuickChart 1.8 (scripts/quickchart-test-server.ps1), never
    /// quickchart.io - fail-not-skip without it (D46): a test reading its health check; a bar chart drawn as a PNG and a line
    /// chart as an SVG, each saved and each what it says it is; a webp asked for and the PNG QuickChart answers with named so; a
    /// whole configuration drawn; a chart past QuickChart's size refused here, and its own refusal of a configuration it cannot
    /// draw said in its words with nothing saved.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class QuickChartConnectorLiveTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-quickchart-live-" + Guid.NewGuid().ToString("N"));

        public QuickChartConnectorLiveTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static HostConnectorEgress Egress => new(new[] { new Uri(QuickChartTestEndpoint.Endpoint).Host });

        private async Task<IReadOnlyDictionary<string, object?>> Draw(Dictionary<string, string> parameters) =>
            Assert.Single(await Contexts.DrainAsync(new QuickChartConnector(), Contexts.Extract(Egress, QuickChartTestEndpoint.Endpoint, "chart", parameters, new Dictionary<string, string>(), new Dictionary<string, string>(), timeoutSeconds: 60) with { Files = PipelineConnectorFiles.Under(_root, 8 << 20) })).Fields;

        [Fact]
        public async Task A_test_reads_the_health_check()
        {
            var tested = await new QuickChartConnector().TestConnectionAsync(Contexts.Test(Egress, QuickChartTestEndpoint.Endpoint, timeoutSeconds: 60));
            Assert.True(tested.Success, tested.Message + " " + QuickChartTestEndpoint.Missing);
            Assert.Equal("QuickChart 1.8.1 answers.", tested.Message);
        }

        [Fact]
        public async Task Charts_are_drawn_saved_and_named_by_what_came_back()
        {
            var bar = await Draw(new() { ["labels"] = "Mån, Tis, Ons", ["data"] = "12, 19, 7", ["label"] = "Bullar", ["file"] = "bullar.png" });
            var png = await File.ReadAllBytesAsync(Path.Combine(_root, "bullar.png"));
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
            Assert.Equal(((long)png.Length, "image/png"), ((long?)bar["size"], (string?)bar["content_type"]));

            await Draw(new() { ["type"] = "line", ["labels"] = "[\"Mån\", \"Tis\"]", ["data"] = "[3, 5]", ["format"] = "svg", ["file"] = "linje/" });
            Assert.StartsWith("<?xml", await File.ReadAllTextAsync(Path.Combine(_root, "linje", "chart.svg")));

            var webp = await Draw(new() { ["data"] = "1, 2", ["format"] = "webp" });
            Assert.Equal(("chart.png", "image/png", "webp"), ((string?)webp["file"], (string?)webp["content_type"], (string?)webp["requested_format"])); // QuickChart 1.8 makes no webp

            var whole = await Draw(new() { ["config"] = "{\"type\": \"radar\", \"data\": {\"labels\": [\"a\", \"b\", \"c\"], \"datasets\": [{\"data\": [1, 2, 3]}]}}", ["file"] = "radar.png" });
            Assert.True((long?)whole["size"] > 0);

            Assert.Equal("'4000' is not 1 to 3000 pixels (width).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Draw(new() { ["data"] = "1", ["width"] = "4000" }))).Message);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Draw(new() { ["config"] = "{\"type\": \"bar\", \"data\": {\"datasets\": \"oops\"}}", ["file"] = "fel.png" }));
            Assert.Equal("QuickChart answered HTTP 500.", refused.Message); // its picture of the error, with no words in its header (measured)
            Assert.False(File.Exists(Path.Combine(_root, "fel.png")));
        }
    }
}
