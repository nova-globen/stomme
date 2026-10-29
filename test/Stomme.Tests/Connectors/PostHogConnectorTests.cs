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
using Stomme.Modules.Connectors.PostHog;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// PostHog (n8n's PostHog node) without a server: every event, id, time and JSON held to PostHog's shape before anything
    /// is sent; the project key in each body's api_key, never a header; an event with its properties and time, several in one call,
    /// a person identified with what is set and set once, an alias, a $pageview at an address with a title and a screen - each
    /// through /batch/, a record per event; a person's feature flags; PostHog's answer said on a failure; a test that only a known
    /// key passes.
    /// </summary>
    public sealed class PostHogConnectorTests
    {
        private const string Host = "https://eu.i.posthog.com";
        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "phc_kafe" };

        private sealed record Sent(string Method, string Url, string? Authorization, JsonObject Body);

        private sealed class PostHog : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{\"status\":\"Ok\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), (JsonObject)JsonNode.Parse(body)!);
                Requests.Add(sent);
                var (status, answer) = Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(PostHog posthog, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new PostHogConnector(), Contexts.Extract(new StubEgress(posthog), "", operation, parameters, new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_posthog_node()
        {
            IConnector connector = new PostHogConnector();
            Assert.Equal(("posthog", "PostHog", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "event", "events", "identify", "alias", "page", "screen", "flags" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var posthog = new PostHog();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(posthog, operation, parameters));
                Assert.Empty(posthog.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("PostHog has no operation 'group' - event, events, identify", await Refused("group", new()), StringComparison.Ordinal);
            Assert.Equal("Give the person's distinct id (distinct_id).", await Refused("event", new() { ["event"] = "order placed" }));
            Assert.Equal("A distinct id is 1 to 200 characters (distinct_id).", await Refused("event", new() { ["event"] = "order placed", ["distinct_id"] = new string('a', 201) }));
            Assert.Equal("Give the event's name (event).", await Refused("event", new() { ["distinct_id"] = "user-42" }));
            Assert.Equal("An event's name is 1 to 200 characters (event).", await Refused("event", new() { ["event"] = new string('e', 201), ["distinct_id"] = "user-42" }));
            Assert.Equal("The properties are a JSON object - {\"plan\": \"pro\"} (properties).", await Refused("event", new() { ["event"] = "order placed", ["distinct_id"] = "user-42", ["properties"] = "[1]" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-18T09:30:00Z (timestamp).", await Refused("event", new() { ["event"] = "order placed", ["distinct_id"] = "user-42", ["timestamp"] = "yesterday" }));
            Assert.Equal("The set_once are a JSON object - {\"plan\": \"pro\"} (set_once).", await Refused("identify", new() { ["distinct_id"] = "user-42", ["set_once"] = "plan" }));
            Assert.Equal("Give the other id (alias).", await Refused("alias", new() { ["distinct_id"] = "user-42" }));
            Assert.Equal("'kafe.example/meny' is not a page's address - https://kafe.example/meny (url).", await Refused("page", new() { ["distinct_id"] = "user-42", ["url"] = "kafe.example/meny" }));
            Assert.Equal("'javascript:alert(1)' is not a page's address - https://kafe.example/meny (url).", await Refused("page", new() { ["distinct_id"] = "user-42", ["url"] = "javascript:alert(1)" }));
            Assert.Equal("Give the screen's name (name).", await Refused("screen", new() { ["distinct_id"] = "user-42" }));
            foreach (var events in new[] { "", "{}", "[]", "[1]", "[{\"event\": \"x\"}]", "[{\"event\": \"x\", \"distinct_id\": \"u\", \"properties\": [1]}]" })
            {
                Assert.Contains("(events)", await Refused("events", new() { ["events"] = events }), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Events_identities_aliases_pages_and_screens_go_through_batch_a_record_each()
        {
            var posthog = new PostHog();
            var captured = Assert.Single(await Run(posthog, "event", new() { ["event"] = "order placed", ["distinct_id"] = "user-42", ["properties"] = "{\"total\": 129.5, \"till\": 4}", ["timestamp"] = "2026-10-18T11:30:00+02:00" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["event"] = "order placed", ["distinct_id"] = "user-42", ["captured"] = true }, captured);
            var sent = posthog.Requests.Single();
            Assert.Equal(("POST", Host + "/batch/", (string?)null), (sent.Method, sent.Url, sent.Authorization)); // the key in the body alone
            Assert.Equal("{\"batch\":[{\"event\":\"order placed\",\"distinct_id\":\"user-42\",\"properties\":{\"total\":129.5,\"till\":4},\"timestamp\":\"2026-10-18T09:30:00.000Z\"}],\"api_key\":\"phc_kafe\"}", sent.Body.ToJsonString());

            posthog.Requests.Clear();
            var several = await Run(posthog, "events", new() { ["events"] = "[{\"event\": \"till opened\", \"distinct_id\": \"till-4\"}, {\"event\": \"till closed\", \"distinct_id\": \"till-4\", \"properties\": {\"cash\": 812}, \"timestamp\": \"2026-10-18T18:00:00Z\"}]" });
            Assert.Equal(new[] { "till opened", "till closed" }, several.Select(record => (string?)record.Fields["event"]));
            Assert.Equal("[{\"event\":\"till opened\",\"distinct_id\":\"till-4\",\"properties\":{}},{\"event\":\"till closed\",\"distinct_id\":\"till-4\",\"properties\":{\"cash\":812},\"timestamp\":\"2026-10-18T18:00:00.000Z\"}]", posthog.Requests.Single().Body["batch"]!.ToJsonString()); // one call

            posthog.Requests.Clear();
            await Run(posthog, "identify", new() { ["distinct_id"] = "user-42", ["properties"] = "{\"email\": \"ada@kafe.example\"}", ["set_once"] = "{\"first_till\": 4}" });
            await Run(posthog, "identify", new() { ["distinct_id"] = "user-43" });
            await Run(posthog, "alias", new() { ["distinct_id"] = "user-42", ["alias"] = "ada@kafe.example" });
            await Run(posthog, "page", new() { ["distinct_id"] = "user-42", ["url"] = "https://kafe.example/meny", ["name"] = "Menyn", ["properties"] = "{\"campaign\": \"höst\"}" });
            await Run(posthog, "screen", new() { ["distinct_id"] = "user-42", ["name"] = "Kassa" });
            Assert.Equal(new[]
            {
                "{\"event\":\"$identify\",\"distinct_id\":\"user-42\",\"properties\":{\"$set\":{\"email\":\"ada@kafe.example\"},\"$set_once\":{\"first_till\":4}}}",
                "{\"event\":\"$identify\",\"distinct_id\":\"user-43\",\"properties\":{\"$set\":{}}}",
                "{\"event\":\"$create_alias\",\"distinct_id\":\"user-42\",\"properties\":{\"alias\":\"ada@kafe.example\"}}",
                "{\"event\":\"$pageview\",\"distinct_id\":\"user-42\",\"properties\":{\"campaign\":\"höst\",\"$current_url\":\"https://kafe.example/meny\",\"title\":\"Menyn\"}}",
                "{\"event\":\"$screen\",\"distinct_id\":\"user-42\",\"properties\":{\"$screen_name\":\"Kassa\"}}",
            }, posthog.Requests.Select(request => request.Body["batch"]![0]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
            Assert.All(posthog.Requests, request => Assert.Equal(("phc_kafe", Host + "/batch/"), (request.Body["api_key"]!.GetValue<string>(), request.Url)));
        }

        [Fact]
        public async Task A_persons_flags_are_read_a_test_needs_a_known_key_and_failures_are_said()
        {
            var posthog = new PostHog { Answer = _ => (HttpStatusCode.OK, "{\"flags\":{\"new-menu\":{\"key\":\"new-menu\",\"enabled\":true,\"variant\":\"b\",\"reason\":{\"code\":\"condition_match\"}},\"dark-till\":{\"key\":\"dark-till\",\"enabled\":false,\"reason\":{\"code\":\"no_condition_match\"}}},\"errorsWhileComputingFlags\":false}") };
            var flags = await Run(posthog, "flags", new() { ["distinct_id"] = "user-42" });
            Assert.Equal(new Dictionary<string, object?> { ["distinct_id"] = "user-42", ["flag"] = "new-menu", ["enabled"] = true, ["variant"] = "b", ["reason"] = "condition_match" }, flags[0].Fields);
            Assert.Equal((false, (string?)null), ((bool)flags[1].Fields["enabled"]!, (string?)flags[1].Fields["variant"]));
            Assert.Equal((Host + "/flags?v=2", "{\"distinct_id\":\"user-42\",\"api_key\":\"phc_kafe\"}"), (posthog.Requests[0].Url, posthog.Requests[0].Body.ToJsonString()));

            var tested = await new PostHogConnector().TestConnectionAsync(Contexts.Test(new StubEgress(posthog), "https://us.i.posthog.com", credentials: Credentials));
            Assert.Equal((true, "PostHog answers: the key's project has 2 feature flags."), (tested.Success, tested.Message));
            Assert.Equal("https://us.i.posthog.com/flags?v=2", posthog.Requests.Last().Url);

            var refusing = new PostHog { Answer = _ => (HttpStatusCode.Unauthorized, "{\"type\":\"authentication_error\",\"code\":\"invalid_api_key\",\"detail\":\"Project API key invalid. You can find your project API key in PostHog project settings.\",\"attr\":null}") };
            var bad = await new PostHogConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refusing), "", credentials: Credentials));
            Assert.Equal((false, "PostHog answered HTTP 401 invalid_api_key: Project API key invalid. You can find your project API key in PostHog project settings. - check the project API key, and that the address is its project's region."), (bad.Success, bad.Message));
            Assert.StartsWith("PostHog answered HTTP 401 invalid_api_key", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "event", new() { ["event"] = "x", ["distinct_id"] = "u" }))).Message, StringComparison.Ordinal);
            var nowhere = await new PostHogConnector().TestConnectionAsync(Contexts.Test(new StubEgress(posthog), "https://eu.i.posthog.com/?project=1", credentials: Credentials));
            Assert.Equal((false, "'https://eu.i.posthog.com/?project=1' is not PostHog's address - its ingestion host, https://eu.i.posthog.com."), (nowhere.Success, nowhere.Message));
        }
    }
}
