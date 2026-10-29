#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.QuickChart
{
    /// <summary>
    /// QuickChart (n8n's QuickChart node) - a chart drawn by a QuickChart server (<c>{address}/chart</c>: quickchart.io, or
    /// one of your own, which n8n's node cannot use - its address is fixed) and saved into the run's pipeline folder.
    /// </summary>
    /// <remarks>
    /// <c>chart</c>: n8n's chart - its type (bar, line, pie, doughnut, polar area, and radar, scatter, bubble, box plot and violin),
    /// labels and data, one dataset's label, colours, fill and point style, the chart's size, pixel ratio, background and format -
    /// or a whole Chart.js configuration as JSON. The chart goes to QuickChart as JSON, never as text: QuickChart evaluates a
    /// configuration sent as text as JavaScript (measured: a function in it ran), so nothing a run puts in a label or a value can
    /// become code there. Measured against QuickChart 1.8.1 (<c>scripts/quickchart-test-server.ps1</c>), which draws what it
    /// cannot do rather than say so: an error is a 500 carrying a picture of the error (its words in <c>X-quickchart-error</c>),
    /// a chart type it does not know a 200 with an empty chart, a format it does not make (webp, gif) a 200 with a PNG - so a
    /// type and a format are held to what it makes, a failure is said in its own words and never saved, and the file is named by
    /// what came back. A width or height past 3000 it refuses; it is refused here first.
    /// </remarks>
    public sealed class QuickChartConnector : IConnector, IConnectorExtractor
    {
        public const int MostSide = 3000; // QuickChart's own limit, measured

        private static readonly string[] Types = { "bar", "line", "pie", "doughnut", "polarArea", "radar", "scatter", "bubble", "boxplot", "violin" };

        private static readonly string[] Horizontal = { "bar", "boxplot", "violin" }; // n8n's HORIZONTAL_CHARTS

        private static readonly string[] Formats = { "png", "svg", "pdf", "webp" };

        private static readonly string[] PointStyles = { "circle", "cross", "crossRot", "dash", "line", "rect", "rectRot", "rectRounded", "star", "triangle" };

        private static readonly Regex ColourPattern = new(@"^(#[0-9A-Fa-f]{3,8}|[a-z]{3,20}|rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(,\s*(0|1|0?\.\d+)\s*)?\))$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "quickchart";

        public string DisplayName => "QuickChart";

        public AuthenticationType AuthType => AuthenticationType.None;

        public IReadOnlyDictionary<string, string>? CredentialLabels => null;

        public string EndpointExample => "https://quickchart.io";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://quickchart.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://quickchart.io/documentation/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("chart", "Draw a chart", "n8n's QuickChart: a chart saved as a file in the run's pipeline folder.")
            {
                Parameters = new(new[]
                {
                    Field("type", "Type", "bar (the default), line, pie, doughnut, polarArea, radar, scatter, bubble, boxplot or violin."),
                    Field("labels", "Labels", "By commas - Mån, Tis, Ons - or a JSON list."),
                    Field("data", "Data", "Numbers by commas - 12, 19, 7 - or a JSON list."),
                    Field("label", "Dataset label", "Bullar. Empty: Chart, as n8n."),
                    Field("background_color", "Dataset colour", "#36A2EB, rgba(54,162,235,0.5) or a colour's name."),
                    Field("border_color", "Line colour", "As the dataset colour."),
                    new ConnectorConfigField("fill", "Fill", ConnectorConfigFieldType.Boolean, Required: false, Description: "A line chart filled under its line - true by default, as n8n."),
                    Field("point_style", "Point style", "A line chart's points: circle, cross, crossRot, dash, line, rect, rectRot, rectRounded, star or triangle."),
                    new ConnectorConfigField("horizontal", "Horizontal", ConnectorConfigFieldType.Boolean, Required: false, Description: "A bar, box plot or violin chart on its side."),
                    new ConnectorConfigField("config", "Configuration", ConnectorConfigFieldType.Multiline, Required: false, Description: "A whole Chart.js configuration as JSON, in place of type, labels and data. Over several lines from the step's Query box."),
                    new ConnectorConfigField("width", "Width", ConnectorConfigFieldType.Integer, Required: false, Description: $"Pixels, 1 to {MostSide}.") { Default = "500" },
                    new ConnectorConfigField("height", "Height", ConnectorConfigFieldType.Integer, Required: false, Description: $"Pixels, 1 to {MostSide}.") { Default = "300" },
                    new ConnectorConfigField("pixel_ratio", "Pixel ratio", ConnectorConfigFieldType.Integer, Required: false, Description: "1 or 2 - the image is that many times the size.") { Default = "2" },
                    Field("background", "Background", "The chart's background - white, #FFFFFF. Empty: transparent."),
                    Field("format", "Format", "png (the default), svg, pdf or webp."),
                    Field("file", "Save to", "A file in the run's pipeline folder - rapport/bullar.png - or a folder ending in /. Empty: chart, named by its format."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var root = Root(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, root + "/healthcheck"), cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                }

                return response.IsSuccessStatusCode && json?["success"] is JsonValue ok && ok.TryGetValue<bool>(out var success) && success
                    ? ConnectionTestResult.Ok($"QuickChart {(json["version"] is JsonValue version ? version.ToString() + " " : string.Empty)}answers.")
                    : ConnectionTestResult.Fail($"QuickChart did not answer its health check (HTTP {(int)response.StatusCode}) - check the address.");
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
            if (!string.Equals((context.OperationName ?? "chart").Trim(), "chart", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"QuickChart has no operation '{context.OperationName}' - chart.");
            }

            // Everything held to QuickChart's shape before anything is sent.
            var p = context.Parameters;
            var root = Root(context.Endpoint);
            var chart = Chart(p);
            var width = Side(p, "width", 500);
            var height = Side(p, "height", 300);
            var ratio = Values.Get(p, "pixel_ratio") is { } given ? given is "1" or "2" ? int.Parse(given, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not 1 or 2 (pixel_ratio).") : 2;
            var format = (Values.Get(p, "format") ?? "png").ToLowerInvariant() is var chosen && Formats.Contains(chosen) ? chosen : throw new InvalidOperationException($"'{Values.Get(p, "format")}' is not png, svg, pdf or webp (format).");
            var target = Values.Get(p, "file");
            if (target is not null && (target.Length > 260 || target.Any(char.IsControl)))
            {
                throw new InvalidOperationException("A file is a path in the run's pipeline folder - rapport/bullar.png (file).");
            }

            var files = context.Files ?? throw new InvalidOperationException("A chart is saved into the run's pipeline folder - run it from a pipeline.");
            var body = new JsonObject { ["chart"] = chart, ["width"] = width, ["height"] = height, ["devicePixelRatio"] = ratio, ["format"] = format };
            if (Values.Get(p, "background") is { } background)
            {
                body["backgroundColor"] = Colour(background, "background");
            }

            var payload = body.ToJsonString(Wire);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, root + "/chart") { Content = new StringContent(payload, Encoding.UTF8, "application/json") }, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            var said = response.Headers.TryGetValues("X-quickchart-error", out var errors) ? errors.FirstOrDefault() : null;
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode || !string.IsNullOrEmpty(said))
            {
                // QuickChart draws its error as a picture: its words are in a header, and the picture is not kept.
                throw new InvalidOperationException($"QuickChart answered HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}");
            }

            var extension = type switch
            {
                "image/png" => "png",
                "image/svg+xml" => "svg",
                "application/pdf" => "pdf",
                "image/webp" => "webp",
                _ => throw new InvalidOperationException($"QuickChart answered with {(type.Length == 0 ? "nothing it named" : type)}, not a chart - check the address: a sign-in page or a proxy answers so."),
            };
            if (response.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
            {
                throw new InvalidOperationException($"The chart is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not saved.");
            }

            // Named by what came back: QuickChart answers a format it does not make with a PNG.
            var file = target is null ? "chart." + extension : target.EndsWith('/') ? target + "chart." + extension : target;
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var written = await files.WriteAsync(file, content, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["file"] = file, ["size"] = written, ["content_type"] = type, ["format"] = extension, ["requested_format"] = format,
                ["width"] = (long)width, ["height"] = (long)height, ["chart"] = chart.ToJsonString(Wire),
            });
        }

        private static string Root(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("Give QuickChart's address - https://quickchart.io, or your own.");
            }

            var root = Values.BaseUrl(endpoint, endpoint);
            if (root.EndsWith("/chart", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^"/chart".Length]; // the chart's own address given: QuickChart's
            }

            var uri = new Uri(root);
            return uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
                ? throw new InvalidOperationException($"'{endpoint}' is not QuickChart's address - https://quickchart.io, with no query.")
                : root;
        }

        // The chart as Chart.js reads it: a whole configuration given as JSON, or n8n's - its type, labels and one dataset.
        private static JsonObject Chart(IReadOnlyDictionary<string, string> p)
        {
            if ((Values.Get(p, "config") ?? Values.Get(p, "query")) is { } config)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(config);
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                return parsed is JsonObject whole && whole["type"] is JsonValue && whole["data"] is JsonObject
                    ? whole
                    : throw new InvalidOperationException("A configuration is a Chart.js configuration as JSON - {\"type\": \"bar\", \"data\": {...}} - never script (config).");
            }

            var type = Values.Get(p, "type") is { } given
                ? Types.FirstOrDefault(known => string.Equals(known, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not a chart QuickChart draws - {string.Join(", ", Types)} (type).")
                : "bar";
            var labels = List(p, "labels", text => (JsonNode?)JsonValue.Create(text.Length <= 200 && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("A label is one line of at most 200 characters (labels).")));
            var data = List(p, "data", text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : throw new InvalidOperationException($"'{text}' is not a number (data)."));
            if (data.Count == 0)
            {
                throw new InvalidOperationException("Give the data - numbers by commas, 12, 19, 7, or a JSON list (data).");
            }

            if (labels.Count > 0 && labels.Count != data.Count)
            {
                throw new InvalidOperationException($"{labels.Count} labels for {data.Count} numbers - give one label a number (labels).");
            }

            var dataset = new JsonObject { ["label"] = Values.Get(p, "label") is { } label ? label.Length <= 200 && !label.Any(char.IsControl) ? label : throw new InvalidOperationException("A label is one line of at most 200 characters (label).") : "Chart", ["data"] = data };
            if (Values.Get(p, "background_color") is { } fillColour)
            {
                dataset["backgroundColor"] = Colour(fillColour, "background_color");
            }

            if (Values.Get(p, "border_color") is { } lineColour)
            {
                dataset["borderColor"] = Colour(lineColour, "border_color");
            }

            if (type == "line")
            {
                dataset["fill"] = Flag(p, "fill") ?? true;
                if (Values.Get(p, "point_style") is { } style)
                {
                    dataset["pointStyle"] = PointStyles.FirstOrDefault(known => string.Equals(known, style, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{style}' is not a point style - {string.Join(", ", PointStyles)} (point_style).");
                }
            }

            if (Flag(p, "horizontal") == true)
            {
                type = Horizontal.Contains(type) ? "horizontal" + char.ToUpperInvariant(type[0]) + type[1..] : throw new InvalidOperationException($"A {type} chart is not drawn on its side - only bar, boxplot and violin (horizontal).");
            }

            return new JsonObject { ["type"] = type, ["data"] = new JsonObject { ["labels"] = labels, ["datasets"] = new JsonArray(dataset) } };
        }

        // A list by commas, or as JSON (n8n's labels array, its data).
        private static JsonArray List(IReadOnlyDictionary<string, string> p, string name, Func<string, JsonNode?> item)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return new JsonArray();
            }

            var trimmed = given.Trim();
            if (trimmed.StartsWith('['))
            {
                JsonArray? parsed;
                try
                {
                    parsed = JsonNode.Parse(trimmed) as JsonArray;
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                return parsed is not null && parsed.All(entry => entry is JsonValue)
                    ? new JsonArray(parsed.Select(entry => item(entry is JsonValue value && value.TryGetValue<string>(out var text) ? text : entry!.ToJsonString())).ToArray())
                    : throw new InvalidOperationException($"'{(trimmed.Length > 40 ? trimmed[..40] + "..." : trimmed)}' is not a JSON list of plain values ({name}).");
            }

            return new JsonArray(trimmed.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).Select(item).ToArray());
        }

        private static string Colour(string given, string name) =>
            ColourPattern.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a colour - #36A2EB, rgba(54,162,235,0.5) or a colour's name ({name}).");

        private static int Side(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var pixels) && pixels is > 0 and <= MostSide ? pixels : throw new InvalidOperationException($"'{given}' is not 1 to {MostSide} pixels ({name}).")
                : fallback;

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given ? bool.TryParse(given, out var flag) ? flag : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
    }
}
