#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Gmail Trigger as "Email received" on a Gmail connection, DRIVEN on an installed SQLite host against a
    /// loopback stand-in for Google's token endpoint and the Gmail API: a Gmail connection made over the API, its secrets vaulted;
    /// a pipeline watching INBOX - marking read - published while a message is already there; a message added to INBOX after
    /// starts ONE run, by the real task through the real port and connector, whose next step writes what it was handed; the
    /// message is marked read by its Gmail id; the one there first neither runs nor is marked; the editor's Fetch test event reads
    /// the label's newest message; neither secret is in the graph or the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GmailTriggerDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_message_added_to_the_label_starts_one_run_and_is_marked_read_by_its_id()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            var refresh = "refresh-" + Guid.NewGuid().ToString("N");
            using var gmail = new GmailStandIn();
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["EmailTriggers"] = new JsonObject { ["IntervalSeconds"] = 5 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Orders mailbox", ConnectorType = "gmail", Endpoint = $"http://127.0.0.1:{gmail.Port}/gmail/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{gmail.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connectionId = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id;

                var before = gmail.Add("There first");
                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders by Gmail" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-email", Name = "email" } },
                    Steps =
                    {
                        new PipelineGraphStep
                        {
                            Id = "mail", Type = PipelineEmailEvent.StepType, Name = "An order arrives",
                            Inputs = { ["Connection"] = connectionId.ToString(), ["MarkRead"] = "true" },
                            OutputVariables = { ["Result"] = "v-email" },
                        },
                        new PipelineGraphStep
                        {
                            Id = "write", Type = "Stomme.WriteTextFile", Name = "Note it",
                            InputExpressions = { ["Path"] = "gmail-seen/{{ email.uid }}.txt", ["Content"] = "{{ email.subject }} | {{ email.from }}" },
                        },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "mail", TargetId = "write" } },
                };
                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);

                var (order, point) = gmail.AddAt("Order 1001");
                PipelineRunView? run = null;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (run is not { IsFinished: true } && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    run = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!.SingleOrDefault();
                }

                Assert.Equal("Finished", run?.SubStatus);
                var written = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/files/content?path=gmail-seen/{point}.txt", output, "what the run wrote");
                Assert.Equal((HttpStatusCode.OK, "Order 1001 | \"Ada\" <ada@example.com>"), (written.Status, written.Body));
                // Marked once the message is handed on to its run, so the mark can land after the run has finished (it did, under a full gate's load).
                for (var waited = 0; waited < 100 && gmail.Marked.Count == 0; waited++)
                {
                    await Task.Delay(100);
                }

                Assert.Equal(new[] { order }, gmail.Marked); // marked read by its Gmail id; the one there first neither ran nor was marked
                Assert.DoesNotContain(before, gmail.Marked);
                await Task.Delay(TimeSpan.FromSeconds(6)); // another look: nothing more
                Assert.Single(JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!);

                // n8n's "Fetch test event": the label's newest message, read through the connection for the editor.
                var newest = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/triggers/email/newest?connection={connectionId}&mailbox=INBOX", output, "the newest message");
                Assert.True(newest.Status == HttpStatusCode.OK, newest.Status + " " + newest.Body);
                Assert.Equal("Order 1001", JsonNode.Parse(JsonNode.Parse(newest.Body)!["value"]!.GetValue<string>())!["subject"]!.GetValue<string>());

                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run!.Id}/steps", output, "the run's steps")).Body, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the Gmail API: INBOX's history growing a point at a time.</summary>
        private sealed class GmailStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(long Point, string Id, string Raw)> _added = new();
            private readonly List<string> _marked = new();
            private long _historyId = 2000;

            public GmailStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<string> Marked
            {
                get
                {
                    lock (_gate)
                    {
                        return _marked.ToArray();
                    }
                }
            }

            public string Add(string subject) => AddAt(subject).Id;

            public (string Id, long Point) AddAt(string subject)
            {
                var message = new MimeMessage { Subject = subject };
                message.From.Add(new MailboxAddress("Ada", "ada@example.com"));
                message.To.Add(new MailboxAddress("Orders", "orders@example.com"));
                message.Body = new TextPart("plain") { Text = subject + ", please." };
                using var bytes = new MemoryStream();
                message.WriteTo(bytes);
                var raw = Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                lock (_gate)
                {
                    _historyId += 11;
                    var id = "m" + _historyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _added.Add((_historyId, id, raw));
                    return (id, _historyId);
                }
            }

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var path = context.Request.Url!.AbsolutePath;
                    var query = context.Request.QueryString;
                    int status;
                    string answer;
                    lock (_gate)
                    {
                        (status, answer) = path switch
                        {
                            "/token" => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                            "/gmail/v1/users/me/profile" when context.Request.Headers["Authorization"] == "Bearer access-drive" =>
                                (200, $$"""{"emailAddress":"orders@example.com","messagesTotal":{{_added.Count}},"historyId":"{{_historyId}}"}"""),
                            "/gmail/v1/users/me/history" => (200, new JsonObject
                            {
                                ["history"] = new JsonArray(_added.Where(added => added.Point > long.Parse(query["startHistoryId"]!, System.Globalization.CultureInfo.InvariantCulture))
                                    .Select(added => (JsonNode?)new JsonObject
                                    {
                                        ["id"] = added.Point.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = added.Id, ["labelIds"] = new JsonArray("INBOX", "UNREAD") } }),
                                    }).ToArray()),
                                ["historyId"] = _historyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            }.ToJsonString()),
                            "/gmail/v1/users/me/messages" => (200, _added.Count == 0 ? "{}" : $$"""{"messages":[{"id":"{{_added[^1].Id}}"}]}"""),
                            "/gmail/v1/users/me/messages/batchModify" => Mark(body),
                            _ when _added.FirstOrDefault(added => path == "/gmail/v1/users/me/messages/" + added.Id) is { Id: { } id } found =>
                                (200, $$"""{"id":"{{id}}","threadId":"t-{{id}}","historyId":"{{found.Point}}","labelIds":["INBOX","UNREAD"],"raw":"{{found.Raw}}"}"""),
                            _ => (404, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                        };
                    }

                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    if (bytes.Length > 0)
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }

                    context.Response.Close();
                }
            }

            private (int, string) Mark(string body)
            {
                foreach (var id in JsonNode.Parse(body)!["ids"]!.AsArray())
                {
                    _marked.Add(id!.GetValue<string>());
                }

                return (204, "");
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
