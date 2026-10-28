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
using Stomme.Modules.Connectors.PagerDuty;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - PagerDuty (n8n's PagerDuty node: incidents created, got, read and updated, their notes, the log entries, a user):
    /// every call PagerDuty's REST API v2 - or the address given - with the token in PagerDuty's own header and its version
    /// asked for, a write sent from the step's or the connection's e-mail; a list page by page by offset, the limit fixed and no
    /// bigger than asked, PagerDuty's more the end, with its filters as lists, its times in UTC, a sort, what to include and a
    /// time zone; an incident created with every field, updated - acknowledged, resolved, reassigned, escalated - its notes read
    /// and added; a log entry, a user, the users, the services and who is on call; a record its fields, a reference its summary
    /// and id; what could go wrong refused before anything is sent; PagerDuty's code, word and errors on a failure; a test
    /// naming the account's abilities.
    /// </summary>
    public sealed class PagerDutyConnectorTests
    {
        private const string Api = "https://api.pagerduty.com";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "pd-token-1" };

        private sealed class PagerDuty : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Accept, string? From, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString(), request.Headers.TryGetValues("From", out var from) ? string.Join(",", from) : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(PagerDuty pagerDuty, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new PagerDutyConnector(), Contexts.Extract(new StubEgress(pagerDuty), endpoint, operation, parameters, settings: settings ?? new Dictionary<string, string>(), credentials: credentials ?? Credentials));

        private static string Incidents(int from, int count, bool more) =>
            "{\"incidents\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"PINC" + i + "\",\"type\":\"incident\",\"incident_number\":" + i + ",\"title\":\"Kassan nere " + i + "\",\"status\":\"triggered\",\"urgency\":\"high\",\"is_mergeable\":true,\"created_at\":\"2026-10-13T06:00:00Z\",\"service\":{\"id\":\"PSRV1\",\"type\":\"service_reference\",\"summary\":\"Kassa\"},\"assignments\":[{\"assignee\":{\"id\":\"PUSR1\",\"summary\":\"Ada\"}}],\"first_trigger_log_entry\":{\"id\":\"PLOG1\",\"type\":\"trigger_log_entry_reference\"},\"body\":null}")) + "],\"limit\":100,\"offset\":" + (from - 1) + ",\"more\":" + (more ? "true" : "false") + ",\"total\":null}";

        [Fact]
        public async Task Incidents_are_read_page_by_page_filtered_and_each_a_reference_its_summary_and_id()
        {
            var pagerDuty = new PagerDuty { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("offset=100", StringComparison.Ordinal) ? Incidents(101, 50, false) : Incidents(1, 100, true)) };

            var incidents = await Run(pagerDuty, PagerDutyConnector.IncidentsOperation, new() { ["statuses"] = "triggered, acknowledged", ["urgencies"] = "high", ["services"] = "PSRV1,PSRV2", ["teams"] = "PTEAM1", ["users"] = "PUSR1", ["since"] = "2026-10-13T08:00:00+02:00", ["until"] = "2026-10-14", ["incident_key"] = "kassa/1", ["sort"] = "created_at:desc", ["include"] = "assignees, services", ["time_zone"] = "Europe/Stockholm" });
            Assert.Equal(150, incidents.Count); // a thousand at most by default
            const string Query = "statuses[]=triggered&statuses[]=acknowledged&urgencies[]=high&service_ids[]=PSRV1&service_ids[]=PSRV2&team_ids[]=PTEAM1&user_ids[]=PUSR1&include[]=assignees&include[]=services&since=2026-10-13T06%3A00%3A00Z&until=2026-10-14T00%3A00%3A00Z&incident_key=kassa%2F1&sort_by=created_at%3Adesc&time_zone=Europe%2FStockholm";
            Assert.Equal(new[] { $"{Api}/incidents?{Query}&offset=0&limit=100", $"{Api}/incidents?{Query}&offset=100&limit=100" }, pagerDuty.Requests.Select(request => request.Url)); // PagerDuty's more the end
            Assert.All(pagerDuty.Requests, request => Assert.Equal(("Token token=pd-token-1", "application/vnd.pagerduty+json; version=2", (string?)null), (request.Authorization, request.Accept.Replace(";version", "; version", StringComparison.Ordinal), request.From)));
            var first = incidents[0].Fields;
            Assert.Equal(("PINC1", 1L, "Kassan nere 1", "high", true, "Kassa", "PSRV1"), ((string?)first["id"], (long?)first["incident_number"], (string?)first["title"], (string?)first["urgency"], (bool?)first["is_mergeable"], (string?)first["service"], (string?)first["service_id"]));
            Assert.Equal("[{\"assignee\":{\"id\":\"PUSR1\",\"summary\":\"Ada\"}}]", (string?)first["assignments"]); // a list as its JSON
            Assert.Equal("{\"id\":\"PLOG1\",\"type\":\"trigger_log_entry_reference\"}", (string?)first["first_trigger_log_entry"]); // an object with no summary as its JSON
            Assert.False(first.ContainsKey("first_trigger_log_entry_id"));
            Assert.Null(first["body"]);

            Assert.Equal(30, (await Run(pagerDuty, PagerDutyConnector.IncidentsOperation, new() { ["max"] = "30", ["all"] = "true", ["since"] = "2026-10-01" })).Count);
            Assert.Equal($"{Api}/incidents?date_range=all&offset=0&limit=30", pagerDuty.Requests[^1].Url); // every time, its times left out; a page as small as asked
            pagerDuty.Answer = (_, _) => (HttpStatusCode.OK, "{\"incidents\":[],\"more\":true}");
            Assert.Empty(await Run(pagerDuty, PagerDutyConnector.IncidentsOperation, new(), endpoint: "https://api.eu.pagerduty.com/"));
            Assert.Equal("https://api.eu.pagerduty.com/incidents?offset=0&limit=100", pagerDuty.Requests[^1].Url); // the address given; an empty page the end
            pagerDuty.Answer = (_, _) => (HttpStatusCode.OK, "{\"log_entries\":[{\"id\":\"PLOG1\",\"type\":\"trigger_log_entry\",\"summary\":\"Triggered\"}]}");
            Assert.Single(await Run(pagerDuty, PagerDutyConnector.LogEntriesOperation, new() { ["since"] = "2026-10-13T00:00:00Z", ["overview"] = "true", ["include"] = "incidents", ["time_zone"] = "UTC" }));
            Assert.Equal($"{Api}/log_entries?include[]=incidents&since=2026-10-13T00%3A00%3A00Z&is_overview=true&time_zone=UTC&offset=0&limit=100", pagerDuty.Requests[^1].Url); // no more the end
        }

        [Fact]
        public async Task An_incident_is_created_updated_got_and_noted_and_the_rest_read()
        {
            var pagerDuty = new PagerDuty
            {
                Answer = (method, url) => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("/notes", StringComparison.Ordinal) && method == HttpMethod.Post => "{\"note\":{\"id\":\"PNOTE1\",\"content\":\"Kassan är tillbaka\",\"user\":{\"id\":\"PUSR1\",\"type\":\"user_reference\",\"summary\":\"Ada\"}}}",
                    _ when url.Contains("/notes", StringComparison.Ordinal) => "{\"notes\":[{\"id\":\"PNOTE1\",\"content\":\"Kassan är tillbaka\"},{\"id\":\"PNOTE2\",\"content\":\"Fixed\"}]}",
                    _ when url.Contains("/log_entries/", StringComparison.Ordinal) => "{\"log_entry\":{\"id\":\"PLOG1\",\"type\":\"trigger_log_entry\"}}",
                    _ when url.Contains("/users/", StringComparison.Ordinal) => "{\"user\":{\"id\":\"PUSR1\",\"name\":\"Ada\",\"email\":\"ada@example.test\"}}",
                    _ when url.Contains("/users?", StringComparison.Ordinal) => "{\"users\":[{\"id\":\"PUSR1\",\"name\":\"Ada\"}],\"more\":false}",
                    _ when url.Contains("/services?", StringComparison.Ordinal) => "{\"services\":[{\"id\":\"PSRV1\",\"name\":\"Kassa\",\"status\":\"active\"}],\"more\":false}",
                    _ when url.Contains("/oncalls?", StringComparison.Ordinal) => "{\"oncalls\":[{\"escalation_level\":1,\"start\":\"2026-10-13T06:00:00Z\",\"user\":{\"id\":\"PUSR1\",\"type\":\"user_reference\",\"summary\":\"Ada\"},\"schedule\":null}],\"more\":false}",
                    _ => "{\"incident\":{\"id\":\"PINC9\",\"incident_number\":9,\"title\":\"Kassan nere\",\"status\":\"triggered\"}}",
                }),
            };
            (string, string, string?, string?) Last() => (pagerDuty.Requests[^1].Method.Method, pagerDuty.Requests[^1].Url, pagerDuty.Requests[^1].From, pagerDuty.Requests[^1].Body);

            var made = Assert.Single(await Run(pagerDuty, PagerDutyConnector.CreateIncidentOperation, new() { ["title"] = "Kassan nere", ["service"] = "PSRV1", ["urgency"] = "High", ["incident_key"] = "kassa/1", ["details"] = "Café's till is down", ["priority"] = "PPRI1", ["escalation_policy"] = "PESC1", ["assignees"] = "PUSR1, PUSR2", ["conference_number"] = "+46 8 465 004 02", ["conference_url"] = "https://meet.example.test/ops", ["status"] = "resolved", ["escalation_level"] = "2" }, new() { ["from"] = "ops@example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/incidents", (string?)"ops@example.test", (string?)"{\"incident\":{\"type\":\"incident\",\"title\":\"Kassan nere\",\"service\":{\"id\":\"PSRV1\",\"type\":\"service_reference\"},\"urgency\":\"high\",\"incident_key\":\"kassa/1\",\"body\":{\"type\":\"incident_body\",\"details\":\"Café's till is down\"},\"priority\":{\"id\":\"PPRI1\",\"type\":\"priority_reference\"},\"escalation_policy\":{\"id\":\"PESC1\",\"type\":\"escalation_policy_reference\"},\"assignments\":[{\"assignee\":{\"id\":\"PUSR1\",\"type\":\"user_reference\"}},{\"assignee\":{\"id\":\"PUSR2\",\"type\":\"user_reference\"}}],\"conference_bridge\":{\"conference_number\":\"+46 8 465 004 02\",\"conference_url\":\"https://meet.example.test/ops\"}}}"), Last()); // the connection's sender; no status or level on a create
            Assert.Equal(("PINC9", 9L), ((string?)made["id"], (long?)made["incident_number"]));

            await Run(pagerDuty, PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["from"] = "ada@example.test", ["status"] = "Resolved", ["resolution"] = "Restarted", ["title"] = "Kassan nere igen", ["urgency"] = "low", ["escalation_level"] = "2", ["incident_key"] = "x", ["details"] = "x", ["conference_url"] = "https://meet.example.test/ops2" }, new() { ["from"] = "ops@example.test" });
            Assert.Equal(("PUT", $"{Api}/incidents/PINC9", (string?)"ada@example.test", (string?)"{\"incident\":{\"type\":\"incident_reference\",\"status\":\"resolved\",\"resolution\":\"Restarted\",\"title\":\"Kassan nere igen\",\"escalation_level\":2,\"urgency\":\"low\",\"conference_bridge\":{\"conference_url\":\"https://meet.example.test/ops2\"}}}"), Last()); // the step's sender; no key or details on an update

            Assert.Equal("Kassan nere", (string?)Assert.Single(await Run(pagerDuty, PagerDutyConnector.IncidentOperation, new() { ["id"] = "PINC9", ["from"] = "x" })).Fields["title"]);
            Assert.Equal(("GET", $"{Api}/incidents/PINC9", (string?)null, (string?)null), Last()); // no sender on a read
            var note = Assert.Single(await Run(pagerDuty, PagerDutyConnector.CreateNoteOperation, new() { ["id"] = "PINC9", ["content"] = "Kassan är tillbaka" }, new() { ["from"] = "ops@example.test" })).Fields;
            Assert.Equal(("POST", $"{Api}/incidents/PINC9/notes", (string?)"ops@example.test", (string?)"{\"note\":{\"content\":\"Kassan är tillbaka\"}}"), Last());
            Assert.Equal(("PNOTE1", "Ada", "PUSR1"), ((string?)note["id"], (string?)note["user"], (string?)note["user_id"]));
            Assert.Equal(2, (await Run(pagerDuty, PagerDutyConnector.NotesOperation, new() { ["id"] = "PINC9" })).Count);
            Assert.Equal(("GET", $"{Api}/incidents/PINC9/notes"), (Last().Item1, Last().Item2));

            Assert.Equal("trigger_log_entry", (string?)Assert.Single(await Run(pagerDuty, PagerDutyConnector.LogEntryOperation, new() { ["id"] = "PLOG1" })).Fields["type"]);
            Assert.Equal($"{Api}/log_entries/PLOG1", Last().Item2);
            Assert.Equal("ada@example.test", (string?)Assert.Single(await Run(pagerDuty, PagerDutyConnector.UserOperation, new() { ["id"] = "PUSR1" })).Fields["email"]);
            Assert.Equal($"{Api}/users/PUSR1", Last().Item2);
            Assert.Single(await Run(pagerDuty, PagerDutyConnector.UsersOperation, new() { ["query"] = "Ada & L", ["statuses"] = "triggered" }));
            Assert.Equal($"{Api}/users?query=Ada%20%26%20L&offset=0&limit=100", Last().Item2); // the search escaped; no incident filter on users
            Assert.Single(await Run(pagerDuty, PagerDutyConnector.ServicesOperation, new() { ["query"] = "Kassa" }));
            Assert.Equal($"{Api}/services?query=Kassa&offset=0&limit=100", Last().Item2);
            var oncall = Assert.Single(await Run(pagerDuty, PagerDutyConnector.OnCallsOperation, new() { ["schedules"] = "PSCH1", ["escalation_policies"] = "PESC1", ["users"] = "PUSR1", ["since"] = "2026-10-13T00:00:00Z", ["earliest"] = "true", ["query"] = "x" })).Fields;
            Assert.Equal($"{Api}/oncalls?schedule_ids[]=PSCH1&escalation_policy_ids[]=PESC1&user_ids[]=PUSR1&since=2026-10-13T00%3A00%3A00Z&earliest=true&offset=0&limit=100", Last().Item2);
            Assert.Equal((1L, "Ada", "PUSR1"), ((long?)oncall["escalation_level"], (string?)oncall["user"], (string?)oncall["user_id"]));
            Assert.Null(oncall["schedule"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_pagerdutys_word_on_a_failure()
        {
            var pagerDuty = new PagerDuty();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pagerDuty, operation, parameters, settings, endpoint, credentials))).Message;
            const string Incidents = PagerDutyConnector.IncidentsOperation;
            var from = new Dictionary<string, string> { ["from"] = "ops@example.test" };

            Assert.StartsWith("PagerDuty has no operation 'schedules' - incidents, incident,", await Refused("schedules", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(Incidents, new() { ["max"] = "0" }));
            Assert.Equal("Give the id.", await Refused(PagerDutyConnector.IncidentOperation, new()));
            Assert.Equal("'pinc9' is not a PagerDuty id - capital letters and digits, PXXXXXX.", await Refused(PagerDutyConnector.NotesOperation, new() { ["id"] = "pinc9" }));
            Assert.Equal("Give who it comes from - a PagerDuty user's e-mail, here or on the connection.", await Refused(PagerDutyConnector.CreateNoteOperation, new() { ["id"] = "PINC9", ["content"] = "x" }));
            Assert.Equal("'ops' is not an e-mail address (from).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["from"] = "ops" }));
            Assert.Equal("'open' is not triggered, acknowledged or resolved (statuses).", await Refused(Incidents, new() { ["statuses"] = "open" }));
            Assert.Equal("'urgent' is not high or low (urgencies).", await Refused(Incidents, new() { ["urgencies"] = "urgent" }));
            Assert.Equal("'kassa' is not a PagerDuty id (services).", await Refused(Incidents, new() { ["services"] = "kassa" }));
            Assert.Equal("'notes' is not one of acknowledgers, agents, assignees, conference_bridge, escalation_policies, first_trigger_log_entries, priorities, services, teams, users (include).", await Refused(Incidents, new() { ["include"] = "notes" }));
            Assert.Equal("'logs' is not incidents, services, channels or teams (include).", await Refused(PagerDutyConnector.LogEntriesOperation, new() { ["include"] = "logs" }));
            Assert.Equal("'yesterday' is not a time - 2026-10-01T00:00:00Z (since).", await Refused(Incidents, new() { ["since"] = "yesterday" }));
            Assert.Equal("'created_at;drop' is not a sort - created_at:desc.", await Refused(Incidents, new() { ["sort"] = "created_at;drop" }));
            Assert.Equal("'Europe/Stock holm' is not a time zone - Europe/Stockholm.", await Refused(Incidents, new() { ["time_zone"] = "Europe/Stock holm" }));
            Assert.Equal("Give the incident's title.", await Refused(PagerDutyConnector.CreateIncidentOperation, new() { ["service"] = "PSRV1" }, from));
            Assert.Equal("Give the service - its id.", await Refused(PagerDutyConnector.CreateIncidentOperation, new() { ["title"] = "x" }, from));
            Assert.Equal("'Kassa' is not a PagerDuty id - capital letters and digits (service).", await Refused(PagerDutyConnector.CreateIncidentOperation, new() { ["title"] = "x", ["service"] = "Kassa" }, from));
            Assert.Equal("'closed' is not acknowledged or resolved (status).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["status"] = "closed" }, from));
            Assert.Equal("'0' is not a level above 0 (escalation_level).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["escalation_level"] = "0" }, from));
            Assert.Equal("'urgent' is not high or low (urgency).", await Refused(PagerDutyConnector.CreateIncidentOperation, new() { ["title"] = "x", ["service"] = "PSRV1", ["urgency"] = "urgent" }, from));
            Assert.Equal("'p1' is not a PagerDuty id - capital letters and digits (priority).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["priority"] = "p1" }, from));
            Assert.Equal("'ada' is not a PagerDuty id - capital letters and digits (assignees).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["assignees"] = "PUSR1,ada" }, from));
            Assert.Equal("'meet.example.test' is not an address - https://meet.example.com/ops (conference_url).", await Refused(PagerDutyConnector.UpdateIncidentOperation, new() { ["id"] = "PINC9", ["conference_url"] = "meet.example.test" }, from));
            Assert.Equal("Give the note.", await Refused(PagerDutyConnector.CreateNoteOperation, new() { ["id"] = "PINC9" }, from));
            Assert.Equal("The PagerDuty connection has no API token.", await Refused(Incidents, new(), credentials: new()));
            Assert.Equal("The endpoint 'api.pagerduty.com' is not an absolute http(s) URL.", await Refused(Incidents, new(), endpoint: "api.pagerduty.com"));
            Assert.Empty(pagerDuty.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new PagerDuty { Answer = (_, _) => (status, answer) }, Incidents, new()))).Message;
            Assert.Equal("HTTP 400 (2001): Invalid Input Provided - Invalid status; Invalid urgency", await Failed(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid Input Provided\",\"code\":2001,\"errors\":[\"Invalid status\",\"Invalid urgency\"]}}"));
            Assert.Equal("HTTP 401: Unauthorized", await Failed(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Unauthorized\"}}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"" + new string('e', 400) + "\"}}")); // a long word cut
            Assert.Equal("PagerDuty answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_accounts_abilities()
        {
            Task<ConnectionTestResult> Test(PagerDuty pagerDuty) =>
                new PagerDutyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(pagerDuty), "", null, Credentials));
            var three = new PagerDuty { Answer = (_, _) => (HttpStatusCode.OK, "{\"abilities\":[\"teams\",\"read_only_users\",\"urgencies\"]}") };
            var tested = await Test(three);
            Assert.Equal((true, "PagerDuty: the token answers - 3 abilities on the account."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/abilities", "Token token=pd-token-1"), (three.Requests[0].Url, three.Requests[0].Authorization));
            Assert.Equal("PagerDuty: the token answers - 1 ability on the account.", (await Test(new PagerDuty { Answer = (_, _) => (HttpStatusCode.OK, "{\"abilities\":[\"teams\"]}") })).Message);
            var refused = await Test(new PagerDuty { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Unauthorized\",\"code\":2006}}") });
            Assert.Equal((false, "HTTP 401 (2006): Unauthorized"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "pagerduty"), (new PagerDutyConnector().AuthType, new PagerDutyConnector().ConnectorType));
            Assert.Equal(12, new PagerDutyConnector().Operations.Count);
        }
    }
}
