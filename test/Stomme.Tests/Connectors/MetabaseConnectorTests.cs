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
using Stomme.Modules.Connectors.Metabase;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>A pipeline folder in memory, for an operation that saves a file (BL-229's Metabase tests).</summary>
    internal sealed class MemoryFolder(long max = 1_000_000) : IConnectorFiles
    {
        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);

        public long MaxBytes => max;

        public Task<ConnectorFileContent> OpenReadAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<long> WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Written[path] = buffer.ToArray();
            return buffer.Length;
        }
    }

    /// <summary>
    /// Metabase (n8n's Metabase node) without a server: every number, format and file held to its shape before anything is
    /// sent; an API key sent as x-api-key, or a user's e-mail and password exchanged for a session sent as X-Metabase-Session and a
    /// refused sign-in said; questions simplified to what n8n kept or whole, held to the most; a question's result as rows or as a
    /// CSV saved into the run's folder; databases with their connection's details flattened, as n8n's Simplify did, from a page or
    /// an array; a database's fields; metrics and alerts; Metabase's message on a failure; a test naming the user.
    /// </summary>
    public sealed class MetabaseConnectorTests
    {
        private const string Site = "https://metabase.kafe.example";
        private static readonly Dictionary<string, string> Key = new() { ["password"] = "mb_kafe-key" };

        private sealed record Sent(string Method, string Url, string? ApiKey, string? Session, string? Body);

        private sealed class Metabase : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            public Func<Sent, (HttpStatusCode Status, string Body)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, Header("x-api-key"), Header("X-Metabase-Session"), body);
                Requests.Add(sent);
                var (status, answer) = sent.Url == Site + "/api/session" ? (HttpStatusCode.OK, "{\"id\":\"kafe-session\"}") : Answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, sent.Url.EndsWith("/query/csv", StringComparison.Ordinal) ? "text/csv" : "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Metabase metabase, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null, IConnectorFiles? files = null) =>
            Contexts.DrainAsync(new MetabaseConnector(), Contexts.Extract(new StubEgress(metabase), Site, operation, parameters, new Dictionary<string, string>(), credentials ?? Key) with { Files = files });

        private const string Card = "{\"id\":7,\"name\":\"Orders by month\",\"description\":null,\"type\":\"question\",\"display\":\"line\",\"database_id\":1,\"table_id\":2,\"collection_id\":3,\"query_type\":\"query\",\"archived\":false,\"creator_id\":1,\"created_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\",\"result_metadata\":[{\"name\":\"CREATED_AT\"}],\"dataset_query\":{\"database\":1}}";

        [Fact]
        public void It_declares_itself_as_n8ns_metabase_node()
        {
            IConnector connector = new MetabaseConnector();
            Assert.Equal(("metabase", "Metabase", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "questions", "question", "question_result", "databases", "database_fields", "metrics", "metric", "alerts", "alert" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.DoesNotContain(((IConnectorExtractor)connector).Operations.SelectMany(operation => operation.Parameters?.Fields ?? Array.Empty<ConnectorConfigField>()), field => field.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? credentials = null)
            {
                var metabase = new Metabase();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(metabase, operation, parameters, credentials));
                Assert.Empty(metabase.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("Metabase has no operation 'dashboards' - questions, question, question_result", await Refused("dashboards", new()), StringComparison.Ordinal);
            Assert.Equal("Give the question's number (question).", await Refused("question", new()));
            Assert.Equal("'seven' is not a question's number - from its address in Metabase (question).", await Refused("question_result", new() { ["question"] = "seven" }));
            Assert.Equal("'0' is not a database's number - from its address in Metabase (database).", await Refused("database_fields", new() { ["database"] = "0" }));
            Assert.Equal("Give the metric's number (metric).", await Refused("metric", new()));
            Assert.Equal("Give the alert's number (alert).", await Refused("alert", new()));
            Assert.Equal("'pdf' is not json, csv or xlsx (format).", await Refused("question_result", new() { ["question"] = "7", ["format"] = "pdf" }));
            Assert.Equal("Give the file a csv result is saved to, in the run's pipeline folder (file).", await Refused("question_result", new() { ["question"] = "7", ["format"] = "CSV" }));
            Assert.Equal("A result as a file is saved into the run's pipeline folder - run it from a pipeline (file).", await Refused("question_result", new() { ["question"] = "7", ["format"] = "xlsx", ["file"] = "kassa/" }));
            Assert.Equal("'-1' is not a number above 0 (max).", await Refused("questions", new() { ["max"] = "-1" }));
            Assert.Equal("'maybe' is not true or false (simple).", await Refused("databases", new() { ["simple"] = "maybe" }));
            Assert.Equal("The Metabase connection has no password - or API key.", await Refused("questions", new(), new Dictionary<string, string> { ["username"] = "kafe@kafe.example" }));
        }

        [Fact]
        public async Task An_api_key_is_sent_as_its_header_and_a_users_password_for_a_session()
        {
            var metabase = new Metabase { Answer = _ => (HttpStatusCode.OK, "[" + Card + "]") };
            await Run(metabase, "questions", new());
            Assert.Equal(("GET", Site + "/api/card/?f=all", "mb_kafe-key", (string?)null), (metabase.Requests[0].Method, metabase.Requests[0].Url, metabase.Requests[0].ApiKey, metabase.Requests[0].Session));

            metabase.Requests.Clear();
            await Run(metabase, "questions", new(), new Dictionary<string, string> { ["username"] = "kafe@kafe.example", ["password"] = "kafe-password" });
            Assert.Equal(("POST", Site + "/api/session", "{\"username\":\"kafe@kafe.example\",\"password\":\"kafe-password\"}"), (metabase.Requests[0].Method, metabase.Requests[0].Url, metabase.Requests[0].Body));
            Assert.Equal(("kafe-session", (string?)null), (metabase.Requests[1].Session, metabase.Requests[1].ApiKey)); // the session's id, never the password, on every call

            var refusing = new Metabase();
            var tested = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new RefusingSignIn()), Site + "/api", credentials: new Dictionary<string, string> { ["username"] = "kafe@kafe.example", ["password"] = "wrong" }));
            Assert.Equal((false, "Metabase refused the sign-in (HTTP 401): password: did not match stored password - check the e-mail and the password."), (tested.Success, tested.Message));
        }

        private sealed class RefusingSignIn : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"errors\":{\"password\":\"did not match stored password\"}}", Encoding.UTF8, "application/json") });
        }

        [Fact]
        public async Task Questions_are_simplified_or_whole_and_a_result_is_rows_or_a_file()
        {
            var metabase = new Metabase
            {
                Answer = sent => sent.Url switch
                {
                    var url when url.EndsWith("/api/card/?f=all", StringComparison.Ordinal) => (HttpStatusCode.OK, "[" + Card + "," + Card.Replace("\"id\":7", "\"id\":8") + "," + Card.Replace("\"id\":7", "\"id\":9") + "]"),
                    var url when url.EndsWith("/api/card/7", StringComparison.Ordinal) => (HttpStatusCode.OK, Card),
                    var url when url.EndsWith("/query/json", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"Month\":\"2026-09-01\",\"Orders\":74},{\"Month\":\"2026-10-01\",\"Orders\":12},{\"Month\":\"2026-11-01\",\"Orders\":10}]"),
                    var url when url.EndsWith("/query/csv", StringComparison.Ordinal) => (HttpStatusCode.OK, "Month,Orders\n2026-09-01,74\n"),
                    _ => (HttpStatusCode.NotFound, "\"Not found.\""),
                },
            };
            var questions = await Run(metabase, "questions", new() { ["max"] = "2" });
            Assert.Equal(new object?[] { 7L, 8L }, questions.Select(question => question.Fields["id"]));
            Assert.Equal(new[] { "id", "name", "description", "type", "display", "database_id", "table_id", "collection_id", "query_type", "archived", "creator_id", "created_at", "updated_at" }, questions[0].Fields.Keys);
            var whole = Assert.Single(await Run(metabase, "question", new() { ["question"] = "7", ["simple"] = "false" })).Fields;
            Assert.Equal("[{\"name\":\"CREATED_AT\"}]", whole["result_metadata"]);
            Assert.Equal("Orders by month", Assert.Single(await Run(metabase, "question", new() { ["question"] = "7" })).Fields["name"]);

            metabase.Requests.Clear();
            var rows = await Run(metabase, "question_result", new() { ["question"] = "7", ["max"] = "2" });
            Assert.Equal(new Dictionary<string, object?> { ["Month"] = "2026-09-01", ["Orders"] = 74L }, rows[0].Fields);
            Assert.Equal(2, rows.Count);
            Assert.Equal(("POST", Site + "/api/card/7/query/json"), (metabase.Requests[0].Method, metabase.Requests[0].Url));

            var folder = new MemoryFolder();
            var saved = Assert.Single(await Run(metabase, "question_result", new() { ["question"] = "7", ["format"] = "csv", ["file"] = "kassa/" }, files: folder)).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["question"] = 7, ["format"] = "csv", ["file"] = "kassa/question-7.csv", ["size"] = 27L }, saved);
            Assert.Equal("Month,Orders\n2026-09-01,74\n", Encoding.UTF8.GetString(folder.Written["kassa/question-7.csv"]));
            var small = new MemoryFolder(max: 5);
            Assert.StartsWith("The result is 27 bytes, over the pipeline folder's 5", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(metabase, "question_result", new() { ["question"] = "7", ["format"] = "csv", ["file"] = "x.csv" }, files: small))).Message, StringComparison.Ordinal);
            Assert.Empty(small.Written);
        }

        [Fact]
        public async Task Databases_their_fields_metrics_and_alerts_are_read()
        {
            const string Database = "{\"id\":1,\"name\":\"Kassa\",\"description\":\"The tills\",\"engine\":\"postgres\",\"creator_id\":1,\"timezone\":\"Europe/Stockholm\",\"created_at\":\"2026-01-01T00:00:00Z\",\"updated_at\":\"2026-01-02T00:00:00Z\",\"is_full_sync\":true,\"details\":{\"host\":\"db.kafe.example\",\"port\":5432,\"db\":\"kassa\",\"user\":\"reader\",\"ssl\":true},\"features\":[\"foreign-keys\"]}";
            var metabase = new Metabase
            {
                Answer = sent => sent.Url switch
                {
                    var url when url.EndsWith("/api/database/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":[" + Database + "],\"total\":1}"),
                    var url when url.EndsWith("/api/database/1/fields", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"id\":4,\"name\":\"ID\",\"table_name\":\"ORDERS\",\"base_type\":\"type/BigInteger\"},{\"id\":5,\"name\":\"TOTAL\",\"table_name\":\"ORDERS\"}]"),
                    var url when url.EndsWith("/api/metric/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"total\":1,\"data\":[" + Card.Replace("\"type\":\"question\"", "\"type\":\"metric\"").Replace("\"id\":7", "\"id\":19") + "]}"),
                    var url when url.EndsWith("/api/metric/19", StringComparison.Ordinal) => (HttpStatusCode.OK, Card.Replace("\"id\":7", "\"id\":19")),
                    var url when url.EndsWith("/api/alert/", StringComparison.Ordinal) => (HttpStatusCode.OK, "[{\"id\":3,\"card\":{\"id\":7},\"alert_condition\":\"rows\"}]"),
                    var url when url.EndsWith("/api/alert/3", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"id\":3,\"alert_condition\":\"goal\"}"),
                    _ => (HttpStatusCode.NotFound, "\"Not found.\""),
                },
            };
            var database = Assert.Single(await Run(metabase, "databases", new())).Fields;
            Assert.Equal(new Dictionary<string, object?>
            {
                ["id"] = 1L, ["name"] = "Kassa", ["description"] = "The tills", ["engine"] = "postgres", ["creator_id"] = 1L, ["timezone"] = "Europe/Stockholm",
                ["created_at"] = "2026-01-01T00:00:00Z", ["updated_at"] = "2026-01-02T00:00:00Z", ["is_full_sync"] = true,
                ["db"] = "kassa", ["user"] = "reader", ["host"] = "db.kafe.example", ["port"] = 5432L, ["ssl"] = true,
            }, database); // n8n's Simplify: the connection's details flattened, nothing else
            Assert.Equal("[\"foreign-keys\"]", Assert.Single(await Run(metabase, "databases", new() { ["simple"] = "false" })).Fields["features"]);
            Assert.Equal(new[] { "ID", "TOTAL" }, (await Run(metabase, "database_fields", new() { ["database"] = "1" })).Select(field => (string?)field.Fields["name"]));
            Assert.Equal(("metric", (object?)19L), ((string?)Assert.Single(await Run(metabase, "metrics", new())).Fields["type"], Assert.Single(await Run(metabase, "metric", new() { ["metric"] = "19" })).Fields["id"]));
            Assert.Equal("{\"id\":7}", Assert.Single(await Run(metabase, "alerts", new())).Fields["card"]);
            Assert.Equal("goal", Assert.Single(await Run(metabase, "alert", new() { ["alert"] = "3" })).Fields["alert_condition"]);

            Assert.Equal("Metabase answered HTTP 404: Not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(metabase, "alert", new() { ["alert"] = "4" }))).Message);
            var forbidden = new Metabase { Answer = _ => (HttpStatusCode.Forbidden, "{\"message\":\"You don't have permissions to do that.\"}") };
            Assert.Equal("Metabase answered HTTP 403: You don't have permissions to do that. - check the API key, or the e-mail and password, and what they may see.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(forbidden, "databases", new()))).Message);
            var user = new Metabase { Answer = _ => (HttpStatusCode.OK, "{\"common_name\":\"Kafé Test\",\"email\":\"kafe@kafe.example\"}") };
            var tested = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(new StubEgress(user), Site, credentials: Key));
            Assert.Equal((true, "Metabase answers: signed in as Kafé Test."), (tested.Success, tested.Message));
            var nowhere = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(new StubEgress(user), Site + "/?kassa=1", credentials: Key));
            Assert.Equal((false, "'https://metabase.kafe.example/?kassa=1' is not Metabase's address - https://metabase.kafe.example."), (nowhere.Success, nowhere.Message));
        }
    }

    /// <summary>
    /// The Metabase connector against a real Metabase 0.63 (<c>scripts/metabase-test-server.ps1</c>'s throwaway on
    /// loopback, its Sample Database and example content) through the HOST's egress: loopback refused until allowlisted; a test by
    /// the API key and one by the admin's e-mail and password, a wrong password and a wrong key refused in Metabase's words; the
    /// questions simplified, one read, its result as rows held to the most and as a CSV saved into a folder; the Sample Database
    /// and its fields; the example metrics, one read; the alerts. Fails, never skips, when Metabase is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MetabaseConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Key => new() { ["password"] = MetabaseTestEndpoint.ApiKey };

        private static HostConnectorEgress Egress => new(new[] { MetabaseTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters, IConnectorFiles? files = null) =>
            Contexts.DrainAsync(new MetabaseConnector(), Contexts.Extract(Egress, MetabaseTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Key, timeoutSeconds: 60) with { Files = files });

        [Fact]
        public async Task A_test_signs_in_by_key_or_password_and_refuses_wrong_ones()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new MetabaseConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), MetabaseTestEndpoint.Endpoint, credentials: Key, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var byKey = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(Egress, MetabaseTestEndpoint.Endpoint, credentials: Key, timeoutSeconds: 30));
            output.WriteLine($"[live] {byKey.Success}: {byKey.Message}");
            Assert.Equal((true, "Metabase answers: signed in as kafe tests."), (byKey.Success, byKey.Message));
            var byPassword = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(Egress, MetabaseTestEndpoint.Endpoint + "/api", credentials: new Dictionary<string, string> { ["username"] = MetabaseTestEndpoint.User, ["password"] = MetabaseTestEndpoint.Password }, timeoutSeconds: 30));
            Assert.Equal((true, "Metabase answers: signed in as Kafé Test."), (byPassword.Success, byPassword.Message));

            var wrongPassword = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(Egress, MetabaseTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["username"] = MetabaseTestEndpoint.User, ["password"] = "not-" + MetabaseTestEndpoint.Password }, timeoutSeconds: 30));
            output.WriteLine($"[live] wrong password: {wrongPassword.Message}");
            Assert.False(wrongPassword.Success);
            Assert.StartsWith("Metabase refused the sign-in (HTTP 401)", wrongPassword.Message, StringComparison.Ordinal);
            var wrongKey = await new MetabaseConnector().TestConnectionAsync(Contexts.Test(Egress, MetabaseTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["password"] = "mb_not-a-key" }, timeoutSeconds: 30));
            output.WriteLine($"[live] wrong key: {wrongKey.Message}");
            Assert.False(wrongKey.Success);
            Assert.StartsWith("Metabase answered HTTP 401", wrongKey.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Questions_results_databases_metrics_and_alerts_are_read()
        {
            var questions = await Run("questions", new());
            Assert.True(questions.Count >= 10, $"only {questions.Count} questions");
            Assert.All(questions, question => Assert.False(question.Fields.ContainsKey("result_metadata")));
            var asked = questions.First(question => (string?)question.Fields["type"] == "question" && (string?)question.Fields["display"] is "line" or "bar" or "table");
            var id = asked.Fields["id"]!.ToString()!;
            Assert.Equal(asked.Fields["name"], Assert.Single(await Run("question", new() { ["question"] = id })).Fields["name"]);
            Assert.Equal(2, (await Run("questions", new() { ["max"] = "2" })).Count);

            var rows = await Run("question_result", new() { ["question"] = id, ["max"] = "3" });
            Assert.InRange(rows.Count, 1, 3);
            Assert.NotEmpty(rows[0].Fields);
            var folder = new MemoryFolder();
            var saved = Assert.Single(await Run("question_result", new() { ["question"] = id, ["format"] = "csv", ["file"] = "kassa/" }, folder)).Fields;
            Assert.Equal($"kassa/question-{id}.csv", saved["file"]);
            Assert.Contains(',', Encoding.UTF8.GetString(folder.Written[$"kassa/question-{id}.csv"]).Split('\n')[0]); // a header of columns
            var xlsx = new MemoryFolder();
            await Run("question_result", new() { ["question"] = id, ["format"] = "xlsx", ["file"] = "kassa/result.xlsx" }, xlsx);
            Assert.Equal(new byte[] { 0x50, 0x4B }, xlsx.Written["kassa/result.xlsx"][..2]); // a zip, as an xlsx is

            var sample = Assert.Single(await Run("databases", new()), database => (string?)database.Fields["engine"] == "sqlite").Fields; // 0.63's Sample Database is SQLite
            Assert.Equal("Sample Database", sample["name"]);
            Assert.Contains(await Run("database_fields", new() { ["database"] = sample["id"]!.ToString()! }), field => (string?)field.Fields["table_name"] == "ORDERS");
            var metrics = await Run("metrics", new());
            Assert.NotEmpty(metrics);
            Assert.Equal(metrics[0].Fields["name"], Assert.Single(await Run("metric", new() { ["metric"] = metrics[0].Fields["id"]!.ToString()! })).Fields["name"]);
            output.WriteLine($"[live] {questions.Count} questions, {metrics.Count} metrics, {(await Run("alerts", new())).Count} alerts");
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("question", new() { ["question"] = "99999" }));
            Assert.StartsWith("Metabase answered HTTP 404", missing.Message, StringComparison.Ordinal);
        }
    }
}
