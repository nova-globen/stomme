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
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.HomeAssistant;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Home Assistant connector's live tests' server (BL-229): HOMEASSISTANT_TEST_HOST / PORT / TOKEN where set, otherwise this
    /// machine's throwaway Home Assistant that <c>scripts/homeassistant-test-server.ps1</c> makes on 127.0.0.1:18123, its owner's
    /// long-lived token read from the file that script writes under the user's temp folder. No token is a FAILURE that says how to
    /// get one, never a skip (D46).
    /// </summary>
    internal static class HomeAssistantTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("HOMEASSISTANT_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("HOMEASSISTANT_TEST_PORT"), out var port) ? port : 18123;

        public static string Endpoint => $"http://{Host}:{Port}";

        public static string Token
        {
            get
            {
                if (Environment.GetEnvironmentVariable("HOMEASSISTANT_TEST_TOKEN") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-homeassistant-test", "token");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No Home Assistant test server: run 'pwsh scripts/homeassistant-test-server.ps1' (a throwaway Home Assistant on 127.0.0.1:18123, its token in {file}) or set HOMEASSISTANT_TEST_HOST, HOMEASSISTANT_TEST_PORT and HOMEASSISTANT_TEST_TOKEN.");
            }
        }
    }

    /// <summary>
    /// BL-229 - Home Assistant (n8n's Home Assistant node) without a server: every call the API under /api with the token as its
    /// Bearer, whatever the address given ends in; every parameter held to its shape before anything is sent - an entity id, a
    /// domain and service, a JSON object, a flag, a state, an event type, a time; the configuration and its check; services,
    /// states and events held to their most; a service called with its data, each state it changed a record, its own response
    /// when asked; a state set with its attributes or the entity's own kept; an event fired; the error log, a template (from the
    /// Query box) as their text; the logbook and history from a time, to a time, for an entity, history's lists flattened; a
    /// camera's picture written into the run's folder; Home Assistant's words on a failure; a test naming the home.
    /// </summary>
    public sealed class HomeAssistantConnectorTests : IDisposable
    {
        private const string Server = "http://ha.kafe.example.test:8123";

        private static readonly Dictionary<string, string> Credentials = new() { ["token"] = "llat-1" };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-homeassistant-" + Guid.NewGuid().ToString("N"));

        public HomeAssistantConnectorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class Home : HttpMessageHandler
        {
            public List<(string Method, string Url, string? Bearer, string? Body)> Requests { get; } = new();

            public Func<string, string, (HttpStatusCode Status, string Body, string Type)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", "application/json");

            public byte[]? Picture { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method.Method, url, request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null, body));
                if (Picture is not null && url.Contains("/camera_proxy/", StringComparison.Ordinal))
                {
                    var content = new ByteArrayContent(Picture);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                var (status, answer, type) = Answer(request.Method.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, type) };
            }
        }

        private static (HttpStatusCode, string, string) Json(string body, HttpStatusCode status = HttpStatusCode.OK) => (status, body, "application/json");

        private Task<List<ConnectorRecord>> Run(Home home, string operation, Dictionary<string, string> parameters, string endpoint = Server, bool files = true, long cap = 1 << 20) =>
            Contexts.DrainAsync(new HomeAssistantConnector(), Contexts.Extract(new StubEgress(home), endpoint, operation, parameters, credentials: Credentials) with
            {
                Files = files ? PipelineConnectorFiles.Under(_root, cap) : null,
            });

        private const string KassaOpen = "{\"entity_id\":\"input_boolean.kassa_open\",\"state\":\"on\",\"attributes\":{\"editable\":false,\"friendly_name\":\"Kassa open\"},\"last_changed\":\"2026-10-17T15:45:19.679667+00:00\",\"context\":{\"id\":\"01M558\",\"parent_id\":null}}";

        [Fact]
        public void It_declares_itself_as_n8ns_home_assistant_node()
        {
            IConnector connector = new HomeAssistantConnector();
            Assert.Equal(("homeassistant", "Home Assistant", AuthenticationType.Bearer, ConnectorEndpointKind.Url), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.Equal(new[] { ConnectorCategory.Productivity, ConnectorCategory.NetworkInfrastructure }, connector.Categories);
            Assert.Equal(
                new[] { "config", "check_config", "services", "call_service", "states", "state", "set_state", "events", "fire_event", "error_log", "logbook", "history", "render_template", "camera_snapshot" },
                ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Contains("Long-lived access token", connector.CredentialLabels!["token"], StringComparison.Ordinal);
        }

        [Fact]
        public async Task Every_call_is_the_api_with_the_token_as_its_bearer_whatever_the_address_ends_in()
        {
            var home = new Home { Answer = (_, _) => Json("{\"location_name\":\"Kafé\",\"version\":\"2026.9.4\",\"components\":[\"api\",\"http\"]}") };
            foreach (var endpoint in new[] { Server, Server + "/", Server + "/api", Server + "/api/" })
            {
                await Run(home, "config", new(), endpoint);
                Assert.Equal(("GET", Server + "/api/config", (string?)"llat-1"), (home.Requests[^1].Method, home.Requests[^1].Url, home.Requests[^1].Bearer));
            }

            var config = Assert.Single(await Run(home, null!, new())).Fields; // no operation: the configuration, n8n's default
            Assert.Equal(("Kafé", "2026.9.4", "[\"api\",\"http\"]"), ((string?)config["location_name"], (string?)config["version"], (string?)config["components"]));
            var tested = await new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(new StubEgress(home), Server, credentials: Credentials));
            Assert.Equal((true, "Home Assistant 2026.9.4: Kafé."), (tested.Success, tested.Message));
            var tokenless = await new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(new StubEgress(home), Server));
            Assert.Equal((false, "The Home Assistant connection has no access token."), (tokenless.Success, tokenless.Message));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, bool files = true)
            {
                var home = new Home();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(home, operation, parameters, files: files));
                Assert.Empty(home.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Home Assistant has no operation 'turn_on' - config, check_config,", await Refused("turn_on", new()), StringComparison.Ordinal);
            Assert.Equal("Give the entity's id - light.kitchen (entity).", await Refused("state", new()));
            Assert.Equal("'Light.Kitchen' is not an entity id - light.kitchen, sensor.kassa_queue (entity).", await Refused("state", new() { ["entity"] = "Light.Kitchen" }));
            Assert.Equal("'kitchen' is not an entity id - light.kitchen, sensor.kassa_queue (entity).", await Refused("set_state", new() { ["entity"] = "kitchen", ["state"] = "on" }));
            Assert.Equal("'light/../config' is not an entity id - light.kitchen, sensor.kassa_queue (entity).", await Refused("camera_snapshot", new() { ["entity"] = "light/../config" }));
            Assert.Equal("Give the domain (domain).", await Refused("call_service", new() { ["service"] = "turn_on" }));
            Assert.Equal("'Light' is not a domain - lowercase letters, digits and _ (domain).", await Refused("call_service", new() { ["domain"] = "Light", ["service"] = "turn_on" }));
            Assert.Equal("'turn on' is not a service - lowercase letters, digits and _ (service).", await Refused("call_service", new() { ["domain"] = "light", ["service"] = "turn on" }));
            Assert.Equal("The data is not a JSON object - {\"entity_id\": \"light.kitchen\"} (data).", await Refused("call_service", new() { ["domain"] = "light", ["service"] = "turn_on", ["data"] = "[\"light.kitchen\"]" }));
            Assert.Equal("The data is not a JSON object - {\"entity_id\": \"light.kitchen\"} (data).", await Refused("fire_event", new() { ["event"] = "kassa_opened", ["data"] = "till 2" }));
            Assert.Equal("'maybe' is not true or false (response).", await Refused("call_service", new() { ["domain"] = "light", ["service"] = "turn_on", ["response"] = "maybe" }));
            Assert.Equal("Give its state (state).", await Refused("set_state", new() { ["entity"] = "sensor.kassa_queue" }));
            Assert.Equal("A state is at most 255 characters (state).", await Refused("set_state", new() { ["entity"] = "sensor.kassa_queue", ["state"] = new string('x', 256) }));
            Assert.Equal("The attributes is not a JSON object - {\"entity_id\": \"light.kitchen\"} (attributes).", await Refused("set_state", new() { ["entity"] = "sensor.kassa_queue", ["state"] = "3", ["attributes"] = "3" }));
            Assert.Equal("Give the event's type (event).", await Refused("fire_event", new()));
            Assert.Equal("'kassa opened' is not an event type - kassa_opened (event).", await Refused("fire_event", new() { ["event"] = "kassa opened" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-17T08:00:00+02:00 (from).", await Refused("logbook", new() { ["from"] = "yesterday" }));
            Assert.Equal("'light' is not an entity id - light.kitchen, sensor.kassa_queue (entities).", await Refused("history", new() { ["entities"] = "sensor.queue,light" }));
            Assert.Equal("Give the entities - sensor.kassa_queue, light.kitchen (entities).", await Refused("history", new()));
            Assert.Equal("'yes' is not true or false (minimal).", await Refused("history", new() { ["entities"] = "sensor.queue", ["minimal"] = "yes" }));
            Assert.Equal("Give the template - or write it in the step's Query box.", await Refused("render_template", new()));
            Assert.Equal("A camera's picture is written into the run's pipeline folder - run it from a pipeline.", await Refused("camera_snapshot", new() { ["entity"] = "camera.kassa" }, files: false));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("states", new() { ["max"] = "0" }));
        }

        [Fact]
        public async Task Services_states_and_events_are_listed_and_held_to_their_most()
        {
            var home = new Home
            {
                Answer = (_, url) => url.EndsWith("/services", StringComparison.Ordinal)
                    ? Json("[{\"domain\":\"light\",\"services\":{\"turn_on\":{\"fields\":{}}}},{\"domain\":\"input_boolean\",\"services\":{\"toggle\":{}}}]")
                    : url.EndsWith("/events", StringComparison.Ordinal)
                        ? Json("[{\"event\":\"*\",\"listener_count\":1},{\"event\":\"state_changed\",\"listener_count\":12}]")
                        : Json("[" + KassaOpen + "," + KassaOpen.Replace("kassa_open", "kassa_closed") + "]"),
            };
            var services = await Run(home, "services", new());
            Assert.Equal(("light", "{\"turn_on\":{\"fields\":{}}}"), ((string?)services[0].Fields["domain"], (string?)services[0].Fields["services"]));
            Assert.Equal(2, services.Count);
            var state = Assert.Single(await Run(home, "states", new() { ["max"] = "1" })).Fields;
            Assert.Equal(("input_boolean.kassa_open", "on", "{\"editable\":false,\"friendly_name\":\"Kassa open\"}"), ((string?)state["entity_id"], (string?)state["state"], (string?)state["attributes"]));
            Assert.Equal(12L, (await Run(home, "events", new()))[1].Fields["listener_count"]);
            Assert.Equal(Server + "/api/events", home.Requests[^1].Url);
        }

        [Fact]
        public async Task A_service_is_called_with_its_data_and_answers_with_the_states_it_changed_or_its_own_response()
        {
            var home = new Home { Answer = (_, url) => url.Contains("return_response", StringComparison.Ordinal) ? Json("{\"changed_states\":[],\"service_response\":{\"weather.home\":{\"forecast\":[{\"temperature\":12}]}}}") : url.Contains("/notify/", StringComparison.Ordinal) ? Json("[]") : Json("[" + KassaOpen + "]") };
            var changed = Assert.Single(await Run(home, "call_service", new() { ["domain"] = "input_boolean", ["service"] = "turn_on", ["data"] = "{\"entity_id\": \"input_boolean.kassa_open\"}" })).Fields;
            Assert.Equal(("POST", Server + "/api/services/input_boolean/turn_on", (string?)"{\"entity_id\":\"input_boolean.kassa_open\"}"), (home.Requests[^1].Method, home.Requests[^1].Url, home.Requests[^1].Body));
            Assert.Equal(("input_boolean.kassa_open", "on"), ((string?)changed["entity_id"], (string?)changed["state"]));

            var quiet = Assert.Single(await Run(home, "call_service", new() { ["domain"] = "notify", ["service"] = "send_message" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["called"] = true, ["changed"] = 0L }, quiet); // nothing changed: the call itself a record
            Assert.Equal("{}", home.Requests[^1].Body); // no data: an empty object

            var responded = Assert.Single(await Run(home, "call_service", new() { ["domain"] = "weather", ["service"] = "get_forecasts", ["data"] = "{\"entity_id\":\"weather.home\",\"type\":\"daily\"}", ["response"] = "TRUE" })).Fields;
            Assert.Equal(Server + "/api/services/weather/get_forecasts?return_response", home.Requests[^1].Url);
            Assert.Equal(("{\"weather.home\":{\"forecast\":[{\"temperature\":12}]}}", "[]"), ((string?)responded["response"], (string?)responded["changed"]));
        }

        [Fact]
        public async Task A_state_is_set_with_its_attributes_or_the_entitys_own_kept()
        {
            var known = "{\"entity_id\":\"sensor.kassa_queue\",\"state\":\"3\",\"attributes\":{\"unit_of_measurement\":\"people\",\"friendly_name\":\"Kassa queue\"}}";
            var home = new Home
            {
                Answer = (method, url) => (method, url) switch
                {
                    ("GET", var u) when u.EndsWith("sensor.kassa_new", StringComparison.Ordinal) => Json("{\"message\":\"Entity not found.\"}", HttpStatusCode.NotFound),
                    ("GET", _) => Json(known),
                    _ => Json(known.Replace("\"3\"", "\"4\"")),
                },
            };
            await Run(home, "set_state", new() { ["entity"] = "sensor.kassa_queue", ["state"] = "4", ["attributes"] = "{\"unit_of_measurement\": \"guests\"}" });
            Assert.Equal(("POST", Server + "/api/states/sensor.kassa_queue", (string?)"{\"state\":\"4\",\"attributes\":{\"unit_of_measurement\":\"guests\"}}"), (home.Requests[^1].Method, home.Requests[^1].Url, home.Requests[^1].Body));
            Assert.Single(home.Requests); // attributes given: no read first

            home.Requests.Clear();
            var set = Assert.Single(await Run(home, "set_state", new() { ["entity"] = "sensor.kassa_queue", ["state"] = " 4 " })).Fields;
            Assert.Equal(new[] { "GET", "POST" }, home.Requests.Select(request => request.Method));
            Assert.Equal("{\"state\":\" 4 \",\"attributes\":{\"unit_of_measurement\":\"people\",\"friendly_name\":\"Kassa queue\"}}", home.Requests[^1].Body); // none given: the entity's own kept, n8n's way
            Assert.Equal("4", set["state"]);

            home.Requests.Clear();
            await Run(home, "set_state", new() { ["entity"] = "sensor.kassa_new", ["state"] = "1" });
            Assert.Equal("{\"state\":\"1\"}", home.Requests[^1].Body); // a new entity: nothing to keep
        }

        [Fact]
        public async Task Events_logs_templates_and_history_are_sent_as_asked()
        {
            var home = new Home
            {
                Answer = (method, url) => url switch
                {
                    _ when url.Contains("/events/", StringComparison.Ordinal) => Json("{\"message\":\"Event kassa_opened fired.\"}"),
                    _ when url.EndsWith("/error_log", StringComparison.Ordinal) => (HttpStatusCode.OK, "2026-10-17 15:44:55 ERROR (MainThread) [homeassistant.components.bluetooth] No adapter", "application/octet-stream"),
                    _ when url.EndsWith("/template", StringComparison.Ordinal) => (HttpStatusCode.OK, "Queue: 4", "text/plain"),
                    _ when url.Contains("/logbook", StringComparison.Ordinal) => Json("[{\"name\":\"Kassa open\",\"message\":\"turned on\",\"entity_id\":\"input_boolean.kassa_open\",\"when\":\"2026-10-17T15:45:19+00:00\"},{\"name\":\"Home Assistant\",\"message\":\"started\"}]"),
                    _ => Json("[[{\"entity_id\":\"sensor.kassa_queue\",\"state\":\"3\",\"attributes\":{\"unit_of_measurement\":\"people\"}},{\"state\":\"4\"}],[{\"entity_id\":\"light.kitchen\",\"state\":\"on\"}]]"),
                },
            };
            var fired = Assert.Single(await Run(home, "fire_event", new() { ["event"] = "kassa_opened", ["data"] = "{\"till\": 2}" })).Fields;
            Assert.Equal(("POST", Server + "/api/events/kassa_opened", (string?)"{\"till\":2}"), (home.Requests[^1].Method, home.Requests[^1].Url, home.Requests[^1].Body));
            Assert.Equal(new Dictionary<string, object?> { ["event"] = "kassa_opened", ["message"] = "Event kassa_opened fired." }, fired);

            Assert.Equal("2026-10-17 15:44:55 ERROR (MainThread) [homeassistant.components.bluetooth] No adapter", Assert.Single(await Run(home, "error_log", new())).Fields["errorLog"]);
            Assert.Equal("Queue: 4", Assert.Single(await Run(home, "render_template", new() { ["query"] = "Queue: {{ states('sensor.kassa_queue') }}\n{# kassa #}" })).Fields["renderedTemplate"]);
            Assert.Equal("{\"template\":\"Queue: {{ states('sensor.kassa_queue') }}\\n{# kassa #}\"}", home.Requests[^1].Body); // the Query box's lines, as written
            await Run(home, "render_template", new() { ["template"] = "{{ now() }}", ["query"] = "ignored" });
            Assert.Equal("{\"template\":\"{{ now() }}\"}", home.Requests[^1].Body); // the parameter before the box

            var entries = await Run(home, "logbook", new() { ["from"] = "2026-10-17T08:00:00+02:00", ["to"] = "2026-10-17 18:00Z", ["entity"] = "input_boolean.kassa_open", ["max"] = "1" });
            Assert.Equal(Server + "/api/logbook/2026-10-17T08%3A00%3A00%2B02%3A00?end_time=2026-10-17T18%3A00%3A00%2B00%3A00&entity=input_boolean.kassa_open", home.Requests[^1].Url);
            Assert.Equal("turned on", Assert.Single(entries).Fields["message"]);
            await Run(home, "logbook", new());
            Assert.Equal(Server + "/api/logbook", home.Requests[^1].Url);

            var history = await Run(home, "history", new() { ["entities"] = "sensor.kassa_queue, light.kitchen", ["from"] = "2026-10-17T06:00:00Z", ["minimal"] = "true", ["significant"] = "true" });
            Assert.Equal(Server + "/api/history/period/2026-10-17T06%3A00%3A00%2B00%3A00?filter_entity_id=sensor.kassa_queue,light.kitchen&minimal_response&significant_changes_only", home.Requests[^1].Url);
            Assert.Equal(new[] { ("sensor.kassa_queue", "3"), ("sensor.kassa_queue", "4"), ("light.kitchen", "on") }, history.Select(record => ((string)record.Fields["entity_id"]!, (string)record.Fields["state"]!))); // a minimal state carries its entity's id
            Assert.Equal(2, (await Run(home, "history", new() { ["entities"] = "sensor.kassa_queue", ["max"] = "2" })).Count);
            Assert.Equal(Server + "/api/history/period?filter_entity_id=sensor.kassa_queue", home.Requests[^1].Url);
        }

        [Fact]
        public async Task A_cameras_picture_is_written_into_the_runs_folder()
        {
            var home = new Home { Picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 } };
            var shot = Assert.Single(await Run(home, "camera_snapshot", new() { ["entity"] = "camera.kassa" })).Fields;
            Assert.Equal(Server + "/api/camera_proxy/camera.kassa", home.Requests[^1].Url);
            Assert.Equal(new Dictionary<string, object?> { ["entity_id"] = "camera.kassa", ["file"] = "homeassistant/camera_kassa.png", ["size"] = 7L, ["contentType"] = "image/png" }, shot);
            Assert.Equal(home.Picture, File.ReadAllBytes(Path.Combine(_root, "homeassistant", "camera_kassa.png")));
            Assert.Equal("kassa.png", Assert.Single(await Run(home, "camera_snapshot", new() { ["entity"] = "camera.kassa", ["file"] = "kassa.png" })).Fields["file"]);
            var over = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(home, "camera_snapshot", new() { ["entity"] = "camera.kassa", ["file"] = "big.png" }, cap: 4));
            Assert.Equal("The picture is 7 bytes, over the pipeline folder's 4; it is not fetched.", over.Message);
            Assert.False(File.Exists(Path.Combine(_root, "big.png")));
        }

        [Fact]
        public async Task A_failure_is_said_in_home_assistants_words()
        {
            async Task<string> Failed((HttpStatusCode, string, string) answer, string operation = "config", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Home { Answer = (_, _) => answer }, operation, parameters ?? new()))).Message;

            Assert.Equal("Home Assistant answered HTTP 401: 401: Unauthorized - check the access token.", await Failed((HttpStatusCode.Unauthorized, "401: Unauthorized", "text/plain")));
            Assert.Equal("Home Assistant answered HTTP 400: 400: Bad Request", await Failed((HttpStatusCode.BadRequest, "400: Bad Request", "text/plain"), "call_service", new() { ["domain"] = "nope", ["service"] = "nada" }));
            Assert.Equal("Home Assistant answered HTTP 404: Entity not found.", await Failed(Json("{\"message\":\"Entity not found.\"}", HttpStatusCode.NotFound), "state", new() { ["entity"] = "sensor.nope" }));
            Assert.Equal("Home Assistant answered HTTP 400: Error rendering template: TemplateSyntaxError: unexpected '}'", await Failed(Json("{\"message\":\"Error rendering template: TemplateSyntaxError: unexpected '}'\"}", HttpStatusCode.BadRequest), "render_template", new() { ["template"] = "{{ nope( }}" }));
            Assert.Equal("Home Assistant answered HTTP 502.", await Failed((HttpStatusCode.BadGateway, "", "text/plain")));
            Assert.Equal("Home Assistant answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed((HttpStatusCode.OK, "<html>Sign in</html>", "text/html")));
            var test = await new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Home { Answer = (_, _) => (HttpStatusCode.Unauthorized, "401: Unauthorized", "text/plain") }), Server, credentials: Credentials));
            Assert.Equal((false, "Home Assistant answered HTTP 401: 401: Unauthorized - check the access token."), (test.Success, test.Message));
        }
    }

    /// <summary>
    /// BL-229 - the Home Assistant connector against a real Home Assistant 2026.9 (<c>scripts/homeassistant-test-server.ps1</c>'s
    /// throwaway server on loopback) through the HOST's egress: loopback refused until allowlisted; a test naming the home, a wrong
    /// token refused; the configuration and its check; a sensor's state set with attributes, set again with them kept, read, listed
    /// and its history read; an input_boolean switched by a service, each changed state a record, and the logbook saying so; a
    /// service asked for a response it does not give refused in Home Assistant's words; an event fired; a template rendered; the
    /// error log read; a camera that does not exist refused. Fails, never skips, when the server is absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class HomeAssistantConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["token"] = HomeAssistantTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { HomeAssistantTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new HomeAssistantConnector(), Contexts.Extract(Egress, HomeAssistantTestEndpoint.Endpoint, operation, parameters, credentials: Credentials, timeoutSeconds: 20));

        [Fact]
        public async Task A_test_names_the_home_and_a_wrong_token_is_refused()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), HomeAssistantTestEndpoint.Endpoint, credentials: Credentials, timeoutSeconds: 20)));
            Assert.True(OutboundConnectGuard.IsDenial(denied)); // loopback is a private host: refused until allowlisted
            var right = await new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(Egress, HomeAssistantTestEndpoint.Endpoint, credentials: Credentials, timeoutSeconds: 20));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.Equal((true, "Home Assistant 2026.9.4: Kafe."), (right.Success, right.Message));
            var wrong = await new HomeAssistantConnector().TestConnectionAsync(Contexts.Test(Egress, HomeAssistantTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["token"] = "not-the-token" }, timeoutSeconds: 20));
            output.WriteLine($"[live] {wrong.Success}: {wrong.Message}");
            Assert.False(wrong.Success);
            Assert.StartsWith("Home Assistant answered HTTP 401", wrong.Message, StringComparison.Ordinal);

            Assert.Equal("valid", Assert.Single(await Run("check_config", new())).Fields["result"]);
            Assert.Contains("input_boolean", (string)Assert.Single(await Run("config", new())).Fields["components"]!, StringComparison.Ordinal);
            Assert.Contains(await Run("services", new()), service => (string?)service.Fields["domain"] == "input_boolean");
        }

        [Fact]
        public async Task A_sensors_state_is_set_kept_listed_and_its_history_read()
        {
            var sensor = "sensor.kassa_queue_" + Guid.NewGuid().ToString("N")[..8];
            var made = Assert.Single(await Run("set_state", new() { ["entity"] = sensor, ["state"] = "3", ["attributes"] = "{\"unit_of_measurement\": \"people\", \"friendly_name\": \"Kassa queue\"}" })).Fields;
            Assert.Equal((sensor, "3"), ((string?)made["entity_id"], (string?)made["state"]));
            var again = Assert.Single(await Run("set_state", new() { ["entity"] = sensor, ["state"] = "4" })).Fields;
            Assert.Equal("4", again["state"]);
            Assert.Equal("people", JsonNode.Parse((string)again["attributes"]!)!["unit_of_measurement"]!.GetValue<string>()); // none given: kept, as n8n keeps them

            var read = Assert.Single(await Run("state", new() { ["entity"] = sensor })).Fields;
            Assert.Equal(("4", "Kassa queue"), ((string?)read["state"], JsonNode.Parse((string)read["attributes"]!)!["friendly_name"]!.GetValue<string>()));
            Assert.Contains(await Run("states", new()), state => (string?)state.Fields["entity_id"] == sensor);

            // The recorder commits a moment after a change: read until both states are there, or ten seconds have passed.
            var history = new List<ConnectorRecord>();
            for (var attempt = 0; attempt < 20 && history.Count < 2; attempt++)
            {
                await Task.Delay(attempt == 0 ? 0 : 500);
                history = await Run("history", new() { ["entities"] = sensor, ["from"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o") });
            }

            output.WriteLine($"[live] history: {string.Join(", ", history.Select(state => state.Fields["state"]))}");
            Assert.Equal(new[] { "3", "4" }, history.Select(state => (string)state.Fields["state"]!));
            Assert.All(history, state => Assert.Equal(sensor, state.Fields["entity_id"]));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("state", new() { ["entity"] = "sensor.nope_" + Guid.NewGuid().ToString("N")[..8] }));
            Assert.Equal("Home Assistant answered HTTP 404: Entity not found.", missing.Message);
        }

        [Fact]
        public async Task A_service_switches_an_entity_and_events_templates_and_logs_answer()
        {
            var before = (string)Assert.Single(await Run("state", new() { ["entity"] = "input_boolean.kassa_open" })).Fields["state"]!;
            var target = before == "on" ? "off" : "on";
            var changed = await Run("call_service", new() { ["domain"] = "input_boolean", ["service"] = "turn_" + target, ["data"] = "{\"entity_id\": \"input_boolean.kassa_open\"}" });
            Assert.Equal(("input_boolean.kassa_open", target), ((string?)changed.Single().Fields["entity_id"], (string?)changed.Single().Fields["state"]));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("call_service", new() { ["domain"] = "input_boolean", ["service"] = "toggle", ["data"] = "{\"entity_id\": \"input_boolean.kassa_open\"}", ["response"] = "true" }));
            output.WriteLine($"[live] {refused.Message}");
            Assert.Equal("Home Assistant answered HTTP 400: Service does not support responses. Remove return_response from request.", refused.Message);

            Assert.Equal("Event kassa_opened fired.", Assert.Single(await Run("fire_event", new() { ["event"] = "kassa_opened", ["data"] = "{\"till\": 2}" })).Fields["message"]);
            Assert.Equal($"Kassa: {target}", Assert.Single(await Run("render_template", new() { ["query"] = "Kassa: {{ states('input_boolean.kassa_open') }}" })).Fields["renderedTemplate"]);

            var entries = new List<ConnectorRecord>();
            for (var attempt = 0; attempt < 20 && !entries.Any(entry => (string?)entry.Fields.GetValueOrDefault("state") == target && (string?)entry.Fields.GetValueOrDefault("context_service") == "turn_" + target); attempt++)
            {
                await Task.Delay(attempt == 0 ? 0 : 500); // the recorder commits a moment after the change
                entries = await Run("logbook", new() { ["from"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o"), ["entity"] = "input_boolean.kassa_open" });
            }

            output.WriteLine($"[live] logbook: {string.Join(" | ", entries.Select(entry => string.Join(",", entry.Fields.Select(field => field.Key + "=" + field.Value))))}");
            Assert.Contains(entries, entry => (string?)entry.Fields.GetValueOrDefault("state") == target && (string?)entry.Fields.GetValueOrDefault("context_service") == "turn_" + target);
            Assert.NotNull(Assert.Single(await Run("error_log", new())).Fields["errorLog"]);
            var camera = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new HomeAssistantConnector(), Contexts.Extract(Egress, HomeAssistantTestEndpoint.Endpoint, "camera_snapshot", new Dictionary<string, string> { ["entity"] = "camera.nope" }, credentials: Credentials, timeoutSeconds: 20) with
            {
                Files = PipelineConnectorFiles.Under(Path.Combine(Path.GetTempPath(), "stomme-homeassistant-" + Guid.NewGuid().ToString("N")), 1 << 20),
            }));
            Assert.Equal("Home Assistant answered HTTP 404: 404: Not Found", camera.Message);
        }
    }
}
