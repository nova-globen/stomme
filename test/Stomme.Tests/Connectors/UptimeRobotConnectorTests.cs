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
using Stomme.Modules.Connectors.UptimeRobot;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// UptimeRobot (n8n's UptimeRobot node: its account, monitors, alert contacts, maintenance windows and public status
    /// pages): every call a POST to UptimeRobot's API v2 with the key and format=json in its form; a list read page by page by
    /// offset, its total the end, no more than asked; monitors created, updated, paused, reset and deleted; alert contacts,
    /// maintenance windows and status pages kept; kinds by name or UptimeRobot's number; what could go wrong refused before
    /// anything is sent; UptimeRobot's stat anything but ok a failure with its words; a test naming the key's account.
    /// </summary>
    public sealed class UptimeRobotConnectorTests
    {
        private const string Api = "https://api.uptimerobot.com/v2";

        private const string Key = "api_key=u123-kafe&format=json";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "u123-kafe" };

        private sealed class UptimeRobot : HttpMessageHandler
        {
            public List<(string Url, string? Body)> Requests { get; } = new();

            public Func<string, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Post, request.Method); // every call a POST
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((url, body));
                var (status, answer) = Answer(url, body ?? "");
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(UptimeRobot robot, string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
            Contexts.DrainAsync(new UptimeRobotConnector(), Contexts.Extract(new StubEgress(robot), endpoint, operation, parameters, credentials: credentials ?? Credentials));

        private static string Monitors(int first, int count, int total) =>
            "{\"stat\":\"ok\",\"pagination\":{\"offset\":" + (first - 1) + ",\"limit\":50,\"total\":" + total + "},\"monitors\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + (777000 + i) + ",\"friendly_name\":\"Kassa " + i + "\",\"url\":\"https://kafe.example.test\",\"type\":1,\"status\":2,\"logs\":[{\"type\":2}]}")) + "]}";

        [Fact]
        public async Task Lists_are_read_page_by_page_and_monitors_kept()
        {
            var robot = new UptimeRobot
            {
                Answer = (url, body) => url switch
                {
                    var u when u.EndsWith("/getMonitors", StringComparison.Ordinal) && body.Contains("offset=100", StringComparison.Ordinal) => (HttpStatusCode.OK, Monitors(101, 20, 120)),
                    var u when u.EndsWith("/getMonitors", StringComparison.Ordinal) && body.Contains("offset=50", StringComparison.Ordinal) => (HttpStatusCode.OK, Monitors(51, 50, 120)),
                    var u when u.EndsWith("/getMonitors", StringComparison.Ordinal) => (HttpStatusCode.OK, Monitors(1, 50, 120)),
                    var u when u.EndsWith("/newMonitor", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"monitor\":{\"id\":777810874,\"status\":1}}"),
                    _ => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"monitor\":{\"id\":777810874}}"),
                },
            };
            (string, string?) Last() => (robot.Requests[^1].Url, robot.Requests[^1].Body);

            var read = await Run(robot, UptimeRobotConnector.MonitorsOperation, new() { ["max"] = "70" });
            Assert.Equal(70, read.Count); // no more than asked
            Assert.Equal(new[] { $"{Key}&offset=0&limit=50", $"{Key}&offset=50&limit=50" }, robot.Requests.Select(request => request.Body)); // by offset
            Assert.All(robot.Requests, request => Assert.Equal($"{Api}/getMonitors", request.Url));
            Assert.Equal((777001L, "Kassa 1", 2L, "[{\"type\":2}]"), ((long?)read[0].Fields["id"], (string?)read[0].Fields["friendly_name"], (long?)read[0].Fields["status"], (string?)read[0].Fields["logs"]));
            robot.Requests.Clear();
            Assert.Equal(120, (await Run(robot, null!, new())).Count); // no operation: the monitors - its total the end
            Assert.Equal(3, robot.Requests.Count);
            var empty = new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"pagination\":{\"offset\":0,\"limit\":50,\"total\":9},\"monitors\":[]}") };
            Assert.Empty(await Run(empty, UptimeRobotConnector.MonitorsOperation, new())); // an empty page the end
            Assert.Single(empty.Requests);
            var untotalled = new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"monitors\":[{\"id\":1}]}") };
            Assert.Single(await Run(untotalled, UptimeRobotConnector.MonitorsOperation, new())); // no total the end
            var endless = new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, Monitors(1, 50, 100000)) };
            Assert.Equal(1000, (await Run(endless, UptimeRobotConnector.MonitorsOperation, new())).Count); // 1,000 at most when no max is given
            var shortPages = new UptimeRobot { Answer = (_, body) => (HttpStatusCode.OK, body.Contains("offset=20", StringComparison.Ordinal) ? Monitors(21, 10, 30) : Monitors(1, 20, 30)) };
            Assert.Equal(30, (await Run(shortPages, UptimeRobotConnector.MonitorsOperation, new() { ["max"] = "40" })).Count);
            Assert.Equal(new[] { $"{Key}&offset=0&limit=40", $"{Key}&offset=20&limit=40" }, shortPages.Requests.Select(request => request.Body)); // a short page: the next where it ended

            robot.Requests.Clear();
            await Run(robot, UptimeRobotConnector.MonitorsOperation, new() { ["ids"] = "777001-777002", ["search"] = "kassa & bröd", ["statuses"] = "Up, 9", ["types"] = "http,keyword", ["logs"] = "true", ["response_times"] = "True", ["alert_contacts"] = "false", ["mwindows"] = "true", ["max"] = "5" });
            Assert.Equal($"{Key}&monitors=777001-777002&search=kassa+%26+br%C3%B6d&statuses=2-9&types=1-2&logs=1&response_times=1&mwindows=1&offset=0&limit=5", Last().Item2);
            Assert.Equal("Kassa 1", (string?)Assert.Single(await Run(robot, UptimeRobotConnector.MonitorOperation, new() { ["id"] = "777001" }, endpoint: Api + "/")).Fields["friendly_name"]);
            Assert.Equal(($"{Api}/getMonitors", (string?)$"{Key}&monitors=777001"), Last());

            Assert.Equal(777810874L, (long?)Assert.Single(await Run(robot, UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa & bröd", ["url"] = "https://kafe.example.test/kassa", ["type"] = "Keyword", ["keyword_type"] = "not_exists", ["keyword_value"] = "Error", ["interval"] = "300" })).Fields["id"]);
            Assert.Equal(($"{Api}/newMonitor", (string?)$"{Key}&friendly_name=Kassa+%26+br%C3%B6d&url=https%3A%2F%2Fkafe.example.test%2Fkassa&type=2&keyword_type=2&keyword_value=Error&interval=300"), Last());
            await Run(robot, UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Mail", ["url"] = "mail.kafe.example.test", ["type"] = "4", ["sub_type"] = "custom", ["port"] = "2525" });
            Assert.Equal($"{Key}&friendly_name=Mail&url=mail.kafe.example.test&type=4&sub_type=99&port=2525", Last().Item2);
            await Run(robot, UptimeRobotConnector.UpdateMonitorOperation, new() { ["id"] = "777810874", ["friendly_name"] = "Kassan", ["url"] = "https://kafe.example.test", ["interval"] = "60", ["http_method"] = "HEAD", ["status"] = "Paused" });
            Assert.Equal(($"{Api}/editMonitor", (string?)$"{Key}&id=777810874&friendly_name=Kassan&url=https%3A%2F%2Fkafe.example.test&interval=60&http_method=1&status=0"), Last());
            await Run(robot, UptimeRobotConnector.UpdateMonitorOperation, new() { ["id"] = "777810874", ["status"] = "active" });
            Assert.Equal($"{Key}&id=777810874&status=1", Last().Item2);
            await Run(robot, UptimeRobotConnector.ResetMonitorOperation, new() { ["id"] = "777810874" });
            Assert.Equal(($"{Api}/resetMonitor", (string?)$"{Key}&id=777810874"), Last());
            Assert.Equal(777810874L, (long?)Assert.Single(await Run(robot, UptimeRobotConnector.DeleteMonitorOperation, new() { ["id"] = "777810874" })).Fields["id"]);
            Assert.Equal(($"{Api}/deleteMonitor", (string?)$"{Key}&id=777810874"), Last());
            var account = Assert.Single(await Run(new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"account\":{\"email\":\"ada@kafe.example.test\",\"monitor_limit\":50,\"up_monitors\":7}}") }, UptimeRobotConnector.AccountOperation, new())).Fields;
            Assert.Equal(("ada@kafe.example.test", 50L), ((string?)account["email"], (long?)account["monitor_limit"]));
        }

        [Fact]
        public async Task Alert_contacts_maintenance_windows_and_status_pages_are_kept()
        {
            var robot = new UptimeRobot
            {
                Answer = (url, _) => url switch
                {
                    var u when u.EndsWith("/getAlertContacts", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"offset\":0,\"limit\":50,\"total\":1,\"alert_contacts\":[{\"id\":\"0993765\",\"friendly_name\":\"Ada\",\"type\":2,\"value\":\"ada@kafe.example.test\"}]}"),
                    var u when u.EndsWith("/newAlertContact", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"alertcontact\":{\"id\":\"4561\",\"status\":0}}"),
                    var u when u.EndsWith("/getMWindows", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"pagination\":{\"offset\":0,\"limit\":50,\"total\":1},\"mwindows\":[{\"id\":581,\"type\":3,\"friendly_name\":\"Fika\",\"start_time\":\"22:00\",\"duration\":60,\"value\":\"1-3\"}]}"),
                    var u when u.EndsWith("/deleteMWindow", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"message\":\"The maintenance window is deleted.\"}"),
                    var u when u.EndsWith("/getPSPs", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"pagination\":{\"offset\":0,\"limit\":50,\"total\":1},\"psps\":[{\"id\":2345,\"friendly_name\":\"Kafé status\",\"monitors\":[777001],\"standard_url\":\"https://stats.uptimerobot.com/abc\"}]}"),
                    var u when u.EndsWith("MWindow", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"mwindow\":{\"id\":581,\"status\":1}}"),
                    var u when u.EndsWith("PSP", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"psp\":{\"id\":2345}}"),
                    _ => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"alert_contact\":{\"id\":\"4561\"}}"),
                },
            };
            (string, string?) Last() => (robot.Requests[^1].Url, robot.Requests[^1].Body);

            var contact = Assert.Single(await Run(robot, UptimeRobotConnector.AlertContactsOperation, new() { ["ids"] = "993765" })).Fields;
            Assert.Equal(($"{Api}/getAlertContacts", (string?)$"{Key}&alert_contacts=993765&offset=0&limit=50"), Last());
            Assert.Equal(("0993765", 2L), ((string?)contact["id"], (long?)contact["type"]));
            var contacts = new UptimeRobot { Answer = (_, body) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"offset\":0,\"limit\":50,\"total\":2,\"alert_contacts\":[{\"id\":\"" + (body.Contains("offset=1", StringComparison.Ordinal) ? "2" : "1") + "\"}]}") };
            Assert.Equal(new[] { "1", "2" }, (await Run(contacts, UptimeRobotConnector.AlertContactsOperation, new())).Select(record => (string?)record.Fields["id"])); // the alert contacts' total beside their list
            await Run(robot, UptimeRobotConnector.AlertContactOperation, new() { ["id"] = "993765" });
            Assert.Equal($"{Key}&alert_contacts=993765", Last().Item2);
            Assert.Equal("4561", (string?)Assert.Single(await Run(robot, UptimeRobotConnector.CreateAlertContactOperation, new() { ["friendly_name"] = "Kassa hook", ["type"] = "webhook", ["value"] = "https://kafe.example.test/hook?" })).Fields["id"]);
            Assert.Equal(($"{Api}/newAlertContact", (string?)$"{Key}&friendly_name=Kassa+hook&type=5&value=https%3A%2F%2Fkafe.example.test%2Fhook%3F"), Last());
            await Run(robot, UptimeRobotConnector.UpdateAlertContactOperation, new() { ["id"] = "4561", ["friendly_name"] = "Kassa" });
            Assert.Equal(($"{Api}/editAlertContact", (string?)$"{Key}&id=4561&friendly_name=Kassa"), Last());
            await Run(robot, UptimeRobotConnector.DeleteAlertContactOperation, new() { ["id"] = "4561" });
            Assert.Equal(($"{Api}/deleteAlertContact", (string?)$"{Key}&id=4561"), Last());

            Assert.Equal("1-3", (string?)Assert.Single(await Run(robot, UptimeRobotConnector.MaintenanceWindowsOperation, new())).Fields["value"]);
            Assert.Equal(($"{Api}/getMWindows", (string?)$"{Key}&offset=0&limit=50"), Last());
            await Run(robot, UptimeRobotConnector.MaintenanceWindowOperation, new() { ["id"] = "581" });
            Assert.Equal($"{Key}&mwindows=581", Last().Item2);
            await Run(robot, UptimeRobotConnector.CreateMaintenanceWindowOperation, new() { ["friendly_name"] = "Fika", ["type"] = "weekly", ["start_time"] = "22:00", ["duration"] = "60", ["value"] = "1-3" });
            Assert.Equal(($"{Api}/newMWindow", (string?)$"{Key}&friendly_name=Fika&type=3&value=1-3&start_time=22%3A00&duration=60"), Last());
            await Run(robot, UptimeRobotConnector.CreateMaintenanceWindowOperation, new() { ["friendly_name"] = "Move", ["type"] = "once", ["start_time"] = "2026-10-20T22:00:00+02:00", ["duration"] = "120" });
            Assert.Equal($"{Key}&friendly_name=Move&type=1&start_time=1792526400&duration=120", Last().Item2); // a moment as UptimeRobot's seconds
            await Run(robot, UptimeRobotConnector.CreateMaintenanceWindowOperation, new() { ["friendly_name"] = "Count", ["type"] = "monthly", ["start_time"] = "03:30", ["duration"] = "30", ["value"] = "31" });
            Assert.Equal($"{Key}&friendly_name=Count&type=4&value=31&start_time=03%3A30&duration=30", Last().Item2);
            await Run(robot, UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581", ["duration"] = "90", ["friendly_name"] = "Fika!", ["type"] = "daily", ["start_time"] = "21:00" });
            Assert.Equal(($"{Api}/editMWindow", (string?)$"{Key}&id=581&duration=90&friendly_name=Fika%21&type=2&start_time=21%3A00"), Last());
            await Run(robot, UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581", ["duration"] = "45" });
            Assert.Equal($"{Key}&id=581&duration=45", Last().Item2);
            Assert.Equal("The maintenance window is deleted.", (string?)Assert.Single(await Run(robot, UptimeRobotConnector.DeleteMaintenanceWindowOperation, new() { ["id"] = "581" })).Fields["message"]);
            Assert.Equal(($"{Api}/deleteMWindow", (string?)$"{Key}&id=581"), Last());

            var page = Assert.Single(await Run(robot, UptimeRobotConnector.StatusPagesOperation, new() { ["ids"] = "2345" })).Fields;
            Assert.Equal(($"{Api}/getPSPs", (string?)$"{Key}&psps=2345&offset=0&limit=50"), Last());
            Assert.Equal(("Kafé status", "[777001]"), ((string?)page["friendly_name"], (string?)page["monitors"]));
            await Run(robot, UptimeRobotConnector.StatusPageOperation, new() { ["id"] = "2345" });
            Assert.Equal($"{Key}&psps=2345", Last().Item2);
            Assert.Equal(2345L, (long?)Assert.Single(await Run(robot, UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "Kafé status", ["monitors"] = "777001-777002", ["custom_domain"] = "status.kafe.example.test", ["sort"] = "down_up_paused" })).Fields["id"]);
            Assert.Equal(($"{Api}/newPSP", (string?)$"{Key}&friendly_name=Kaf%C3%A9+status&monitors=777001-777002&custom_domain=status.kafe.example.test&sort=4"), Last());
            await Run(robot, UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "All", ["monitors"] = "0" });
            Assert.Equal($"{Key}&friendly_name=All&monitors=0", Last().Item2);
            await Run(robot, UptimeRobotConnector.DeleteStatusPageOperation, new() { ["id"] = "2345" });
            Assert.Equal(($"{Api}/deletePSP", (string?)$"{Key}&id=2345"), Last());
            var none = new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"psps\":[]}") };
            Assert.Equal("UptimeRobot has none with that id.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(none, UptimeRobotConnector.StatusPageOperation, new() { ["id"] = "9" }))).Message);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_uptimerobots_words_on_a_failure()
        {
            var robot = new UptimeRobot();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "", Dictionary<string, string>? credentials = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(robot, operation, parameters, endpoint, credentials))).Message;
            Dictionary<string, string> Window(params (string Name, string Value)[] more)
            {
                var parameters = new Dictionary<string, string> { ["friendly_name"] = "Fika", ["type"] = "daily", ["start_time"] = "22:00", ["duration"] = "60" };
                foreach (var (name, value) in more)
                {
                    parameters[name] = value;
                }

                return parameters;
            }

            Assert.StartsWith("UptimeRobot has no operation 'heartbeats' - account, monitors, monitor,", await Refused("heartbeats", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(UptimeRobotConnector.MonitorsOperation, new() { ["max"] = "0" }));
            Assert.Equal("Give the monitor's id.", await Refused(UptimeRobotConnector.MonitorOperation, new()));
            Assert.Equal("'7a' is not an id (id).", await Refused(UptimeRobotConnector.DeleteAlertContactOperation, new() { ["id"] = "7a" }));
            Assert.Equal("'1,2' is not ids joined by '-' - 236-1782 (ids).", await Refused(UptimeRobotConnector.MonitorsOperation, new() { ["ids"] = "1,2" }));
            Assert.Equal("'gone' is not paused, not_checked, up, seems_down or down (statuses).", await Refused(UptimeRobotConnector.MonitorsOperation, new() { ["statuses"] = "up,gone" }));
            Assert.Equal("'smtp' is not http, keyword, ping, port or heartbeat (types).", await Refused(UptimeRobotConnector.MonitorsOperation, new() { ["types"] = "smtp" }));
            Assert.Equal("'yes' is not true or false (logs).", await Refused(UptimeRobotConnector.MonitorsOperation, new() { ["logs"] = "yes" }));
            Assert.Equal("Give its name.", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["url"] = "https://kafe.example.test", ["type"] = "http" }));
            Assert.Equal("Give what it watches - an address, a host or an IP (url).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["type"] = "http" }));
            Assert.Equal("'kafe example' is not an address, a host or an IP (url).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "kafe example", ["type"] = "http" }));
            Assert.Equal("Give its type - http, keyword, ping, port, heartbeat.", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test" }));
            Assert.Equal("'6' is not http, keyword, ping, port or heartbeat (type).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "6" }));
            Assert.Equal("Only a port monitor has a port type (sub_type).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "http", ["sub_type"] = "https" }));
            Assert.Equal("'65536' is not a whole number from 1 to 65535 (port).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Mail", ["url"] = "mail.kafe.example.test", ["type"] = "port", ["sub_type"] = "custom", ["port"] = "65536" }));
            Assert.Equal("A keyword monitor needs its keyword type and keyword (keyword_type, keyword_value).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "keyword", ["keyword_value"] = "Error" }));
            Assert.Equal("A keyword monitor needs its keyword type and keyword (keyword_type, keyword_value).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "keyword", ["keyword_type"] = "exists" }));
            Assert.Equal("Only a keyword monitor has a keyword (keyword_type, keyword_value).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "http", ["keyword_value"] = "Error" }));
            Assert.Equal("'29' is not a whole number from 30 to 86400 (interval).", await Refused(UptimeRobotConnector.CreateMonitorOperation, new() { ["friendly_name"] = "Kassa", ["url"] = "https://kafe.example.test", ["type"] = "http", ["interval"] = "29" }));
            Assert.Equal("'TRACE' is not head, get, post, put, patch, delete or options (http_method).", await Refused(UptimeRobotConnector.UpdateMonitorOperation, new() { ["id"] = "1", ["http_method"] = "TRACE" }));
            Assert.Equal("'stopped' is not paused or active (status).", await Refused(UptimeRobotConnector.UpdateMonitorOperation, new() { ["id"] = "1", ["status"] = "stopped" }));
            Assert.Equal("Give what to change - its name, address, port type, port, interval, HTTP method or status.", await Refused(UptimeRobotConnector.UpdateMonitorOperation, new() { ["id"] = "1" }));
            Assert.Equal("Give its type - sms, email, twitter, boxcar, webhook, pushbullet, pushover.", await Refused(UptimeRobotConnector.CreateAlertContactOperation, new() { ["friendly_name"] = "Ada", ["value"] = "ada@kafe.example.test" }));
            Assert.Equal("Give its value - an e-mail, a number or an address.", await Refused(UptimeRobotConnector.CreateAlertContactOperation, new() { ["friendly_name"] = "Ada", ["type"] = "email" }));
            Assert.Equal("Give what to change - its name or value.", await Refused(UptimeRobotConnector.UpdateAlertContactOperation, new() { ["id"] = "4561" }));
            Assert.Equal("'yearly' is not once, daily, weekly or monthly (type).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "yearly"))));
            Assert.Equal("Give a weekly window's days (value).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "weekly"))));
            Assert.Equal("Give a monthly window's day (value).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "monthly"))));
            Assert.Equal("'1-8' is not week days 1 (Monday) to 7 joined by '-' (value).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "weekly"), ("value", "1-8"))));
            Assert.Equal("'32' is not a day of the month from 1 to 31 (value).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "monthly"), ("value", "32"))));
            Assert.Equal("Only a weekly or monthly window has days (value).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("value", "1"))));
            Assert.Equal("'25:00' is not a time of day - 22:00 (start_time).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("start_time", "25:00"))));
            Assert.Equal("'22:00' is not a moment - 2026-10-20T22:00:00Z (start_time).", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, Window(("type", "once"))));
            Assert.Equal("Give its start.", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, new() { ["friendly_name"] = "Fika", ["type"] = "daily", ["duration"] = "60" }));
            Assert.Equal("Give its duration.", await Refused(UptimeRobotConnector.CreateMaintenanceWindowOperation, new() { ["friendly_name"] = "Fika", ["type"] = "daily", ["start_time"] = "22:00" }));
            Assert.Equal("'0' is not a whole number from 1 to 525600 (duration).", await Refused(UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581", ["duration"] = "0" }));
            Assert.Equal("Give its duration.", await Refused(UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581" }));
            Assert.Equal("Give the window's type with its days (type, value).", await Refused(UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581", ["duration"] = "60", ["value"] = "1" }));
            Assert.Equal("Give the window's type with its start (type, start_time).", await Refused(UptimeRobotConnector.UpdateMaintenanceWindowOperation, new() { ["id"] = "581", ["duration"] = "60", ["start_time"] = "22:00" }));
            Assert.Equal("Give its monitors - their ids joined by '-', or 0 for all.", await Refused(UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "Kafé" }));
            Assert.Equal("'1,2' is not monitors' ids joined by '-', or 0 for all (monitors).", await Refused(UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "Kafé", ["monitors"] = "1,2" }));
            Assert.Equal("'kafe' is not a domain - status.example.com (custom_domain).", await Refused(UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "Kafé", ["monitors"] = "0", ["custom_domain"] = "kafe" }));
            Assert.Equal("'random' is not name_asc, name_desc, up_down_paused or down_up_paused (sort).", await Refused(UptimeRobotConnector.CreateStatusPageOperation, new() { ["friendly_name"] = "Kafé", ["monitors"] = "0", ["sort"] = "random" }));
            Assert.Equal("The endpoint 'api.uptimerobot.com' is not an absolute http(s) URL.", await Refused(UptimeRobotConnector.AccountOperation, new(), endpoint: "api.uptimerobot.com"));
            Assert.Equal("The UptimeRobot connection has no API key.", await Refused(UptimeRobotConnector.AccountOperation, new(), credentials: new()));
            Assert.Empty(robot.Requests); // nothing sent

            async Task<string> Failed(HttpStatusCode status, string answer) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new UptimeRobot { Answer = (_, _) => (status, answer) }, UptimeRobotConnector.ResetMonitorOperation, new() { ["id"] = "1" }))).Message;
            Assert.Equal("UptimeRobot refused it: not_found: monitor not found.", await Failed(HttpStatusCode.OK, "{\"stat\":\"fail\",\"error\":{\"type\":\"not_found\",\"parameter_name\":\"id\",\"passed_value\":\"1\",\"message\":\"monitor not found.\"}}")); // a 200 with its stat fail
            Assert.Equal("UptimeRobot refused it.", await Failed(HttpStatusCode.OK, "{\"monitor\":{\"id\":1}}")); // no stat ok
            Assert.Equal("HTTP 401: invalid_parameter: api_key is wrong.", await Failed(HttpStatusCode.Unauthorized, "{\"stat\":\"fail\",\"error\":{\"type\":\"invalid_parameter\",\"message\":\"api_key is wrong.\"}}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"stat\":\"fail\",\"error\":{\"message\":\"" + new string('e', 400) + "\"}}")); // a long one cut
            Assert.Equal("UptimeRobot answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>"));
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        }

        [Fact]
        public async Task A_test_names_the_keys_account()
        {
            Task<ConnectionTestResult> Test(UptimeRobot robot) =>
                new UptimeRobotConnector().TestConnectionAsync(Contexts.Test(new StubEgress(robot), "", null, Credentials));
            var account = new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"ok\",\"account\":{\"email\":\"ada@kafe.example.test\",\"monitor_limit\":50}}") };
            var tested = await Test(account);
            Assert.Equal((true, "UptimeRobot: the key is ada@kafe.example.test's, 50 monitors at most."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/getAccountDetails", Key), (account.Requests[0].Url, account.Requests[0].Body));
            Assert.Equal("UptimeRobot: the key is an account's.", (await Test(new UptimeRobot())).Message);
            var refused = await Test(new UptimeRobot { Answer = (_, _) => (HttpStatusCode.OK, "{\"stat\":\"fail\",\"error\":{\"type\":\"invalid_parameter\",\"message\":\"api_key is wrong.\"}}") });
            Assert.Equal((false, "UptimeRobot refused it: invalid_parameter: api_key is wrong."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "uptimerobot"), (new UptimeRobotConnector().AuthType, new UptimeRobotConnector().ConnectorType));
            Assert.Equal(21, new UptimeRobotConnector().Operations.Count);
            Assert.Equal(("api-key", "Main API key (Integrations & API > API)"), (Assert.Single(new UptimeRobotConnector().CredentialLabels!).Key, Assert.Single(new UptimeRobotConnector().CredentialLabels!).Value)); // in UptimeRobot's words
        }
    }
}
