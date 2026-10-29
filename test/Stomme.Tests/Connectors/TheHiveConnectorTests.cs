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
using Stomme.Modules.Connectors.TheHive;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// TheHive 5 (n8n's TheHive 5 node) without a server: every id, level, date, choice and length held to TheHive's
    /// shape before anything is sent; the key as a bearer token and in no address; alerts and cases searched through TheHive's
    /// query API page by page, newest first, by title, status, severity, tag, source, type and assignee; a case by id or number;
    /// alerts raised, changed, promoted, merged and deleted; comments, tasks, task logs and observables on a case or an alert;
    /// TheHive's 204 as a record of what was done; TheHive's message and the field it refused on a failure; a test naming the key.
    /// </summary>
    public sealed class TheHiveConnectorTests
    {
        private const string Address = "https://thehive.kafe.example";

        private const string Api = Address + "/api/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "thehive-kafe-key" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Hive : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Hive(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private const string Alert = "{\"_id\":\"~4309136\",\"_type\":\"Alert\",\"_createdBy\":\"kafe@kafe.example\",\"_createdAt\":1792627696465,\"type\":\"phishing\",\"source\":\"kassa\",\"sourceRef\":\"kafe-1\",\"title\":\"Phishing against the till\",\"severity\":2,\"severityLabel\":\"MEDIUM\",\"tags\":[\"kafe\",\"phishing\"],\"tlp\":2,\"follow\":true,\"observableCount\":1,\"status\":\"New\"}";

        private const string Case = "{\"_id\":\"~8310832\",\"_type\":\"Case\",\"number\":1,\"title\":\"Phishing against the till\",\"severity\":3,\"status\":\"New\",\"assignee\":\"kafe@kafe.example\",\"userPermissions\":[\"manageCase/create\"]}";

        private static Task<List<ConnectorRecord>> Run(Hive hive, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new TheHiveConnector(), Contexts.Extract(new StubEgress(hive), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Alerts(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(n => $"{{\"_id\":\"~{n}\",\"_type\":\"Alert\",\"title\":\"A{n}\"}}")) + "]";

        [Fact]
        public void It_declares_itself_as_n8ns_thehive_5_node()
        {
            IConnector connector = new TheHiveConnector();
            Assert.Equal(("thehive", "TheHive", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(28, ((IConnectorExtractor)connector).Operations.Count);
            Assert.Contains("alert_merge", ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_thehives_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var hive = new Hive(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(hive, operation, parameters, endpoint));
                Assert.Empty(hive.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.StartsWith("TheHive has no operation 'incident' - alerts, alert,", await Refused("incident", new()));
            Assert.Equal("Give an alert - its id, ~4309136 (alert).", await Refused("alert", new()));
            Assert.Equal("'4309136' is not an alert's id - ~4309136 (alert).", await Refused("alert", new() { ["alert"] = "4309136" }));
            Assert.Equal("'~1/../2' is not an alert's id - ~4309136 (alert).", await Refused("alert_delete", new() { ["alert"] = "~1/../2" }));
            Assert.Equal("Give the alert's type - phishing (type).", await Refused("alert_create", new() { ["title"] = "t", ["description"] = "d" }));
            Assert.Equal("Give the alert's source reference, unique for its type and source - kafe-2026-10-22-1 (source_ref).", await Refused("alert_create", new() { ["type"] = "phishing", ["source"] = "kassa", ["title"] = "t", ["description"] = "d" }));
            Assert.Equal("Give the alert's description (description).", await Refused("alert_create", new() { ["type"] = "phishing", ["source"] = "kassa", ["source_ref"] = "r", ["title"] = "t" }));
            Assert.Equal("'5' is not 1-4 or LOW, MEDIUM, HIGH, CRITICAL (severity).", await Refused("alert_create", new() { ["type"] = "p", ["source"] = "k", ["source_ref"] = "r", ["title"] = "t", ["description"] = "d", ["severity"] = "5" }));
            Assert.Equal("'PURPLE' is not 0-4 or CLEAR, GREEN, AMBER, AMBER+STRICT, RED (tlp).", await Refused("alert_create", new() { ["type"] = "p", ["source"] = "k", ["source_ref"] = "r", ["title"] = "t", ["description"] = "d", ["tlp"] = "PURPLE" }));
            Assert.Equal("'4' is not 0-3 or CLEAR, GREEN, AMBER, RED (pap).", await Refused("case_create", new() { ["title"] = "t", ["description"] = "d", ["pap"] = "4" }));
            Assert.Equal("'yesterday' is not a date - 2026-10-22T08:15:00Z (date).", await Refused("alert_create", new() { ["type"] = "p", ["source"] = "k", ["source_ref"] = "r", ["title"] = "t", ["description"] = "d", ["date"] = "yesterday" }));
            Assert.Equal("A tag is at most 128 characters, on one line (tags).", await Refused("case_create", new() { ["title"] = "t", ["description"] = "d", ["tags"] = new string('k', 129) }));
            Assert.Equal("'Phishing\nagainst' is more than 512 characters or more than one line (title).", await Refused("case_create", new() { ["title"] = "Phishing\nagainst", ["description"] = "d" }));
            Assert.Equal("'ada' is not a login - ada@kafe.example (assignee).", await Refused("case_create", new() { ["title"] = "t", ["description"] = "d", ["assignee"] = "ada" }));
            Assert.Equal("'ja' is not true or false (flag).", await Refused("case_create", new() { ["title"] = "t", ["description"] = "d", ["flag"] = "ja" }));
            Assert.Equal("Give something to change on an alert.", await Refused("alert_update", new() { ["alert"] = "~1" }));
            Assert.Equal("'Closed!' is not a status - New, InProgress, Ignored (status).", await Refused("alert_update", new() { ["alert"] = "~1", ["status"] = "Closed!" }));
            Assert.Equal("Give the case - its id (~4415736) or its number (2) (case).", await Refused("case", new()));
            Assert.Equal("'#0' is not a case's id or number - ~4415736 or 2 (case).", await Refused("case", new() { ["case"] = "#0" }));
            Assert.Equal("'2b' is not a case's id or number - ~4415736 or 2 (case).", await Refused("alert_merge", new() { ["alert"] = "~1", ["case"] = "2b" }));
            Assert.Equal("'Solved' is not NoImpact, WithImpact or NotApplicable (impact_status).", await Refused("case_update", new() { ["case"] = "2", ["impact_status"] = "Solved" }));
            Assert.Equal("'ticket' is not case or alert (on).", await Refused("comments", new() { ["on"] = "ticket", ["id"] = "2" }));
            Assert.Equal("Give the comment (message).", await Refused("comment_add", new() { ["id"] = "2" }));
            Assert.Equal("'Done' is not Waiting, InProgress, Completed or Cancel (status).", await Refused("task_update", new() { ["task"] = "~1", ["status"] = "Done" }));
            Assert.Equal("Give the observable's data type - ip, domain, url (data_type).", await Refused("observable_create", new() { ["id"] = "2", ["data"] = "203.0.113.7" }));
            Assert.Equal("'IP Address' is not a data type - ip, domain, url (data_type).", await Refused("observable_create", new() { ["id"] = "2", ["data_type"] = "IP Address", ["data"] = "203.0.113.7" }));
            Assert.Equal("An observable has no severity - its TLP, PAP, IOC and sighted say how it is handled (severity).", await Refused("observable_update", new() { ["observable"] = "~1", ["severity"] = "2" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("alerts", new() { ["max"] = "0" }));
            Assert.Equal("Give TheHive's address - https://thehive.kafe.example.", await Refused("alerts", new(), ""));
            Assert.Equal("'https://thehive.kafe.example/?org=kafe' is not TheHive's address - https://thehive.kafe.example, with no query.", await Refused("alerts", new(), "https://thehive.kafe.example/?org=kafe"));
            Assert.Equal("The TheHive connection has no API key.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TheHiveConnector(), Contexts.Extract(new StubEgress(new Hive(_ => (HttpStatusCode.OK, "{}"))), Address, "alerts", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task An_alert_is_raised_with_its_levels_by_name_or_number_and_the_key_as_a_bearer_token()
        {
            var hive = new Hive(_ => (HttpStatusCode.Created, Alert));
            var made = Assert.Single(await Run(hive, "alert_create", new()
            {
                ["type"] = "phishing", ["source"] = "kassa", ["source_ref"] = "kafe-1", ["title"] = "Phishing against the till", ["description"] = "Mails to the **till** staff",
                ["severity"] = "medium", ["tlp"] = "white", ["pap"] = "3", ["tags"] = "phishing, kafé, phishing", ["date"] = "2026-10-22T08:15:00Z",
            }, Api)).Fields;
            var sent = Assert.Single(hive.Requests);
            Assert.Equal(("POST", Api + "/alert", "Bearer thehive-kafe-key"), (sent.Method, sent.Url, sent.Authorization));
            Assert.Equal("{\"type\":\"phishing\",\"source\":\"kassa\",\"sourceRef\":\"kafe-1\",\"title\":\"Phishing against the till\",\"description\":\"Mails to the **till** staff\",\"severity\":2,\"tlp\":0,\"pap\":3,\"tags\":[\"phishing\",\"kafé\"],\"date\":1792656900000}", sent.Body);
            Assert.Equal(("~4309136", "Alert", "phishing", "New", 2L, "[\"kafe\",\"phishing\"]"), ((string?)made["id"], (string?)made["kind"], (string?)made["type"], (string?)made["status"], (long?)made["severity"], (string?)made["tags"]));
            Assert.Equal("kafe@kafe.example", (string?)made["createdBy"]); // TheHive's _ fields without their underscore
            Assert.DoesNotContain(made.Keys, key => key.StartsWith('_'));

            // A description over several lines comes from the step's Query box when the field is not given.
            hive.Requests.Clear();
            await Run(hive, "case_create", new() { ["title"] = "Kassa", ["query"] = "Mails to the till staff\nat 08:00" });
            Assert.Equal("{\"title\":\"Kassa\",\"description\":\"Mails to the till staff\\nat 08:00\"}", hive.Requests[0].Body);
        }

        [Fact]
        public async Task A_change_or_a_delete_answers_what_was_done_and_alerts_are_promoted_and_merged()
        {
            var hive = new Hive(sent => sent.Method is "PATCH" or "DELETE" ? (HttpStatusCode.NoContent, string.Empty) : (HttpStatusCode.Created, Case));
            var changed = Assert.Single(await Run(hive, "alert_update", new() { ["alert"] = "~4309136", ["severity"] = "HIGH", ["status"] = "Ignored", ["tags"] = "kafe" })).Fields;
            Assert.Equal(("~4309136", "alert_update"), ((string?)changed["id"], (string?)changed["done"]));
            Assert.Equal(("PATCH", Api + "/alert/~4309136", "{\"severity\":3,\"tags\":[\"kafe\"],\"status\":\"Ignored\"}"), (hive.Requests[0].Method, hive.Requests[0].Url, hive.Requests[0].Body));

            var promoted = Assert.Single(await Run(hive, "alert_promote", new() { ["alert"] = "~4309136" })).Fields;
            Assert.Equal(("POST", Api + "/alert/~4309136/case", "{}"), (hive.Requests[1].Method, hive.Requests[1].Url, hive.Requests[1].Body));
            Assert.Equal(("~8310832", 1L), ((string?)promoted["id"], (long?)promoted["number"]));
            Assert.False(promoted.ContainsKey("userPermissions")); // the key's own permissions are not the case's data

            await Run(hive, "alert_merge", new() { ["alert"] = "~4309136", ["case"] = "#2" });
            Assert.Equal(Api + "/alert/~4309136/merge/2", hive.Requests[2].Url);
            await Run(hive, "case_delete", new() { ["case"] = "~8310832" });
            Assert.Equal(("DELETE", Api + "/case/~8310832"), (hive.Requests[3].Method, hive.Requests[3].Url));
            await Run(hive, "case_update", new() { ["case"] = "2", ["status"] = "TruePositive", ["summary"] = "Blocked", ["impact_status"] = "noimpact", ["flag"] = "true" });
            Assert.Equal(("PATCH", Api + "/case/2", "{\"status\":\"TruePositive\",\"summary\":\"Blocked\",\"impactStatus\":\"NoImpact\",\"flag\":true}"), (hive.Requests[4].Method, hive.Requests[4].Url, hive.Requests[4].Body));
        }

        [Fact]
        public async Task Alerts_are_searched_newest_first_page_by_page_by_their_filters_up_to_the_most()
        {
            var hive = new Hive(sent =>
            {
                var page = JsonNode.Parse(sent.Body!)!["query"]!.AsArray().Last()!;
                var from = (int)page["from"]!;
                var to = (int)page["to"]!;
                return (HttpStatusCode.OK, Alerts(from + 1, Math.Max(0, Math.Min(to, 120) - from))); // 120 in all, as many as the page asks
            });
            var found = await Run(hive, "alerts", new() { ["title"] = "Phishing", ["status"] = "New", ["severity"] = "critical", ["tag"] = "kafe", ["source"] = "kassa", ["type"] = "phishing", ["max"] = "150" });
            Assert.Equal(120, found.Count);
            Assert.Equal(("~1", "A1", "~120"), ((string?)found[0].Fields["id"], (string?)found[0].Fields["title"], (string?)found[^1].Fields["id"]));
            Assert.Equal(new[] { Api + "/query?name=alerts", Api + "/query?name=alerts" }, hive.Requests.Select(sent => sent.Url));
            Assert.Equal("{\"query\":[{\"_name\":\"listAlert\"},{\"_name\":\"filter\",\"_and\":[{\"_like\":{\"_field\":\"title\",\"_value\":\"*Phishing*\"}},{\"_eq\":{\"_field\":\"status\",\"_value\":\"New\"}},{\"_eq\":{\"_field\":\"severity\",\"_value\":4}},{\"_eq\":{\"_field\":\"tags\",\"_value\":\"kafe\"}},{\"_eq\":{\"_field\":\"source\",\"_value\":\"kassa\"}},{\"_eq\":{\"_field\":\"type\",\"_value\":\"phishing\"}}]},{\"_name\":\"sort\",\"_fields\":[{\"_createdAt\":\"desc\"}]},{\"_name\":\"page\",\"from\":0,\"to\":100}]}", hive.Requests[0].Body);
            Assert.EndsWith("{\"_name\":\"page\",\"from\":100,\"to\":150}]}", hive.Requests[1].Body);

            hive.Requests.Clear();
            Assert.Equal(3, (await Run(hive, "cases", new() { ["assignee"] = "Ada@Kafe.example", ["max"] = "3" })).Count);
            Assert.Equal("{\"query\":[{\"_name\":\"listCase\"},{\"_name\":\"filter\",\"_eq\":{\"_field\":\"assignee\",\"_value\":\"ada@kafe.example\"}},{\"_name\":\"sort\",\"_fields\":[{\"_createdAt\":\"desc\"}]},{\"_name\":\"page\",\"from\":0,\"to\":3}]}", hive.Requests[0].Body);
            Assert.Equal(Api + "/query?name=cases", hive.Requests[0].Url);
        }

        [Fact]
        public async Task Comments_tasks_logs_and_observables_go_to_their_case_or_alert()
        {
            var hive = new Hive(sent => sent.Url.EndsWith("/observable", StringComparison.Ordinal)
                ? (HttpStatusCode.Created, "[{\"_id\":\"~4153448\",\"_type\":\"Observable\",\"dataType\":\"domain\",\"data\":\"kassa.example\",\"ioc\":true}]")
                : sent.Url.EndsWith("/query", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[{\"_id\":\"~4034728\",\"_type\":\"Comment\",\"message\":\"Kaffet är klart.\"}]")
                : (HttpStatusCode.Created, "{\"_id\":\"~4038824\",\"_type\":\"Task\",\"title\":\"Block the sender\",\"status\":\"Waiting\"}"));

            await Run(hive, "comment_add", new() { ["on"] = "Alert", ["id"] = "~4309136", ["message"] = "Seen it" });
            Assert.Equal(("POST", Api + "/alert/~4309136/comment", "{\"message\":\"Seen it\"}"), (hive.Requests[0].Method, hive.Requests[0].Url, hive.Requests[0].Body));
            var comments = await Run(hive, "comments", new() { ["id"] = "2" });
            Assert.Equal("Kaffet är klart.", (string?)Assert.Single(comments).Fields["message"]);
            Assert.Equal("{\"query\":[{\"_name\":\"getCase\",\"idOrName\":\"2\"},{\"_name\":\"comments\"},{\"_name\":\"sort\",\"_fields\":[{\"_createdAt\":\"desc\"}]},{\"_name\":\"page\",\"from\":0,\"to\":50}]}", hive.Requests[1].Body);

            await Run(hive, "task_create", new() { ["case"] = "~8310832", ["title"] = "Block the sender", ["description"] = "At the gateway", ["group"] = "mail", ["due"] = "2026-10-23T12:00:00+02:00", ["mandatory"] = "true" });
            Assert.Equal((Api + "/case/~8310832/task", "{\"title\":\"Block the sender\",\"description\":\"At the gateway\",\"group\":\"mail\",\"dueDate\":1792749600000,\"mandatory\":true}"), (hive.Requests[2].Url, hive.Requests[2].Body));
            await Run(hive, "task_log_add", new() { ["task"] = "~8323120", ["message"] = "Blocked" });
            Assert.Equal(Api + "/task/~8323120/log", hive.Requests[3].Url);
            await Run(hive, "task_logs", new() { ["task"] = "~8323120", ["max"] = "5" });
            Assert.Equal("{\"query\":[{\"_name\":\"getTask\",\"idOrName\":\"~8323120\"},{\"_name\":\"logs\"},{\"_name\":\"sort\",\"_fields\":[{\"_createdAt\":\"desc\"}]},{\"_name\":\"page\",\"from\":0,\"to\":5}]}", hive.Requests[4].Body);

            var observable = Assert.Single(await Run(hive, "observable_create", new() { ["on"] = "case", ["id"] = "2", ["data_type"] = "domain", ["data"] = "kassa.example", ["message"] = "in the mail", ["ioc"] = "true", ["tlp"] = "green" })).Fields;
            Assert.Equal((Api + "/case/2/observable", "{\"dataType\":\"domain\",\"data\":\"kassa.example\",\"message\":\"in the mail\",\"ioc\":true,\"tlp\":1}"), (hive.Requests[5].Url, hive.Requests[5].Body));
            Assert.Equal(("~4153448", true), ((string?)observable["id"], (bool?)observable["ioc"]));
            await Run(hive, "observables", new() { ["on"] = "alert", ["id"] = "~4309136", ["data_type"] = "ip" });
            Assert.Equal("{\"query\":[{\"_name\":\"getAlert\",\"idOrName\":\"~4309136\"},{\"_name\":\"observables\"},{\"_name\":\"filter\",\"_eq\":{\"_field\":\"dataType\",\"_value\":\"ip\"}},{\"_name\":\"page\",\"from\":0,\"to\":50}]}", hive.Requests[6].Body);
        }

        [Fact]
        public async Task TheHives_message_and_the_field_it_refused_are_said()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "alert", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Hive(_ => (status, body)), operation, parameters ?? new() { ["alert"] = "~1" }))).Message;

            Assert.Equal("TheHive answered HTTP 400: Invalid json: severity - error.expected.validenumvalue.", await Failed(HttpStatusCode.BadRequest, "{\"type\":\"BadRequest\",\"message\":\"Invalid json\",\"cause\":\"JsResultException\",\"errors\":[{\"path\":\".severity\",\"error\":\"error.expected.validenumvalue\"}]}"));
            Assert.Equal("TheHive answered HTTP 404: Alert not found.", await Failed(HttpStatusCode.NotFound, "{\"type\":\"NotFoundError\",\"message\":\"Alert not found\"}"));
            Assert.Equal("TheHive answered HTTP 401: Authentication failure. - check the API key.", await Failed(HttpStatusCode.Unauthorized, "{\"type\":\"AuthenticationError\",\"message\":\"Authentication failure\"}"));
            Assert.Equal("TheHive answered HTTP 403. - the key's user may not do this; check its profile.", await Failed(HttpStatusCode.Forbidden, string.Empty));
            Assert.Equal("TheHive answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Sign in</html>"));
            Assert.Equal("TheHive answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"_id\":\"~1\"}", "alerts", new()));
            Assert.Equal("TheHive answered with nothing - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, string.Empty));
        }

        [Fact]
        public async Task A_test_names_the_keys_user_and_its_organisation()
        {
            var connector = new TheHiveConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Hive(_ => (HttpStatusCode.OK, "{\"_id\":\"~4050976\",\"login\":\"kafe@kafe.example\",\"organisation\":\"kafe\"}"))), Address, null, Credentials));
            Assert.True(ok.Success);
            var html = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Hive(_ => (HttpStatusCode.OK, "<html>Sign in</html>"))), Address, null, Credentials));
            Assert.False(html.Success);
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Hive(_ => (HttpStatusCode.Unauthorized, "{\"message\":\"Authentication failure\"}"))), Address, null, Credentials));
            Assert.Equal("TheHive answered HTTP 401: Authentication failure. - check the API key.", refused.Message);
        }
    }

    /// <summary>
    /// TheHive LIVE, against this machine's throwaway TheHive 5.5 (scripts/thehive-test-server.ps1) - fail-not-skip without
    /// it: an alert raised, found by its source, changed, commented and given an observable, promoted to a case; the case read
    /// by its number, given a task, the task a log, changed and closed with a summary; another alert merged into it; everything
    /// read back as TheHive keeps it, and deleted again; a wrong key refused with TheHive's own words.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class TheHiveConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Key => new() { ["api-key"] = TheHiveTestEndpoint.ApiKey };

        private static HostConnectorEgress Egress => new(new[] { new Uri(TheHiveTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new TheHiveConnector(), Contexts.Extract(Egress, TheHiveTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Key, timeoutSeconds: 60));

        [Fact]
        public async Task A_test_names_its_user_and_refuses_a_wrong_key()
        {
            var tested = await new TheHiveConnector().TestConnectionAsync(Contexts.Test(Egress, TheHiveTestEndpoint.Endpoint, credentials: Key, timeoutSeconds: 60));
            Assert.True(tested.Success, tested.Message);
            Assert.Equal("TheHive answers - the key is kafe@kafe.example, in kafe.", tested.Message);
            var wrong = await new TheHiveConnector().TestConnectionAsync(Contexts.Test(Egress, TheHiveTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = new string('a', 32) }, timeoutSeconds: 60));
            Assert.Equal((false, "TheHive answered HTTP 401: Authentication failure. - check the API key."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task An_alert_becomes_a_case_with_a_task_a_log_a_comment_and_an_observable_and_everything_is_deleted_again()
        {
            var marker = "kafe-" + Guid.NewGuid().ToString("N")[..10];
            var alert = Assert.Single(await Run("alert_create", new()
            {
                ["type"] = "phishing", ["source"] = marker, ["source_ref"] = marker + "-1", ["title"] = "Phishing against the till", ["query"] = "Mails to the till staff\nat 08:00",
                ["severity"] = "high", ["tlp"] = "green", ["tags"] = "phishing, kafé",
            })).Fields;
            var alertId = (string)alert["id"]!;
            output.WriteLine($"alert {alertId}");
            string? caseId = null;
            string? otherId = null;
            try
            {
                Assert.Equal(("Alert", 3L, 1L, "New", "Mails to the till staff\nat 08:00"), ((string?)alert["kind"], (long?)alert["severity"], (long?)alert["tlp"], (string?)alert["status"], (string?)alert["description"]));
                var found = Assert.Single(await Run("alerts", new() { ["source"] = marker, ["title"] = "till" }));
                Assert.Equal(alertId, (string?)found.Fields["id"]);
                Assert.Equal(("alert_update", alertId), ((string?)Assert.Single(await Run("alert_update", new() { ["alert"] = alertId, ["severity"] = "critical", ["tags"] = "phishing, kafé, kassa" })).Fields["done"], alertId));
                Assert.Equal((4L, "[\"phishing\",\"kafé\",\"kassa\"]"), ((long?)Assert.Single(await Run("alert", new() { ["alert"] = alertId })).Fields["severity"], SortedTags((string?)Assert.Single(await Run("alert", new() { ["alert"] = alertId })).Fields["tags"])));
                Assert.Equal("Seen it", (string?)Assert.Single(await Run("comment_add", new() { ["on"] = "alert", ["id"] = alertId, ["message"] = "Seen it" })).Fields["message"]);
                var observable = Assert.Single(await Run("observable_create", new() { ["on"] = "alert", ["id"] = alertId, ["data_type"] = "domain", ["data"] = marker + ".example", ["ioc"] = "true", ["message"] = "in the mail" })).Fields;
                Assert.Equal(("domain", marker + ".example", true), ((string?)observable["dataType"], (string?)observable["data"], (bool?)observable["ioc"]));
                Assert.Equal(marker + ".example", (string?)Assert.Single(await Run("observables", new() { ["on"] = "alert", ["id"] = alertId, ["data_type"] = "domain" })).Fields["data"]);

                var promoted = Assert.Single(await Run("alert_promote", new() { ["alert"] = alertId })).Fields;
                caseId = (string)promoted["id"]!;
                var number = ((long)promoted["number"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(caseId, (string?)Assert.Single(await Run("case", new() { ["case"] = "#" + number })).Fields["id"]);
                Assert.Equal(caseId, (string?)Assert.Single(await Run("cases", new() { ["title"] = "Phishing", ["tag"] = "kassa", ["max"] = "5" }).ContinueWith(task => task.Result.Where(record => (string?)record.Fields["id"] == caseId).ToList())).Fields["id"]);

                var task = Assert.Single(await Run("task_create", new() { ["case"] = caseId, ["title"] = "Block the sender", ["description"] = "At the gateway", ["group"] = "mail" })).Fields;
                var taskId = (string)task["id"]!;
                Assert.Equal(("Block the sender", "Waiting", "mail"), ((string?)task["title"], (string?)task["status"], (string?)task["group"]));
                Assert.Equal("Blocked at the gateway", (string?)Assert.Single(await Run("task_log_add", new() { ["task"] = taskId, ["message"] = "Blocked at the gateway" })).Fields["message"]);
                Assert.Equal("Blocked at the gateway", (string?)Assert.Single(await Run("task_logs", new() { ["task"] = taskId })).Fields["message"]);
                await Run("task_update", new() { ["task"] = taskId, ["status"] = "completed", ["flag"] = "true" });
                Assert.Equal(("Completed", true), ((string?)Assert.Single(await Run("task", new() { ["task"] = taskId })).Fields["status"], (bool?)Assert.Single(await Run("task", new() { ["task"] = taskId })).Fields["flag"]));
                Assert.Contains(await Run("tasks", new() { ["case"] = caseId }), record => (string?)record.Fields["id"] == taskId);

                var comment = Assert.Single(await Run("comment_add", new() { ["id"] = caseId, ["message"] = "Kaffet är klart." })).Fields;
                await Run("comment_update", new() { ["comment"] = (string)comment["id"]!, ["message"] = "Kaffet är klart - och kassan." });
                Assert.Equal("Kaffet är klart - och kassan.", (string?)Assert.Single(await Run("comments", new() { ["id"] = caseId })).Fields["message"]);
                await Run("comment_delete", new() { ["comment"] = (string)comment["id"]! });
                Assert.Empty(await Run("comments", new() { ["id"] = caseId }));

                var other = Assert.Single(await Run("alert_create", new() { ["type"] = "phishing", ["source"] = marker, ["source_ref"] = marker + "-2", ["title"] = "Another mail", ["description"] = "The same sender" })).Fields;
                otherId = (string)other["id"]!;
                Assert.Equal(caseId, (string?)Assert.Single(await Run("alert_merge", new() { ["alert"] = otherId, ["case"] = number })).Fields["id"]);
                otherId = null; // merged: it went with the case

                await Run("case_update", new() { ["case"] = caseId, ["status"] = "TruePositive", ["summary"] = "Blocked at the gateway", ["impact_status"] = "NoImpact" });
                var closed = Assert.Single(await Run("case", new() { ["case"] = caseId })).Fields;
                Assert.Equal(("TruePositive", "Blocked at the gateway", "NoImpact"), ((string?)closed["status"], (string?)closed["summary"], (string?)closed["impactStatus"]));

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("alert_update", new() { ["alert"] = alertId, ["status"] = "Bogus" }));
                Assert.Equal("TheHive answered HTTP 404: AlertStatus Bogus not found.", refused.Message);
            }
            finally
            {
                if (caseId is not null)
                {
                    await Run("case_delete", new() { ["case"] = caseId });
                }

                if (otherId is not null)
                {
                    await Run("alert_delete", new() { ["alert"] = otherId });
                }

                await Run("alert_delete", new() { ["alert"] = alertId });
            }

            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("alert", new() { ["alert"] = alertId }));
            Assert.Equal("TheHive answered HTTP 404: Alert not found.", gone.Message);
        }

        private static string? SortedTags(string? tags) => tags is null ? null : "[" + string.Join(",", System.Text.Json.Nodes.JsonNode.Parse(tags)!.AsArray().Select(tag => "\"" + tag!.GetValue<string>() + "\"").OrderBy(tag => tag switch { "\"phishing\"" => 0, "\"kafé\"" => 1, _ => 2 })) + "]";
    }
}