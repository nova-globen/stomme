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
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Misp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - MISP (n8n's MISP node) without a server: every id, choice, colour, date, flag and length held to MISP's shape
    /// before anything is sent; the key in MISP's Authorization header and in no address; events and attributes listed and
    /// searched page by page with the page the same size each time, an event searched without its attributes; an event made,
    /// changed with only what is given, published, tagged and deleted; an attribute of any MISP type made on an event; tags
    /// made, changed and deleted; MISP's message and field errors on a failure; a test asking MISP for its version.
    /// </summary>
    public sealed class MispConnectorTests
    {
        private const string Api = "https://misp.kafe.example";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "misp-kafe-key-0123456789abcdefghijklmnopq" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Misp : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Misp(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var key) ? key.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private const string Event = "{\"Event\":{\"id\":\"1\",\"info\":\"Kafé phishing wave\",\"threat_level_id\":\"2\",\"published\":false,\"uuid\":\"860f9db4-de88-415e-a5d0-353fca45f865\",\"attribute_count\":\"2\",\"distribution\":\"0\",\"Org\":{\"id\":\"1\",\"name\":\"Kafe\"},\"Attribute\":[{\"id\":\"1\",\"type\":\"ip-dst\",\"value\":\"203.0.113.7\"}],\"Tag\":[{\"id\":\"75\",\"name\":\"kafe:phishing\"}]}}";

        private const string Attribute = "{\"Attribute\":{\"id\":\"7\",\"event_id\":\"1\",\"category\":\"Network activity\",\"type\":\"ip-dst\",\"to_ids\":true,\"value\":\"203.0.113.7\",\"comment\":\"C2\"},\"AttributeTag\":[]}";

        private static Task<List<ConnectorRecord>> Run(Misp misp, string operation, Dictionary<string, string> parameters, string endpoint = Api) =>
            Contexts.DrainAsync(new MispConnector(), Contexts.Extract(new StubEgress(misp), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Events(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(n => $"{{\"id\":\"{n}\",\"info\":\"Event {n}\",\"EventTag\":[]}}")) + "]";

        private static string Attributes(int from, int count) => "{\"response\":{\"Attribute\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => $"{{\"id\":\"{n}\",\"type\":\"domain\",\"value\":\"k{n}.example\",\"Event\":{{\"id\":\"1\"}}}}")) + "]}}";

        [Fact]
        public void It_declares_itself_as_n8ns_misp_node()
        {
            IConnector connector = new MispConnector();
            Assert.Equal(("misp", "MISP", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[]
            {
                "events", "event", "event_create", "event_update", "event_delete", "event_publish", "event_unpublish", "event_search", "event_tag_add", "event_tag_remove",
                "attributes", "attribute", "attribute_create", "attribute_update", "attribute_delete", "attribute_search", "tags", "tag_create", "tag_update", "tag_delete",
            }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            string[] Fields(string operation) => ((IConnectorExtractor)connector).Operations.Single(declared => declared.Name == operation).Parameters!.Fields.Select(field => field.Name).ToArray();
            Assert.Equal(new[] { "value", "searchall", "tags", "type", "category", "published", "to_ids", "deleted", "last", "timestamp", "max" }, Fields("event_search")); // every filter the search takes, offered
            Assert.Equal(new[] { "value", "tags", "type", "category", "published", "to_ids", "deleted", "last", "timestamp", "max" }, Fields("attribute_search"));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_misps_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Api)
            {
                var misp = new Misp(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(misp, operation, parameters, endpoint));
                Assert.Empty(misp.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("MISP has no operation 'feeds' - events, event, event_create,", await Refused("feeds", new()), StringComparison.Ordinal);
            Assert.Equal("Give an event - its id or UUID (event).", await Refused("event", new()));
            Assert.Equal("'#42' is not an event's id or UUID (event).", await Refused("event_delete", new() { ["event"] = "#42" }));
            Assert.Equal("'../1' is not an event's id or UUID (event).", await Refused("event_publish", new() { ["event"] = "../1" }));
            Assert.Equal("Give what the event is (info).", await Refused("event_create", new() { ["distribution"] = "0" }));
            Assert.Equal("An event's info is at most 65535 characters - this is 65536 (info).", await Refused("event_create", new() { ["info"] = new string('k', 65536) }));
            Assert.Equal("'5' is not organisation (0), community (1), connected (2), all (3) or sharing_group (4) (distribution).", await Refused("event_create", new() { ["info"] = "Kassan", ["distribution"] = "5" }));
            Assert.Equal("'everyone' is not organisation (0), community (1), connected (2), all (3) or sharing_group (4) (distribution).", await Refused("event_create", new() { ["info"] = "Kassan", ["distribution"] = "everyone" }));
            Assert.Equal("'critical' is not high (1), medium (2), low (3) or undefined (4) (threat_level).", await Refused("event_create", new() { ["info"] = "Kassan", ["threat_level"] = "critical" }));
            Assert.Equal("'3' is not initial (0), ongoing (1) or complete (2) (analysis).", await Refused("event_create", new() { ["info"] = "Kassan", ["analysis"] = "3" }));
            Assert.Equal("Give the sharing group - its id (sharing_group).", await Refused("event_create", new() { ["info"] = "Kassan", ["distribution"] = "sharing_group" }));
            Assert.Equal("A sharing group is for a distribution to one - give distribution = sharing_group (sharing_group).", await Refused("event_create", new() { ["info"] = "Kassan", ["sharing_group"] = "3" }));
            Assert.Equal("'kafe' is not a sharing group's id (sharing_group).", await Refused("event_create", new() { ["info"] = "Kassan", ["distribution"] = "4", ["sharing_group"] = "kafe" }));
            Assert.Equal("'18/10/2026' is not a day - 2026-10-18 (date).", await Refused("event_create", new() { ["info"] = "Kassan", ["date"] = "18/10/2026" }));
            Assert.Equal("'Kafe' is not an organisation's id or UUID (organisation).", await Refused("event_create", new() { ["info"] = "Kassan", ["organisation"] = "Kafe" }));
            Assert.Equal("Give what to change - info, distribution, threat level, analysis, sharing group or date.", await Refused("event_update", new() { ["event"] = "1" }));
            Assert.Equal("Give the tag - its id, 75 (tag).", await Refused("event_tag_add", new() { ["event"] = "1" }));
            Assert.Equal("'kafe:phishing' is not a tag's id - 75 (tag).", await Refused("event_tag_remove", new() { ["event"] = "1", ["tag"] = "kafe:phishing" }));
            Assert.Equal("Give the attribute's type - ip-dst, domain, url, sha256 (type).", await Refused("attribute_create", new() { ["event"] = "1", ["value"] = "203.0.113.7" }));
            Assert.Equal("'ip dst' is not a MISP type - ip-dst, domain, url, sha256 (type).", await Refused("attribute_create", new() { ["event"] = "1", ["type"] = "ip dst", ["value"] = "203.0.113.7" }));
            Assert.Equal("Give the attribute's value - 203.0.113.7 (value).", await Refused("attribute_create", new() { ["event"] = "1", ["type"] = "ip-dst" }));
            Assert.Equal("'ja' is not true or false (to_ids).", await Refused("attribute_create", new() { ["event"] = "1", ["type"] = "ip-dst", ["value"] = "203.0.113.7", ["to_ids"] = "ja" }));
            Assert.Equal("A category is at most 255 characters (category).", await Refused("attribute_create", new() { ["event"] = "1", ["type"] = "ip-dst", ["value"] = "203.0.113.7", ["category"] = new string('k', 256) }));
            Assert.Equal("'6' is not organisation (0), community (1), connected (2), all (3), sharing_group (4) or inherit (5) (distribution).", await Refused("attribute_create", new() { ["event"] = "1", ["type"] = "ip-dst", ["value"] = "203.0.113.7", ["distribution"] = "6" }));
            Assert.Equal("Give an event - its id or UUID (event).", await Refused("attribute_create", new() { ["type"] = "ip-dst", ["value"] = "203.0.113.7" }));
            Assert.Equal("Give what to change - value, type, category, IDS flag, comment, distribution or sharing group.", await Refused("attribute_update", new() { ["attribute"] = "7" }));
            Assert.Equal("Give an attribute - its id or UUID (attribute).", await Refused("attribute_update", new() { ["comment"] = "C2" }));
            Assert.Equal("The search is MISP's own JSON, an object - {\"value\": \"203.0.113.7\"} (query).", await Refused("event_search", new() { ["query"] = "[\"203.0.113.7\"]" }));
            Assert.Equal("'ip dst' is not a MISP type - ip-dst, domain, url, sha256 (type).", await Refused("attribute_search", new() { ["type"] = "ip dst" }));
            Assert.Equal("Anywhere is an event search's - an attribute is searched by its value (searchall).", await Refused("attribute_search", new() { ["searchall"] = "phishing" }));
            Assert.Equal("'yes' is not true or false (published).", await Refused("event_search", new() { ["published"] = "yes" }));
            Assert.Equal("'1' is not true or false (to_ids).", await Refused("attribute_search", new() { ["to_ids"] = "1" }));
            Assert.Equal("'yesterday' is not a time - 1d, 12h, 30m (last).", await Refused("event_search", new() { ["last"] = "yesterday" }));
            Assert.Equal("'7 days' is not a time - 1d, 12h, 30m (timestamp).", await Refused("attribute_search", new() { ["timestamp"] = "7 days" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("events", new() { ["max"] = "0" }));
            Assert.Equal("Give the tag's name - kafe:phishing (name).", await Refused("tag_create", new() { ["colour"] = "#ff0000" }));
            Assert.Equal("A tag's name is at most 255 characters, on one line (name).", await Refused("tag_create", new() { ["name"] = "kafe:\nphishing" }));
            Assert.Equal("'red' is not a colour - #ff0000 (colour).", await Refused("tag_create", new() { ["name"] = "kafe:phishing", ["colour"] = "red" }));
            Assert.Equal("'nej' is not true or false (exportable).", await Refused("tag_create", new() { ["name"] = "kafe:phishing", ["exportable"] = "nej" }));
            Assert.Equal("Give what to change - name or colour.", await Refused("tag_update", new() { ["tag"] = "75" }));
            Assert.Equal("'x' is not a tag's id - 75 (tag).", await Refused("tag_delete", new() { ["tag"] = "x" }));
            Assert.Equal("Give MISP's address - https://misp.kafe.example.", await Refused("events", new(), endpoint: ""));
            Assert.Equal("'https://misp.kafe.example/?x=1' is not MISP's address - https://misp.kafe.example, with no query.", await Refused("events", new(), endpoint: "https://misp.kafe.example/?x=1"));
        }

        [Fact]
        public async Task Events_and_attributes_are_listed_and_searched_page_by_page_the_page_the_same_size_each_time()
        {
            var misp = new Misp(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/events/index", StringComparison.Ordinal)
                ? JsonNode.Parse(sent.Body!)!["page"]!.GetValue<int>() switch { 1 => Events(1, 100), 2 => Events(101, 100), _ => Events(201, 30) }
                : Attributes(1, JsonNode.Parse(sent.Body!)!["page"]!.GetValue<int>() == 1 ? 3 : 0)));
            var events = await Run(misp, "events", new() { ["max"] = "150" });
            Assert.Equal(150, events.Count);
            Assert.Equal(new[] { ("POST", Api + "/events/index", "{\"limit\":100,\"page\":1}"), ("POST", Api + "/events/index", "{\"limit\":100,\"page\":2}") }, misp.Requests.Select(sent => (sent.Method, sent.Url, sent.Body)));
            Assert.All(misp.Requests, sent => Assert.Equal("misp-kafe-key-0123456789abcdefghijklmnopq", sent.Authorization));
            Assert.Equal(("101", "Event 101", "[]"), ((string?)events[100].Fields["id"], (string?)events[100].Fields["info"], (string?)events[100].Fields["EventTag"]));

            misp.Requests.Clear();
            var attributes = await Run(misp, "attributes", new() { ["max"] = "20" });
            Assert.Equal(3, attributes.Count); // a page short of its size is the last
            Assert.Equal("{\"returnFormat\":\"json\",\"limit\":20,\"page\":1}", Assert.Single(misp.Requests).Body);
            Assert.Equal(("domain", "k1.example", "{\"id\":\"1\"}"), ((string?)attributes[0].Fields["type"], (string?)attributes[0].Fields["value"], (string?)attributes[0].Fields["Event"]));

            misp.Requests.Clear();
            var none = new Misp(_ => (HttpStatusCode.OK, "{\"response\":[]}"));
            Assert.Empty(await Run(none, "event_search", new() { ["value"] = "203.0.113.7" }));
            Assert.Empty(await Run(none, "attribute_search", new() { ["value"] = "203.0.113.7" }));
        }

        [Fact]
        public async Task A_search_is_its_fields_or_misps_own_json_and_an_event_comes_without_its_attributes()
        {
            var misp = new Misp(sent => (HttpStatusCode.OK, sent.Url.EndsWith("/events/restSearch", StringComparison.Ordinal)
                ? "{\"response\":[" + Event + "]}"
                : Attributes(1, 1)));
            var found = Assert.Single(await Run(misp, "event_search", new()
            {
                ["value"] = "203.0.113.7", ["searchall"] = "phishing", ["tags"] = "tlp:green, kafe:phishing", ["type"] = "ip-dst", ["category"] = "Network activity",
                ["published"] = "False", ["deleted"] = "false", ["last"] = "7d", ["timestamp"] = "12h", ["max"] = "5",
            })).Fields;
            Assert.Equal(("1", "Kafé phishing wave", false), ((string?)found["id"], (string?)found["info"], found.ContainsKey("Attribute"))); // its attributes left out, as n8n's node does
            Assert.Equal("[{\"id\":\"75\",\"name\":\"kafe:phishing\"}]", (string?)found["Tag"]);
            await Run(misp, "attribute_search", new() { ["value"] = "%.kafe.example", ["to_ids"] = "true", ["published"] = "true" });
            await Run(misp, "event_search", new() { ["searchall"] = "kassa%" });
            await Run(misp, "event_search", new() { ["query"] = "{\n  \"value\": \"203.0.113.7\",\n  \"limit\": 1000,\n  \"returnFormat\": \"csv\",\n  \"enforceWarninglist\": true\n}" });
            Assert.Equal(new[]
            {
                "{\"value\":\"203.0.113.7\",\"type\":\"ip-dst\",\"category\":\"Network activity\",\"searchall\":\"%phishing%\",\"tags\":[\"tlp:green\",\"kafe:phishing\"],\"published\":false,\"deleted\":false,\"last\":\"7d\",\"timestamp\":\"12h\",\"returnFormat\":\"json\",\"metadata\":true,\"limit\":5,\"page\":1}",
                "{\"value\":\"%.kafe.example\",\"published\":true,\"to_ids\":true,\"returnFormat\":\"json\",\"limit\":50,\"page\":1}",
                "{\"searchall\":\"kassa%\",\"returnFormat\":\"json\",\"metadata\":true,\"limit\":50,\"page\":1}", // MISP's own % as given
                "{\"value\":\"203.0.113.7\",\"enforceWarninglist\":true,\"returnFormat\":\"json\",\"metadata\":true,\"limit\":50,\"page\":1}", // its own paging and format the connector's
            }, misp.Requests.Select(sent => sent.Body));
        }

        [Fact]
        public async Task An_event_is_made_changed_published_tagged_and_deleted()
        {
            var misp = new Misp(sent => (HttpStatusCode.OK, sent.Url switch
            {
                var url when url.Contains("Tag", StringComparison.Ordinal) => "{\"saved\":true,\"success\":\"Tag added.\",\"check_publish\":true}",
                var url when url.Contains("/events/add", StringComparison.Ordinal) || url.Contains("/events/edit/", StringComparison.Ordinal) || url.Contains("/events/view/", StringComparison.Ordinal) => Event,
                _ => "{\"saved\":true,\"success\":true,\"name\":\"Job queued\",\"message\":\"Job queued\",\"url\":\"/events/publish/1\",\"id\":\"1\"}",
            }));
            var made = Assert.Single(await Run(misp, "event_create", new()
            {
                ["info"] = "Kafé phishing wave", ["distribution"] = "community", ["threat_level"] = "Medium", ["analysis"] = "ongoing", ["date"] = "2026-10-18",
                ["organisation"] = "7753AC23-5149-4BB5-8387-A09CA426FCB3",
            })).Fields;
            Assert.Equal(("1", "Kafé phishing wave", false), ((string?)made["id"], (string?)made["info"], made.ContainsKey("Attribute")));
            await Run(misp, "event_create", new() { ["info"] = "Kassan", ["distribution"] = "4", ["sharing_group"] = "3" });
            await Run(misp, "event_update", new() { ["event"] = "860f9db4-de88-415e-a5d0-353fca45f865", ["threat_level"] = "1", ["info"] = "Kafé phishing wave, updated" });
            await Run(misp, "event", new() { ["event"] = "1" });
            var published = Assert.Single(await Run(misp, "event_publish", new() { ["event"] = "1" })).Fields;
            Assert.Equal(("Job queued", true), ((string?)published["message"], (bool?)published["success"]));
            await Run(misp, "event_unpublish", new() { ["event"] = "1" });
            var tagged = Assert.Single(await Run(misp, "event_tag_add", new() { ["event"] = "1", ["tag"] = "75" })).Fields;
            Assert.Equal((true, "Tag added."), ((bool?)tagged["saved"], (string?)tagged["success"]));
            await Run(misp, "event_tag_remove", new() { ["event"] = "1", ["tag"] = "75" });
            await Run(misp, "event_delete", new() { ["event"] = "1" });
            Assert.Equal(new[]
            {
                ("POST", "/events/add", "{\"info\":\"Kafé phishing wave\",\"distribution\":\"1\",\"threat_level_id\":\"2\",\"analysis\":\"1\",\"date\":\"2026-10-18\",\"orgc_id\":\"7753ac23-5149-4bb5-8387-a09ca426fcb3\"}"),
                ("POST", "/events/add", "{\"info\":\"Kassan\",\"distribution\":\"4\",\"sharing_group_id\":\"3\"}"),
                ("PUT", "/events/edit/860f9db4-de88-415e-a5d0-353fca45f865", "{\"info\":\"Kafé phishing wave, updated\",\"threat_level_id\":\"1\"}"),
                ("GET", "/events/view/1", (string?)null),
                ("POST", "/events/publish/1", null),
                ("POST", "/events/unpublish/1", null),
                ("POST", "/events/addTag", "{\"event\":\"1\",\"tag\":\"75\"}"),
                ("POST", "/events/removeTag/1/75", null),
                ("DELETE", "/events/delete/1", null),
            }, misp.Requests.Select(sent => (sent.Method, sent.Url[Api.Length..], sent.Body)));
        }

        [Fact]
        public async Task Attributes_of_any_type_and_tags_are_made_changed_and_deleted()
        {
            var misp = new Misp(sent => (HttpStatusCode.OK, sent.Url switch
            {
                var url when url.Contains("/attributes/delete/", StringComparison.Ordinal) => "{\"message\":\"Attribute deleted.\"}",
                var url when url.Contains("/attributes/", StringComparison.Ordinal) => Attribute,
                var url when url.EndsWith("/tags", StringComparison.Ordinal) => "{\"Tag\":[" + string.Join(",", Enumerable.Range(1, 60).Select(n => $"{{\"id\":\"{n}\",\"name\":\"tag{n}\",\"colour\":\"#001fc2\",\"count\":0}}")) + "]}",
                var url when url.Contains("/tags/delete/", StringComparison.Ordinal) => "{\"saved\":true,\"success\":true,\"name\":\"Tag deleted.\",\"message\":\"Tag deleted.\",\"id\":75}",
                _ => "{\"Tag\":{\"id\":\"75\",\"name\":\"kafe:phishing\",\"colour\":\"#ff0000\",\"exportable\":true}}",
            }));
            var made = Assert.Single(await Run(misp, "attribute_create", new()
            {
                ["event"] = "1", ["type"] = "ip-dst|port", ["value"] = "203.0.113.7|443", ["category"] = "Network activity", ["to_ids"] = "True", ["comment"] = "C2", ["distribution"] = "inherit",
            })).Fields;
            Assert.Equal(("7", "ip-dst", true, "C2"), ((string?)made["id"], (string?)made["type"], (bool?)made["to_ids"], (string?)made["comment"]));
            await Run(misp, "attribute_create", new() { ["event"] = "1", ["type"] = "AS", ["value"] = "AS64500" });
            await Run(misp, "attribute_update", new() { ["attribute"] = "7", ["comment"] = "C2 server", ["to_ids"] = "false" });
            await Run(misp, "attribute", new() { ["attribute"] = "5a9ea1f3-321c-462e-bc47-28d71920f06d" });
            Assert.Equal("Attribute deleted.", (string?)Assert.Single(await Run(misp, "attribute_delete", new() { ["attribute"] = "7" })).Fields["message"]);
            Assert.Equal(50, (await Run(misp, "tags", new())).Count); // n8n's limit
            Assert.Equal(60, (await Run(misp, "tags", new() { ["max"] = "1000" })).Count);
            var tag = Assert.Single(await Run(misp, "tag_create", new() { ["name"] = "kafe:phishing", ["colour"] = "FF0000", ["exportable"] = "false" })).Fields;
            Assert.Equal(("75", "#ff0000"), ((string?)tag["id"], (string?)tag["colour"]));
            await Run(misp, "tag_update", new() { ["tag"] = "75", ["colour"] = "#00FF00" });
            await Run(misp, "tag_delete", new() { ["tag"] = "75" });
            Assert.Equal(new[]
            {
                ("POST", "/attributes/add/1", "{\"type\":\"ip-dst|port\",\"value\":\"203.0.113.7|443\",\"category\":\"Network activity\",\"to_ids\":true,\"comment\":\"C2\",\"distribution\":\"5\"}"),
                ("POST", "/attributes/add/1", "{\"type\":\"AS\",\"value\":\"AS64500\"}"),
                ("PUT", "/attributes/edit/7", "{\"to_ids\":false,\"comment\":\"C2 server\"}"),
                ("GET", "/attributes/view/5a9ea1f3-321c-462e-bc47-28d71920f06d", (string?)null),
                ("DELETE", "/attributes/delete/7", null),
                ("GET", "/tags", null),
                ("GET", "/tags", null),
                ("POST", "/tags/add", "{\"name\":\"kafe:phishing\",\"colour\":\"#ff0000\",\"exportable\":false}"),
                ("POST", "/tags/edit/75", "{\"colour\":\"#00ff00\"}"),
                ("POST", "/tags/delete/75", null),
            }, misp.Requests.Select(sent => (sent.Method, sent.Url[Api.Length..], sent.Body)));
        }

        [Fact]
        public async Task A_failure_is_misps_message_and_its_field_errors()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "event", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Misp(_ => (status, body)), operation, parameters ?? new() { ["event"] = "1" }))).Message;

            Assert.Equal("MISP answered HTTP 403: Authentication failed. Please make sure you pass the API key of an API enabled user along in the Authorization header. - check the authentication key, and that its user may use the API.",
                await Failed(HttpStatusCode.Forbidden, "{\"name\":\"Authentication failed. Please make sure you pass the API key of an API enabled user along in the Authorization header.\",\"message\":\"Authentication failed. Please make sure you pass the API key of an API enabled user along in the Authorization header.\",\"url\":\"/servers/getVersion\"}"));
            Assert.Equal("MISP answered HTTP 403: Could not add Attribute: type - Invalid type.; value - Value is empty.", await Failed(HttpStatusCode.Forbidden,
                "{\"saved\":false,\"name\":\"Could not add Attribute\",\"message\":\"Could not add Attribute\",\"url\":\"/attributes/add\",\"errors\":{\"type\":[\"Invalid type.\"],\"value\":[\"Value is empty.\"]}}",
                "attribute_create", new() { ["event"] = "1", ["type"] = "ip-dst", ["value"] = "x" }));
            Assert.Equal("MISP answered HTTP 404: Invalid event.", await Failed(HttpStatusCode.NotFound, "{\"name\":\"Invalid event\",\"message\":\"Invalid event\",\"url\":\"/events/view/999999\"}"));
            Assert.Equal("MISP answered HTTP 405: Invalid attribute.", await Failed(HttpStatusCode.MethodNotAllowed, "{\"name\":\"Invalid attribute\",\"message\":\"Invalid attribute\"}", "attribute", new() { ["attribute"] = "999999" }));
            Assert.Equal("MISP answered HTTP 500.", await Failed(HttpStatusCode.InternalServerError, "<html>fel</html>"));
            Assert.Equal("MISP answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Login</body></html>"));
            Assert.Equal("MISP answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]"));
            Assert.Equal("MISP answered without its list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{}", "events", new()));
            Assert.Equal("MISP answered without its list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"response\":{}}", "attribute_search", new()));
            Assert.Equal("MISP answered without its tags - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]", "tags", new()));
        }

        [Fact]
        public async Task A_test_asks_misp_for_its_version()
        {
            var misp = new Misp(_ => (HttpStatusCode.OK, "{\"version\":\"2.5.47\",\"perm_sync\":true}"));
            var tested = await new MispConnector().TestConnectionAsync(Contexts.Test(new StubEgress(misp), Api + "/", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "MISP 2.5.47 answers."), (tested.Success, tested.Message));
            Assert.Equal(("GET", Api + "/servers/getVersion", "misp-kafe-key-0123456789abcdefghijklmnopq"), (misp.Requests.Single().Method, misp.Requests.Single().Url, misp.Requests.Single().Authorization));
            var underPath = new Misp(_ => (HttpStatusCode.OK, "{\"version\":\"2.5.47\"}"));
            await new MispConnector().TestConnectionAsync(Contexts.Test(new StubEgress(underPath), "https://kafe.example/misp", new Dictionary<string, string>(), Credentials));
            Assert.Equal("https://kafe.example/misp/servers/getVersion", underPath.Requests.Single().Url); // a MISP under a path of its host

            async Task<ConnectionTestResult> Test(HttpStatusCode status, string body) =>
                await new MispConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Misp(_ => (status, body))), Api, new Dictionary<string, string>(), Credentials));

            var nobody = await Test(HttpStatusCode.OK, "{}");
            Assert.Equal((false, "MISP answered without its version - check the address: a sign-in page or a proxy answers so."), (nobody.Success, nobody.Message));
            Assert.False((await Test(HttpStatusCode.OK, "<html>Login</html>")).Success);
            Assert.False((await Test(HttpStatusCode.Forbidden, "{\"message\":\"Authentication failed.\"}")).Success);
        }
    }

    /// <summary>
    /// BL-229 - MISP LIVE, against this machine's throwaway MISP 2.5 (scripts/misp-test-server.ps1) - fail-not-skip without it
    /// (D46): an event made, an attribute of its own type put on it, both searched for and found, the event tagged and untagged,
    /// published and unpublished, changed, read without its attributes, and everything deleted again; a wrong key refused with
    /// MISP's own words.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MispConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Key => new() { ["api-key"] = MispTestEndpoint.ApiKey };

        private static HostConnectorEgress Egress => new(new[] { new Uri(MispTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new MispConnector(), Contexts.Extract(Egress, MispTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Key, timeoutSeconds: 60));

        [Fact]
        public async Task A_test_takes_its_key_and_refuses_a_wrong_one()
        {
            var tested = await new MispConnector().TestConnectionAsync(Contexts.Test(Egress, MispTestEndpoint.Endpoint, credentials: Key, timeoutSeconds: 60));
            Assert.True(tested.Success, tested.Message);
            Assert.StartsWith("MISP 2.5.", tested.Message, StringComparison.Ordinal);
            var wrong = await new MispConnector().TestConnectionAsync(Contexts.Test(Egress, MispTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = new string('a', 40) }, timeoutSeconds: 60));
            Assert.Equal((false, "MISP answered HTTP 403: Authentication failed. Please make sure you pass the API key of an API enabled user along in the Authorization header. - check the authentication key, and that its user may use the API."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task An_event_and_its_attribute_are_made_found_tagged_published_changed_and_deleted()
        {
            var marker = "kafe-" + Guid.NewGuid().ToString("N")[..10];
            var made = Assert.Single(await Run("event_create", new() { ["info"] = $"Phishing wave {marker}", ["distribution"] = "organisation", ["threat_level"] = "medium", ["analysis"] = "ongoing" })).Fields;
            var eventId = (string)made["id"]!;
            output.WriteLine($"event {eventId}");
            string? tagId = null;
            try
            {
                Assert.Equal(("2", "1", "0", false), ((string?)made["threat_level_id"], (string?)made["analysis"], (string?)made["distribution"], (bool?)made["published"]));
                var attribute = Assert.Single(await Run("attribute_create", new() { ["event"] = eventId, ["type"] = "domain", ["value"] = $"{marker}.example", ["category"] = "Network activity", ["to_ids"] = "true", ["comment"] = "C2" })).Fields;
                var attributeId = (string)attribute["id"]!;
                Assert.Equal(("domain", $"{marker}.example", true), ((string?)attribute["type"], (string?)attribute["value"], (bool?)attribute["to_ids"]));
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("attribute_create", new() { ["event"] = eventId, ["type"] = "no-such-type", ["value"] = "x" }));
                Assert.Equal("MISP answered HTTP 403: Could not add Attribute: type - Invalid type.", refused.Message);

                var foundAttribute = Assert.Single(await Run("attribute_search", new() { ["value"] = $"{marker}.example" })).Fields;
                Assert.Equal((attributeId, eventId), ((string?)foundAttribute["id"], (string?)foundAttribute["event_id"]));
                var foundEvent = Assert.Single(await Run("event_search", new() { ["value"] = $"{marker}.example" })).Fields;
                Assert.Equal((eventId, false), ((string?)foundEvent["id"], foundEvent.ContainsKey("Attribute")));
                Assert.Equal(eventId, (string?)Assert.Single(await Run("event_search", new() { ["query"] = $"{{\"eventinfo\": \"%{marker}%\"}}" })).Fields["id"]);
                Assert.Equal(eventId, (string?)Assert.Single(await Run("event_search", new() { ["searchall"] = marker })).Fields["id"]); // anywhere, MISP's %s added
                Assert.Equal(eventId, (string?)Assert.Single(await Run("event_search", new() { ["searchall"] = marker, ["timestamp"] = "1h", ["published"] = "false" })).Fields["id"]);
                Assert.Contains(await Run("events", new() { ["max"] = "1000" }), record => (string?)record.Fields["id"] == eventId);

                var tag = Assert.Single(await Run("tag_create", new() { ["name"] = $"kafe:{marker}", ["colour"] = "ff0000" })).Fields;
                tagId = (string)tag["id"]!;
                Assert.Equal("#ff0000", (string?)tag["colour"]);
                Assert.Equal("#00ff00", (string?)Assert.Single(await Run("tag_update", new() { ["tag"] = tagId, ["colour"] = "#00FF00" })).Fields["colour"]);
                Assert.Contains(await Run("tags", new() { ["max"] = "10000" }), record => (string?)record.Fields["id"] == tagId);
                Assert.Equal(true, (bool?)Assert.Single(await Run("event_tag_add", new() { ["event"] = eventId, ["tag"] = tagId })).Fields["saved"]);
                Assert.Contains($"kafe:{marker}", (string?)Assert.Single(await Run("event", new() { ["event"] = eventId })).Fields["Tag"], StringComparison.Ordinal);
                Assert.Equal("Tag removed.", (string?)Assert.Single(await Run("event_tag_remove", new() { ["event"] = eventId, ["tag"] = tagId })).Fields["success"]);

                // Changed before it is published: MISP publishes in a background job, and a change made right after came back
                // once without its new info (measured).
                var changed = Assert.Single(await Run("event_update", new() { ["event"] = (string)made["uuid"]!, ["info"] = $"Phishing wave {marker}, updated", ["threat_level"] = "high" })).Fields;
                Assert.Equal((eventId, $"Phishing wave {marker}, updated", "1", false), ((string?)changed["id"], (string?)changed["info"], (string?)changed["threat_level_id"], changed.ContainsKey("Attribute"))); // the same event, changed
                Assert.Equal("C2 server", (string?)Assert.Single(await Run("attribute_update", new() { ["attribute"] = attributeId, ["comment"] = "C2 server" })).Fields["comment"]);
                Assert.Equal("Attribute deleted.", (string?)Assert.Single(await Run("attribute_delete", new() { ["attribute"] = attributeId })).Fields["message"]);
                Assert.Equal(true, (bool?)Assert.Single(await Run("event_publish", new() { ["event"] = eventId })).Fields["success"]);
                Assert.Equal("Event unpublished.", (string?)Assert.Single(await Run("event_unpublish", new() { ["event"] = eventId })).Fields["message"]);
            }
            finally
            {
                if (tagId is not null)
                {
                    await Run("tag_delete", new() { ["tag"] = tagId });
                }

                Assert.Equal("Event deleted.", (string?)Assert.Single(await Run("event_delete", new() { ["event"] = eventId })).Fields["message"]);
            }

            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("event", new() { ["event"] = eventId }));
            Assert.Equal("MISP answered HTTP 404: Invalid event.", gone.Message);
        }
    }
}
