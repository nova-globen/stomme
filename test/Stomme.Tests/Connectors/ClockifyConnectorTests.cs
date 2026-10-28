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
using Stomme.Modules.Connectors.Clockify;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Clockify (n8n's Clockify node: a workspace's clients, projects, tags, tasks and time entries created, got,
    /// updated and deleted, its users and the workspaces read): every call Clockify's API v1 with the key in X-Api-Key, a
    /// region's address too; every list page by page, the page size fixed and no bigger than asked, with its filters; a user's
    /// time entries over a period, the key's own user's when none is named, the moments sent in UTC; each resource got, created,
    /// updated and deleted at its path; a time entry's update keeping its own start when none is given; what could go wrong
    /// refused before anything is sent; Clockify's word on a failure; a test naming whom the key signs in as.
    /// </summary>
    public sealed class ClockifyConnectorTests
    {
        private const string Api = "https://clockify.example.test/api/v1";

        private const string Ws = "5f1a2b3c4d5e6f7081920a3b";

        private const string Client = "60aa1b2c3d4e5f6071829304";

        private const string Project = "60bb1b2c3d4e5f6071829304";

        private const string Task = "60cc1b2c3d4e5f6071829304";

        private const string Entry = "60dd1b2c3d4e5f6071829304";

        private const string User = "60ee1b2c3d4e5f6071829304";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "clockify-key-1" };

        private sealed class Clockify : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Key, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("X-Api-Key", out var key) ? key.Single() : null, body));
                var (status, answer) = Answer(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Clockify clockify, string operation, Dictionary<string, string> parameters, string endpoint = Api) =>
            Contexts.DrainAsync(new ClockifyConnector(), Contexts.Extract(new StubEgress(clockify), endpoint, operation, parameters, settings: new Dictionary<string, string>(), credentials: Credentials));

        private static string Items(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"" + i.ToString("x24") + "\",\"name\":\"Café " + i + "\",\"archived\":false,\"hourlyRate\":{\"amount\":9950,\"currency\":\"SEK\",\"by\":\"Café\"},\"budget\":12,\"note\":null}")) + "]";

        [Fact]
        public async Task Lists_are_read_page_by_page_with_their_filters()
        {
            var clockify = new Clockify { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("page=2&", StringComparison.Ordinal) ? Items(201, 50) : Items(1, 200)) };

            var clients = await Run(clockify, "clients", new() { ["workspace"] = Ws, ["name"] = "Café AB", ["archived"] = "TRUE" });
            Assert.Equal(250, clients.Count); // a thousand at most by default
            Assert.Equal(new[]
            {
                $"{Api}/workspaces/{Ws}/clients?name=Caf%C3%A9%20AB&archived=true&page=1&page-size=200",
                $"{Api}/workspaces/{Ws}/clients?name=Caf%C3%A9%20AB&archived=true&page=2&page-size=200",
            }, clockify.Requests.Select(request => request.Url)); // a short page the end
            Assert.All(clockify.Requests, request => Assert.Equal("clockify-key-1", request.Key));
            var first = clients[0].Fields;
            Assert.Equal((1.ToString("x24"), "Café 1", false, "{\"amount\":9950,\"currency\":\"SEK\",\"by\":\"Café\"}", 12L), ((string?)first["id"], (string?)first["name"], (bool?)first["archived"], (string?)first["hourlyRate"], (long?)first["budget"]));
            Assert.Null(first["note"]);

            Assert.Equal(30, (await Run(clockify, "projects", new() { ["workspace"] = Ws, ["billable"] = "false", ["max"] = "30" })).Count);
            Assert.Equal($"{Api}/workspaces/{Ws}/projects?billable=false&page=1&page-size=30", clockify.Requests[^1].Url); // a page no bigger than asked
            await Run(clockify, "tags", new() { ["workspace"] = Ws, ["max"] = "5" });
            Assert.Equal($"{Api}/workspaces/{Ws}/tags?page=1&page-size=5", clockify.Requests[^1].Url);
            await Run(clockify, "users", new() { ["workspace"] = Ws, ["email"] = "ada@example.com", ["max"] = "5" });
            Assert.Equal($"{Api}/workspaces/{Ws}/users?email=ada%40example.com&page=1&page-size=5", clockify.Requests[^1].Url);
            await Run(clockify, "tasks", new() { ["workspace"] = Ws, ["project"] = Project, ["active"] = "true", ["max"] = "5" });
            Assert.Equal($"{Api}/workspaces/{Ws}/projects/{Project}/tasks?is-active=true&page=1&page-size=5", clockify.Requests[^1].Url); // Clockify's own name for it
            await Run(clockify, "workspaces", new() { ["max"] = "5" });
            Assert.Equal($"{Api}/workspaces?page=1&page-size=5", clockify.Requests[^1].Url);

            clockify.Answer = (_, url) => (HttpStatusCode.OK, url.EndsWith("/user", StringComparison.Ordinal) ? "{\"id\":\"" + User + "\",\"name\":\"Ada\"}" : "[{\"id\":\"" + Entry + "\",\"description\":\"Café work\",\"timeInterval\":{\"start\":\"2026-10-13T06:00:00Z\",\"end\":\"2026-10-13T08:00:00Z\",\"duration\":\"PT2H\"}}]");
            var entries = await Run(clockify, "time_entries", new() { ["workspace"] = Ws, ["start"] = "2026-10-01T02:00:00+02:00", ["end"] = "2026-10-31T23:59:59Z", ["project"] = Project, ["description"] = "Café" });
            Assert.Equal(new[]
            {
                $"{Api}/user",
                $"{Api}/workspaces/{Ws}/user/{User}/time-entries?start=2026-10-01T00%3A00%3A00Z&end=2026-10-31T23%3A59%3A59Z&project={Project}&description=Caf%C3%A9&page=1&page-size=200",
            }, clockify.Requests.TakeLast(2).Select(request => request.Url)); // the key's own user; the moments in UTC
            Assert.Equal("{\"start\":\"2026-10-13T06:00:00Z\",\"end\":\"2026-10-13T08:00:00Z\",\"duration\":\"PT2H\"}", (string?)Assert.Single(entries).Fields["timeInterval"]);
            await Run(clockify, "time_entries", new() { ["workspace"] = Ws, ["user"] = User });
            Assert.Equal($"{Api}/workspaces/{Ws}/user/{User}/time-entries?page=1&page-size=200", clockify.Requests[^1].Url); // a user named, none asked
            Assert.NotEqual($"{Api}/user", clockify.Requests[^2].Url);
        }

        [Fact]
        public async Task Each_resource_is_got_created_updated_and_deleted_at_its_path()
        {
            var clockify = new Clockify
            {
                Answer = (method, url) => (method == HttpMethod.Delete ? HttpStatusCode.OK : HttpStatusCode.OK,
                    method == HttpMethod.Get && url.Contains("/time-entries/", StringComparison.Ordinal) ? "{\"id\":\"" + Entry + "\",\"timeInterval\":{\"start\":\"2026-10-13T06:00:00Z\"}}"
                    : method == HttpMethod.Get && url.EndsWith("/user", StringComparison.Ordinal) ? "{\"id\":\"" + User + "\",\"name\":\"Ada\",\"activeWorkspace\":\"" + Ws + "\"}"
                    : "{\"id\":\"" + Client + "\",\"name\":\"Café AB\"}"),
            };
            (string, string, string?) Last() => (clockify.Requests[^1].Method.Method, clockify.Requests[^1].Url, clockify.Requests[^1].Body);
            var ws = $"{Api}/workspaces/{Ws}";

            Assert.Equal(Ws, (string?)Assert.Single(await Run(clockify, "current_user", new())).Fields["activeWorkspace"]);
            Assert.Equal(("GET", $"{Api}/user", (string?)null), Last());
            foreach (var (operation, parameters, url) in new (string, Dictionary<string, string>, string)[]
            {
                ("get_client", new() { ["workspace"] = Ws, ["client"] = Client }, $"{ws}/clients/{Client}"),
                ("get_project", new() { ["workspace"] = Ws, ["project"] = Project }, $"{ws}/projects/{Project}"),
                ("get_tag", new() { ["workspace"] = Ws, ["tag"] = Client }, $"{ws}/tags/{Client}"),
                ("get_task", new() { ["workspace"] = Ws, ["project"] = Project, ["task"] = Task }, $"{ws}/projects/{Project}/tasks/{Task}"),
            })
            {
                Assert.Equal("Café AB", (string?)Assert.Single(await Run(clockify, operation, parameters)).Fields["name"]);
                Assert.Equal(("GET", url, (string?)null), Last());
            }

            await Run(clockify, "get_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = Entry });
            Assert.Equal(("GET", $"{ws}/time-entries/{Entry}", (string?)null), Last());

            var made = Assert.Single(await Run(clockify, "create_client", new() { ["workspace"] = Ws, ["name"] = "Café AB", ["fields"] = "{\"note\": \"Retainer\", \"name\": \"overridden\"}" })).Fields;
            Assert.Equal(("POST", $"{ws}/clients", (string?)"{\"note\":\"Retainer\",\"name\":\"Café AB\"}"), Last()); // the name given over the fields'
            Assert.Equal(Client, (string?)made["id"]);
            await Run(clockify, "create_project", new() { ["workspace"] = Ws, ["name"] = "Shop", ["fields"] = "{\"clientId\": \"" + Client + "\", \"billable\": true}" });
            Assert.Equal(("POST", $"{ws}/projects", (string?)"{\"clientId\":\"" + Client + "\",\"billable\":true,\"name\":\"Shop\"}"), Last());
            await Run(clockify, "create_tag", new() { ["workspace"] = Ws, ["name"] = "Urgent" });
            Assert.Equal(("POST", $"{ws}/tags", (string?)"{\"name\":\"Urgent\"}"), Last());
            await Run(clockify, "create_task", new() { ["workspace"] = Ws, ["project"] = Project, ["name"] = "Design" });
            Assert.Equal(("POST", $"{ws}/projects/{Project}/tasks", (string?)"{\"name\":\"Design\"}"), Last());
            await Run(clockify, "create_time_entry", new() { ["workspace"] = Ws, ["start"] = "2026-10-13T10:00:00+02:00", ["end"] = "2026-10-13T12:30:00+0200", ["fields"] = "{\"projectId\": \"" + Project + "\", \"description\": \"Café work\"}" });
            Assert.Equal(("POST", $"{ws}/time-entries", (string?)"{\"projectId\":\"" + Project + "\",\"description\":\"Café work\",\"start\":\"2026-10-13T08:00:00Z\",\"end\":\"2026-10-13T10:30:00Z\"}"), Last()); // in UTC

            await Run(clockify, "update_client", new() { ["workspace"] = Ws, ["client"] = Client, ["name"] = "Café AB2" });
            Assert.Equal(("PUT", $"{ws}/clients/{Client}", (string?)"{\"name\":\"Café AB2\"}"), Last());
            await Run(clockify, "update_project", new() { ["workspace"] = Ws, ["project"] = Project, ["fields"] = "{\"archived\": true}" });
            Assert.Equal(("PUT", $"{ws}/projects/{Project}", (string?)"{\"archived\":true}"), Last());
            await Run(clockify, "update_tag", new() { ["workspace"] = Ws, ["tag"] = Client, ["name"] = "Later" });
            Assert.Equal(("PUT", $"{ws}/tags/{Client}", (string?)"{\"name\":\"Later\"}"), Last());
            await Run(clockify, "update_task", new() { ["workspace"] = Ws, ["project"] = Project, ["task"] = Task, ["name"] = "Build" });
            Assert.Equal(("PUT", $"{ws}/projects/{Project}/tasks/{Task}", (string?)"{\"name\":\"Build\"}"), Last());
            await Run(clockify, "update_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = Entry, ["end"] = "2026-10-13T09:00:00Z" });
            Assert.Equal(new[]
            {
                $"GET {ws}/time-entries/{Entry} ",
                $"PUT {ws}/time-entries/{Entry} {{\"end\":\"2026-10-13T09:00:00Z\",\"start\":\"2026-10-13T06:00:00Z\"}}",
            }, clockify.Requests.TakeLast(2).Select(request => $"{request.Method} {request.Url} {request.Body}")); // its own start kept
            var before = clockify.Requests.Count;
            await Run(clockify, "update_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = Entry, ["start"] = "2026-10-13T07:00:00Z" });
            Assert.Equal((before + 1, "PUT", (string?)"{\"start\":\"2026-10-13T07:00:00Z\"}"), (clockify.Requests.Count, clockify.Requests[^1].Method.Method, clockify.Requests[^1].Body)); // a start given, nothing read

            foreach (var (operation, parameters, url, id) in new (string, Dictionary<string, string>, string, string)[]
            {
                ("delete_client", new() { ["workspace"] = Ws, ["client"] = Client }, $"{ws}/clients/{Client}", Client),
                ("delete_project", new() { ["workspace"] = Ws, ["project"] = Project }, $"{ws}/projects/{Project}", Project),
                ("delete_tag", new() { ["workspace"] = Ws, ["tag"] = Client }, $"{ws}/tags/{Client}", Client),
                ("delete_task", new() { ["workspace"] = Ws, ["project"] = Project, ["task"] = Task }, $"{ws}/projects/{Project}/tasks/{Task}", Task),
                ("delete_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = Entry }, $"{ws}/time-entries/{Entry}", Entry),
            })
            {
                var gone = Assert.Single(await Run(clockify, operation, parameters)).Fields;
                Assert.Equal(("DELETE", url, (string?)null), Last());
                Assert.Equal((id, true), ((string?)gone["id"], (bool?)gone["deleted"])); // the last id the deleted one
            }
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_clockifys_word_on_a_failure()
        {
            var clockify = new Clockify();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(clockify, operation, parameters))).Message;

            Assert.StartsWith("Clockify has no operation 'reports' - workspaces, current_user,", await Refused("reports", new()));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("workspaces", new() { ["max"] = "0" }));
            Assert.Equal("Give the workspace - its id.", await Refused("clients", new()));
            Assert.Equal("'acme' is not a workspace's id - 24 hexadecimal digits.", await Refused("tags", new() { ["workspace"] = "acme" }));
            Assert.Equal("Give the client - its id.", await Refused("get_client", new() { ["workspace"] = Ws }));
            Assert.Equal("Give the project - its id.", await Refused("tasks", new() { ["workspace"] = Ws }));
            Assert.Equal("'60EE1B2C3D4E5F6071829304' is not a time entry's id - 24 hexadecimal digits.", await Refused("delete_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = "60EE1B2C3D4E5F6071829304" }));
            Assert.Equal("'x' is not a user's id - 24 hexadecimal digits.", await Refused("time_entries", new() { ["workspace"] = Ws, ["user"] = "x" }));
            Assert.Equal("'yes' is not true or false (archived).", await Refused("clients", new() { ["workspace"] = Ws, ["archived"] = "yes" }));
            Assert.Equal("'shop' is not a project's id - 24 hexadecimal digits.", await Refused("time_entries", new() { ["workspace"] = Ws, ["user"] = User, ["project"] = "shop" }));
            Assert.Equal("The name is one line of up to 200 characters.", await Refused("projects", new() { ["workspace"] = Ws, ["name"] = new string('x', 201) }));
            Assert.Equal("The description is one line of up to 200 characters.", await Refused("time_entries", new() { ["workspace"] = Ws, ["user"] = User, ["description"] = "a\u0001b" }));
            Assert.Equal("'2026-10-13T08:00:00' is not a date and time with its zone (start) - 2026-10-13T08:00:00Z.", await Refused("time_entries", new() { ["workspace"] = Ws, ["user"] = User, ["start"] = "2026-10-13T08:00:00" }));
            Assert.Equal("'yesterday Z' is not a date and time with its zone (end) - 2026-10-13T08:00:00Z.", await Refused("create_time_entry", new() { ["workspace"] = Ws, ["start"] = "2026-10-13T08:00:00Z", ["end"] = "yesterday Z" }));
            Assert.Equal("The other fields are not a JSON object - {\"billable\": true}.", await Refused("create_client", new() { ["workspace"] = Ws, ["name"] = "x", ["fields"] = "[1]" }));
            Assert.Equal("Give the name.", await Refused("create_tag", new() { ["workspace"] = Ws, ["fields"] = "{\"archived\": false}" }));
            Assert.Equal("Give the start - when it started, with its zone.", await Refused("create_time_entry", new() { ["workspace"] = Ws, ["fields"] = "{\"billable\": true}" }));
            Assert.Equal("Give what to change - a name, a start or other fields.", await Refused("update_task", new() { ["workspace"] = Ws, ["project"] = Project, ["task"] = Task }));
            Assert.Empty(clockify.Requests); // nothing sent

            var keyless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ClockifyConnector(), Contexts.Extract(new StubEgress(clockify), Api, "workspaces", new Dictionary<string, string>(), settings: new Dictionary<string, string>(), credentials: new Dictionary<string, string>())));
            Assert.Equal("The Clockify connection has no API key.", keyless.Message);

            async Task<string> Failed(HttpStatusCode status, string answer, string operation = "workspaces", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Clockify { Answer = (_, _) => (status, answer) }, operation, parameters ?? new()))).Message;
            Assert.Equal("HTTP 400: Project must be archived first", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"Project must be archived first\",\"code\":501}"));
            Assert.Equal("HTTP 500: " + new string('e', 300), await Failed(HttpStatusCode.InternalServerError, "{\"message\":\"" + new string('e', 400) + "\"}")); // a long word cut
            Assert.Equal("Clockify answered HTTP 200 with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")); // never read as nothing
            Assert.Equal("HTTP 502.", await Failed(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
            Assert.Equal("Clockify did not say whom the key signs in as.", await Failed(HttpStatusCode.OK, "{\"name\":\"Ada\"}", "time_entries", new() { ["workspace"] = Ws }));
            Assert.Equal("Clockify did not say when the time entry started.", await Failed(HttpStatusCode.OK, "{\"id\":\"" + Entry + "\"}", "update_time_entry", new() { ["workspace"] = Ws, ["time_entry"] = Entry, ["fields"] = "{\"billable\": true}" }));
        }

        [Fact]
        public async Task A_test_names_whom_the_key_signs_in_as()
        {
            var clockify = new Clockify { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"" + User + "\",\"name\":\"Ada\",\"email\":\"ada@example.com\"}") };
            var tested = await new ClockifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(clockify), "", credentials: Credentials));
            Assert.Equal((true, "Clockify: signed in as Ada."), (tested.Success, tested.Message));
            Assert.Equal(("https://api.clockify.me/api/v1/user", "clockify-key-1"), (clockify.Requests[0].Url, clockify.Requests[0].Key)); // Clockify's own address when none is given
            var mailed = new Clockify { Answer = (_, _) => (HttpStatusCode.OK, "{\"email\":\"ada@example.com\"}") };
            Assert.Equal("Clockify: signed in as ada@example.com.", (await new ClockifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(mailed), "https://euc1.clockify.me/", credentials: Credentials))).Message);
            Assert.Equal("https://euc1.clockify.me/api/v1/user", mailed.Requests[0].Url); // a region's address, the API's path added
            var nameless = new Clockify { Answer = (_, _) => (HttpStatusCode.OK, "{}") };
            Assert.Equal("Clockify: signed in as the key's user.", (await new ClockifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(nameless), Api, credentials: Credentials))).Message);
            var refused = new Clockify { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"message\":\"Full authentication is required to access this resource\",\"code\":1000}") };
            var denied = await new ClockifyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(refused), Api, credentials: Credentials));
            Assert.Equal((false, "HTTP 401: Full authentication is required to access this resource"), (denied.Success, denied.Message));
            Assert.Equal((AuthenticationType.ApiKey, "clockify"), (new ClockifyConnector().AuthType, new ClockifyConnector().ConnectorType));
            Assert.Equal(28, new ClockifyConnector().Operations.Count);
            Assert.Equal(new[] { "workspace", "client", "name", "fields" }, new ClockifyConnector().Operations.Single(operation => operation.Name == "update_client").Parameters!.Fields.Select(field => field.Name));
            var entries = new ClockifyConnector().Operations.Single(operation => operation.Name == "time_entries").Parameters!.Fields;
            Assert.Equal(new[] { "workspace", "user", "start", "end", "project", "description", "max" }, entries.Select(field => field.Name));
            Assert.Equal(new[] { true, false }, entries.Take(2).Select(field => field.Required)); // the user the key's own when none is named
            Assert.Equal(new[] { "workspace", "start", "end", "fields" }, new ClockifyConnector().Operations.Single(operation => operation.Name == "create_time_entry").Parameters!.Fields.Select(field => field.Name));
        }
    }
}
