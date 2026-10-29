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
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Microsoft Outlook DRIVEN on an installed SQLite host, with the REAL connector, the Send email step, the host's
    /// egress and the extraction port, against a loopback stand-in for the tenant's token endpoint and Microsoft Graph: an Outlook
    /// connection made over the API, its client secret vaulted and its mailbox a setting, tested; a published pipeline reads the
    /// Inbox's unread messages, answers the first with Send email - a reply Outlook makes in its conversation, the body of two
    /// lines above the original - and marks it read; the stand-in got the client-credentials grant, then each Graph call with the
    /// token as the bearer, every one under the mailbox's address; the secret is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OutlookDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_the_unread_messages_answers_one_in_its_conversation_and_marks_it_read()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            using var microsoft = new GraphStandIn();
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
                    Name = "Orders mailbox", ConnectorType = "outlook", Endpoint = $"http://127.0.0.1:{microsoft.Port}/v1.0", TimeoutSeconds = 10,
                    Settings = new() { ["mailbox"] = "orders@contoso.com" },
                    Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{microsoft.Port}/tenant/oauth2/v2.0/token" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders mailbox" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-mail", Name = "mail" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "search", Type = "Stomme.ExtractFromConnection", Name = "Unread", Inputs = { ["Connection"] = connection, ["Operation"] = "search", ["Parameters"] = "filter = isRead eq false" }, OutputVariables = { ["Result"] = "v-mail" } },
                        new PipelineGraphStep { Id = "answer", Type = "Stomme.SendEmail", Name = "Answer", Inputs = { ["Connection"] = connection, ["ReplyTo"] = "AAMk1", ["Text"] = "It ships today.\nIt arrives Tuesday." } },
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ExtractFromConnection", Name = "Mark read", Inputs = { ["Connection"] = connection, ["Operation"] = "mark_read", ["Parameters"] = "ids = AAMk1" } },
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

                // The client-credentials grant with the app's secret; every Graph call with the token, under the mailbox's address.
                var received = microsoft.Received;
                var grant = received.First(request => request.Path == "/tenant/oauth2/v2.0/token").Body;
                Assert.Contains("grant_type=client_credentials", grant);
                Assert.Contains("client_secret=" + secret, grant);
                var graphCalls = received.Where(request => request.Path.StartsWith("/v1.0/", StringComparison.Ordinal)).ToList();
                Assert.All(graphCalls, request => Assert.Equal(("Bearer access-drive", true), (request.Authorization, request.Path.StartsWith("/v1.0/users/orders@contoso.com/", StringComparison.Ordinal))));

                // The reply Outlook made, the body's two lines intact, sent; then the message marked read.
                var reply = received.Single(request => request.Path.EndsWith("/messages/AAMk1/createReply", StringComparison.Ordinal));
                Assert.Equal("It ships today.\nIt arrives Tuesday.", JsonNode.Parse(reply.Body)!["comment"]!.GetValue<string>());
                Assert.Contains(received, request => request.Method == "POST" && request.Path == "/v1.0/users/orders@contoso.com/messages/AAMkReply1/send");
                Assert.Equal("""{"isRead":true}""", received.Single(request => request.Method == "PATCH").Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Order 1001", steps);   // the message read was the search's result
                Assert.Contains("AAMkReply1", steps);   // and the reply's id the Send email step's
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the tenant's token endpoint and Microsoft Graph: one unread message in the mailbox's Inbox.</summary>
        private sealed class GraphStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public GraphStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
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

                    var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, context.Request.Headers["Authorization"], body));
                    }

                    const string box = "/v1.0/users/orders@contoso.com";
                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/tenant/oauth2/v2.0/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", box + "/mailFolders/inbox") => (200, """{"displayName":"Inbox","totalItemCount":1}"""),
                        ("GET", box + "/mailFolders/inbox/messages") => (200, """{"value":[{"id":"AAMk1","conversationId":"c-1","internetMessageId":"<order-1001@example.com>","receivedDateTime":"2026-10-11T09:30:00Z","from":{"emailAddress":{"name":"Ada","address":"ada@example.com"}},"toRecipients":[],"ccRecipients":[],"replyTo":[],"subject":"Order 1001","body":{"contentType":"text","content":"Twelve, please."},"bodyPreview":"Twelve, please.","isRead":false,"hasAttachments":false}]}"""),
                        ("POST", box + "/messages/AAMk1/createReply") => (201, """{"id":"AAMkReply1","conversationId":"c-1","internetMessageId":"<reply-1@contoso.com>"}"""),
                        ("POST", box + "/messages/AAMkReply1/send") => (202, ""),
                        ("PATCH", box + "/messages/AAMk1") => (200, """{"id":"AAMk1","isRead":true}"""),
                        _ => (404, """{"error":{"code":"ErrorItemNotFound","message":"Not found."}}"""),
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
