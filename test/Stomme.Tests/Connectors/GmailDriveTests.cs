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
using Stomme.Tests.Pipelines;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Gmail DRIVEN on an installed SQLite host, with the REAL connector, the Send email step, the host's egress and the
    /// extraction port, against a loopback stand-in for Google's token endpoint and the Gmail API: a Gmail connection made over the
    /// API, its client secret and refresh token vaulted, tested (the mailbox's address read); a published pipeline reads the unread
    /// messages from a sender, answers the first in its thread with a body of two lines, and marks it read - the stand-in got the
    /// refresh grant, then each call with the access token; neither secret is in the pipeline's graph or its run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GmailDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_a_senders_messages_answers_one_in_its_thread_and_marks_it_read()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            var refresh = "refresh-" + Guid.NewGuid().ToString("N");
            using var google = new GmailStandIn();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
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
                    Name = "Orders mailbox", ConnectorType = "gmail", Endpoint = $"http://127.0.0.1:{google.Port}/gmail/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Contains(google.Received, request => request.Path == "/gmail/v1/users/me/profile");

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders mailbox" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-mail", Name = "mail" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "search", Type = "Stomme.ExtractFromConnection", Name = "Unread from Ada", Inputs = { ["Connection"] = connection, ["Operation"] = "search", ["Parameters"] = "query = from:ada@example.com is:unread\nlabels = INBOX" }, OutputVariables = { ["Result"] = "v-mail" } },
                        new PipelineGraphStep { Id = "answer", Type = "Stomme.SendEmail", Name = "Answer", Inputs = { ["Connection"] = connection, ["ReplyTo"] = "m1", ["Text"] = "It ships today.\nIt arrives Tuesday." } },
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ExtractFromConnection", Name = "Mark read", Inputs = { ["Connection"] = connection, ["Operation"] = "mark_read", ["Parameters"] = "ids = m1" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "search", SourcePort = "Done", TargetId = "answer" },
                        new PipelineGraphConnection { SourceId = "answer", SourcePort = "Done", TargetId = "read" },
                    },
                };
                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);

                // The refresh grant with the client's secret, then every Gmail call with the access token it gave.
                var received = google.Received;
                Assert.Contains("client_secret=" + secret, received.First(request => request.Path == "/token").Body);
                Assert.All(received.Where(request => request.Path.StartsWith("/gmail/", StringComparison.Ordinal)), request => Assert.Equal("Bearer access-drive", request.Authorization));
                Assert.Contains("q=from%3Aada%40example.com%20is%3Aunread", received.Single(request => request.Path == "/gmail/v1/users/me/messages").Query);

                // The answer went in the thread, to Ada, its two lines intact; then the message was marked read.
                var sent = JsonNode.Parse(received.Single(request => request.Path == "/gmail/v1/users/me/messages/send").Body)!;
                var raw = sent["raw"]!.GetValue<string>().Replace('-', '+').Replace('_', '/');
                var reply = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw + new string('=', (4 - (raw.Length % 4)) % 4))));
                Assert.Equal(("t1", "ada@example.com", "Re: Order 1001", "order-1001@example.com"), (sent["threadId"]!.GetValue<string>(), reply.To.Mailboxes.Single().Address, reply.Subject, reply.InReplyTo));
                Assert.Equal("It ships today.\nIt arrives Tuesday.", reply.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
                Assert.Equal("""{"ids":["m1"],"removeLabelIds":["UNREAD"]}""", received.Single(request => request.Path == "/gmail/v1/users/me/messages/batchModify").Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Twelve, please.", steps); // the message read was the search's result
                Assert.Contains("sent-1", steps);          // and the answer's id the Send email step's
                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the Gmail API: one unread message from Ada, m1 in thread t1.</summary>
        private sealed class GmailStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public GmailStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string Query, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Raw()
            {
                var message = new MimeMessage();
                message.From.Add(MailboxAddress.Parse("Ada <ada@example.com>"));
                message.To.Add(MailboxAddress.Parse("orders@example.com"));
                message.Subject = "Order 1001";
                message.MessageId = "order-1001@example.com";
                message.Body = new TextPart("plain") { Text = "Twelve, please." };
                using var bytes = new MemoryStream();
                message.WriteTo(bytes);
                return Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
                    var query = context.Request.Url!.Query;
                    lock (_gate)
                    {
                        _received.Add((context.Request.HttpMethod, path, query, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = path switch
                    {
                        "/token" => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        "/gmail/v1/users/me/profile" => (200, """{"emailAddress":"orders@example.com"}"""),
                        "/gmail/v1/users/me/messages" => (200, """{"messages":[{"id":"m1","threadId":"t1"}]}"""),
                        "/gmail/v1/users/me/messages/m1" when query.Contains("format=raw", StringComparison.Ordinal) => (200, $$"""{"id":"m1","threadId":"t1","labelIds":["INBOX","UNREAD"],"snippet":"Twelve, please.","raw":"{{Raw()}}"}"""),
                        "/gmail/v1/users/me/messages/m1" => (200, """{"id":"m1","threadId":"t1","payload":{"headers":[{"name":"From","value":"Ada <ada@example.com>"},{"name":"Subject","value":"Order 1001"},{"name":"Message-ID","value":"<order-1001@example.com>"}]}}"""),
                        "/gmail/v1/users/me/messages/send" => (200, """{"id":"sent-1","threadId":"t1","labelIds":["SENT"]}"""),
                        "/gmail/v1/users/me/messages/batchModify" => (204, ""),
                        _ => (404, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                    };
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

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
