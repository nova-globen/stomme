#nullable enable
using System;
using System.Collections.Concurrent;
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
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Gmail;
using Stomme.Modules.Connectors.Shared;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Tests.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Gmail Trigger as "Email received" on a Gmail connection: the same watcher, task and handler as an IMAP
    /// mailbox's, the real Gmail connector against a scripted Gmail - a published pipeline watching a label starts once for each
    /// message added to it since, never for one there before, nor for one added to another label; it marks read by the messages'
    /// Gmail ids; a point Gmail no longer keeps is watched afresh; and the editor's Fetch test event asks Gmail for the label's
    /// newest message.
    /// </summary>
    public sealed class GmailTriggerTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("8c2a4e6f-1b3d-4f5a-9e7c-2d4f6a8b0c13");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-gmail-trigger-" + Guid.NewGuid().ToString("N"));
        private readonly PipelineTriggerOptions _options = new();
        private readonly ScriptedGmail _gmail = new();
        private GmailPort _port = default!;
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            _port = new GmailPort(_gmail);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Files = { Root = _root } }));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(MemoryStore<>));
            services.AddSingleton<IConnectorExtractionService>(_port);
            services.AddNotificationHandler<KeepRunInput, Stomme.Engine.Pipelines.Notifications.PipelineStarted>();
            services.AddScoped<WatchedMailboxes>();
            services.AddScoped<WatchedMailboxesTask>();
            services.AddNotificationHandler<StartWatchingMailboxOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        /// <summary>Gmail as a test sees it: a label's history growing a point at a time, each message raw, the ids marked read.</summary>
        private sealed class ScriptedGmail : HttpMessageHandler
        {
            private readonly object _gate = new();
            private readonly List<(long Point, string Id, string[] Labels, string Raw)> _added = new();

            public long HistoryId { get; set; } = 1000;

            public bool Forgotten { get; set; }

            public ConcurrentQueue<string> Marked { get; } = new();

            public string Add(string subject, string label = "INBOX")
            {
                lock (_gate)
                {
                    HistoryId += 7;
                    var id = "m" + HistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _added.Add((HistoryId, id, new[] { label, "UNREAD" }, Raw(subject)));
                    return id;
                }
            }

            private static string Raw(string subject)
            {
                var message = new MimeMessage { Subject = subject };
                message.From.Add(new MailboxAddress("Grace Supplier", "grace@supplier.example"));
                message.To.Add(new MailboxAddress("Orders", "orders@example.com"));
                message.Body = new TextPart("plain") { Text = subject + ", the text." };
                using var bytes = new MemoryStream();
                message.WriteTo(bytes);
                return Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!;
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                (HttpStatusCode, string) answer;
                lock (_gate)
                {
                    answer = url.AbsolutePath switch
                    {
                        "/token" => (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}"""),
                        "/gmail/v1/users/me/profile" => (HttpStatusCode.OK, $$"""{"emailAddress":"orders@example.com","messagesTotal":{{_added.Count}},"historyId":"{{HistoryId}}"}"""),
                        "/gmail/v1/users/me/history" when Forgotten => (HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                        "/gmail/v1/users/me/history" => (HttpStatusCode.OK, new JsonObject
                        {
                            ["history"] = new JsonArray(_added.Where(added => added.Point > long.Parse(query["startHistoryId"]!, System.Globalization.CultureInfo.InvariantCulture))
                                .Select(added => (JsonNode?)new JsonObject
                                {
                                    ["id"] = added.Point.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = added.Id, ["labelIds"] = new JsonArray(added.Labels.Select(label => (JsonNode?)label).ToArray()) } }),
                                }).ToArray()),
                            ["historyId"] = HistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }.ToJsonString()),
                        "/gmail/v1/users/me/messages" => (HttpStatusCode.OK, _added.LastOrDefault(added => added.Labels.Contains(query["labelIds"])) is { Id: { } newest } ? $$"""{"messages":[{"id":"{{newest}}"}]}""" : "{}"),
                        "/gmail/v1/users/me/messages/batchModify" => Mark(body),
                        _ when url.AbsolutePath.StartsWith("/gmail/v1/users/me/messages/", StringComparison.Ordinal) && _added.FirstOrDefault(added => url.AbsolutePath.EndsWith("/" + added.Id, StringComparison.Ordinal)) is { Id: { } id } found =>
                            (HttpStatusCode.OK, $$"""{"id":"{{id}}","threadId":"t-{{id}}","historyId":"{{found.Point}}","labelIds":["{{found.Labels[0]}}"],"raw":"{{found.Raw}}"}"""),
                        _ => (HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND"}}"""),
                    };
                }

                return new HttpResponseMessage(answer.Item1) { Content = new StringContent(answer.Item2, Encoding.UTF8, "application/json") };
            }

            private (HttpStatusCode, string) Mark(string body)
            {
                foreach (var id in JsonNode.Parse(body)!["ids"]!.AsArray())
                {
                    Marked.Enqueue(id!.GetValue<string>());
                }

                return (HttpStatusCode.NoContent, "");
            }
        }

        /// <summary>The extraction port as the Connectivity module would answer it: the real Gmail connector against the scripted Gmail, up to the cap.</summary>
        private sealed class GmailPort(ScriptedGmail gmail) : IConnectorExtractionService
        {
            public ConcurrentQueue<string> Operations { get; } = new();

            public async Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default)
            {
                Operations.Enqueue(operation ?? "");
                if (connectionId != ConnectionId)
                {
                    return ConnectorExtractionResult.Fail(ConnectorOutcome.ConnectionNotFound);
                }

                var connector = new GmailConnector(new OAuth2TokenProvider());
                if (!connector.Operations.Any(declared => declared.Name == operation))
                {
                    return ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined);
                }

                var context = new ConnectorExtractContext("http://gmail.example.test/gmail/v1", new Dictionary<string, string> { ["token_url"] = "http://gmail.example.test/token" },
                    TimeSpan.FromSeconds(10), new StubEgress(gmail), operation!, parameters)
                {
                    Credentials = new Dictionary<string, string> { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" },
                };
                var records = new List<ConnectorRecord>();
                await foreach (var record in connector.ExtractAsync(context, cancellationToken))
                {
                    if (records.Count == maxRecords)
                    {
                        return ConnectorExtractionResult.Ok(records, truncated: true, maxRecords);
                    }

                    records.Add(record);
                }

                return ConnectorExtractionResult.Ok(records, truncated: false, maxRecords);
            }
        }

        private static async Task<string> PublishAsync(IServiceProvider services, string label = "INBOX", bool markRead = false)
        {
            var trigger = new EmailReceived { Id = "mail", Connection = new(ConnectionId.ToString()), Mailbox = new(label), MarkRead = new(markRead), KeepAttachments = new(false) };
            trigger.SetCanStartPipeline(true);
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger } });
            definition.Name = "Gmail in";
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        private static async Task<JsonNode[]> StartedWithAsync(IServiceProvider services, string definitionId) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }))
                .OrderBy(run => run.CreatedAt)
                .Select(run => JsonNode.Parse(KeepRunInput.Of(run.PipelineState)[EmailReceived.MessageInputKey].ToString()!)!)
                .ToArray();

        private static string[] Subjects(JsonNode[] messages) => messages.Select(message => message["subject"]!.GetValue<string>()).ToArray();

        [Fact]
        public async Task A_message_added_to_the_label_starts_one_run_and_one_there_before_or_in_another_label_does_not()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            _gmail.Add("There first");
            var watching = await PublishAsync(services, markRead: true);
            Assert.Contains("status", _port.Operations); // the publish recorded where the label's history stood

            var invoice = _gmail.Add("Invoice 1001");
            _gmail.Add("A newsletter", label: "CATEGORY_PROMOTIONS");
            await task.ExecuteAsync(CancellationToken.None);
            await task.ExecuteAsync(CancellationToken.None);

            var started = await StartedWithAsync(services, watching);
            Assert.Equal(new[] { "Invoice 1001" }, Subjects(started));
            Assert.Equal(("INBOX", "\"Grace Supplier\" <grace@supplier.example>"), (started[0]["mailbox"]!.GetValue<string>(), started[0]["from"]!.GetValue<string>()));
            Assert.Equal("Invoice 1001, the text.", started[0]["text"]!.GetValue<string>().Trim());
            Assert.Equal(new[] { invoice }, _gmail.Marked.ToArray()); // marked read by its Gmail id

            _gmail.Add("Invoice 1002");
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "Invoice 1001", "Invoice 1002" }, Subjects(await StartedWithAsync(services, watching)));
        }

        [Fact]
        public void The_catalog_offers_email_received_imap_and_gmail_connections()
        {
            using var scope = _services.CreateScope();
            var trigger = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>()).Single(entry => entry.Type == PipelineEmailEvent.StepType);
            Assert.Equal(new[] { "imap", "gmail" }, trigger.Inputs.Single(input => input.Name == "Connection").ConnectorTypes);
        }

        [Fact]
        public async Task A_point_gmail_no_longer_keeps_is_watched_afresh_and_the_test_event_is_the_labels_newest_message()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            var watching = await PublishAsync(services);

            _gmail.Forgotten = true;
            _gmail.Add("While forgotten");
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Empty(await StartedWithAsync(services, watching)); // watched afresh from where it stands: nothing started

            _gmail.Forgotten = false;
            _gmail.Add("After");
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "After" }, Subjects(await StartedWithAsync(services, watching)));

            // n8n's Fetch test event: Gmail asked for the label's newest message, nothing marked.
            var fetched = await new FetchNewestEmailQueryHandler(services).Handle(new FetchNewestEmailQuery(ConnectionId.ToString(), "INBOX"), CancellationToken.None);
            Assert.Null(fetched.Problem);
            Assert.Equal("After", JsonNode.Parse(fetched.Value!)!["subject"]!.GetValue<string>());
            Assert.Equal("newest", _port.Operations.Last()); // the label's newest asked for, not read back from a point
            Assert.Empty(_gmail.Marked);
        }
    }
}
