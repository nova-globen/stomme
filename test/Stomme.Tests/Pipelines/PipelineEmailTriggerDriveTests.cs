#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Connectors;
using Microsoft.AspNetCore.Builder;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Email Trigger (IMAP) as "Email received", DRIVEN on an installed SQLite host against a scripted IMAP server
    /// on loopback: the Host allows loopback, an IMAP connection is made over the API with its password vaulted and tested
    /// through the host's own egress; a pipeline watching its INBOX - marking read, keeping attachments - is published while a
    /// message is already there; a message with a PDF arriving after starts ONE run, by the real task through the real port and
    /// connector, whose next step writes what it was handed - read back through the Files API - its attachment kept in the
    /// pipeline folder, and the message marked read on the server; the message there first neither runs nor is marked; the
    /// password is in neither the pipeline's graph nor its run's steps.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineEmailTriggerDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string User = "orders@example.com";
        private const string Password = "imap-drive-password-not-in-any-graph";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_message_arriving_starts_one_run_through_the_vaulted_connection_and_is_marked_read()
        {
            using var server = new ScriptedImapServer(User, Password);
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
                    Name = "Orders mailbox", ConnectorType = "imap", Endpoint = "127.0.0.1:" + server.Port, TimeoutSeconds = 10,
                    Settings = new() { ["encryption"] = "none" }, Credentials = new() { ["username"] = User, ["password"] = Password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connectionId = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id;
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connectionId}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body); // the host says it in its own words

                var before = server.Add(Message("There first"));
                var definitionId = await PublishAsync(Send, connectionId);
                var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice 1003");
                var invoice = server.Add(Message("Invoice 1003", ("invoice-1003.pdf", pdf)));

                PipelineRunView? run = null;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (run is not { IsFinished: true } && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    run = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!.SingleOrDefault();
                }

                Assert.Equal("Finished", run?.SubStatus);
                var written = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/files/content?path=mail-seen/{invoice}.txt", output, "what the run wrote");
                Assert.Equal((HttpStatusCode.OK, "Invoice 1003 | \"Grace Supplier\" <grace@supplier.example> | invoice-1003.pdf"), (written.Status, written.Body));

                // The attachment kept in the pipeline folder, under mail/ - its bytes those sent.
                var day = JsonSerializer.Deserialize<PipelineFolderView>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/files?folder=mail/{DateTime.UtcNow:yyyy-MM-dd}", output, "the day's mail")).Body, Json)!;
                var kept = Assert.Single(day.Entries);
                Assert.Matches($"^{invoice}-[0-9a-f]{{8}}-invoice-1003\\.pdf$", kept.Name);
                Assert.Equal(pdf.Length, kept.Length);

                // Marked read on the server - once handed on to its run, so possibly after the run has finished; the message there
                // first neither ran nor was marked.
                for (var waited = 0; waited < 100 && !server.IsSeen(invoice); waited++)
                {
                    await Task.Delay(100);
                }

                Assert.True(server.IsSeen(invoice));
                Assert.False(server.IsSeen(before));
                await Task.Delay(TimeSpan.FromSeconds(6)); // another look: nothing more
                Assert.Single(JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!);

                // n8n's "Fetch test event": the newest message read through the connection for the editor - named attachment, nothing kept.
                var newest = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/triggers/email/newest?connection={connectionId}&mailbox=INBOX", output, "the newest message");
                Assert.True(newest.Status == HttpStatusCode.OK, newest.Status + " " + newest.Body);
                var fetched = JsonNode.Parse(JsonNode.Parse(newest.Body)!["value"]!.GetValue<string>())!;
                Assert.Equal(("Invoice 1003", (long)invoice, null), (fetched["subject"]!.GetValue<string>(), fetched["uid"]!.GetValue<long>(), fetched["attachments"]![0]!["path"]));
                Assert.Single(JsonSerializer.Deserialize<PipelineFolderView>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/files?folder=mail/{DateTime.UtcNow:yyyy-MM-dd}", output, "the day's mail again")).Body, Json)!.Entries);

                // The password: in neither the pipeline nor its run.
                Assert.DoesNotContain(Password, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                Assert.DoesNotContain(Password, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run!.Id}/steps", output, "the run's steps")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static MimeMessage Message(string subject, (string Name, byte[] Bytes)? attachment = null)
        {
            var message = new MimeMessage { Subject = subject };
            message.From.Add(new MailboxAddress("Grace Supplier", "grace@supplier.example"));
            message.To.Add(new MailboxAddress("Orders", User));
            var body = new BodyBuilder { TextBody = subject + ", the text." };
            if (attachment is { } file)
            {
                body.Attachments.Add(file.Name, file.Bytes, new ContentType("application", "pdf"));
            }

            message.Body = body.ToMessageBody();
            return message;
        }

        private static async Task<string> PublishAsync(Func<HttpMethod, string, object, string, Task<(HttpStatusCode Status, string Body)>> send, Guid connectionId)
        {
            var created = await send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Invoices by mail" }, "create");
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-email", Name = "email" } },
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "mail", Type = PipelineEmailEvent.StepType, Name = "An invoice arrives",
                        Inputs = { ["Connection"] = connectionId.ToString(), ["MarkRead"] = "true", ["KeepAttachments"] = "true" },
                        OutputVariables = { ["Result"] = "v-email" },
                    },
                    new PipelineGraphStep
                    {
                        Id = "write", Type = "Stomme.WriteTextFile", Name = "Note it",
                        InputExpressions = { ["Path"] = "mail-seen/{{ email.uid }}.txt", ["Content"] = "{{ email.subject }} | {{ email.from }} | {{ email.attachments[0].name }}" },
                    },
                },
                Connections = { new PipelineGraphConnection { SourceId = "mail", TargetId = "write" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "graph")).Status);
            var published = await send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
            Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
            return definitionId;
        }
    }
}
