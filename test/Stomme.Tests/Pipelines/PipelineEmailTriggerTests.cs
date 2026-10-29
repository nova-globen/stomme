#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Imap;
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
    /// n8n's Email Trigger (IMAP) as "Email received": a published pipeline watching a mailbox through a connection is
    /// started once for each message that arrives - the message its trigger's result - and never for one already there when it
    /// was published; marked read only when it says, and only what it handed on; its attachments kept in the pipeline folder
    /// when it says; at most so many a look; a mailbox made again watched afresh; a mailbox the publish could not reach recorded
    /// by the next look; a mailbox no trigger watches forgotten. The engine with the module's step, task and handler; the port
    /// runs the real IMAP connector against a scripted IMAP server.
    /// </summary>
    public sealed class PipelineEmailTriggerTests : IAsyncLifetime
    {
        private const string User = "orders@example.com";
        private const string Password = "imap-trigger-password";
        private static readonly Guid ConnectionId = Guid.Parse("6f9b7d1e-2c4a-4b8e-9f1d-3a5c7e9b1d2f");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-email-trigger-" + Guid.NewGuid().ToString("N"));
        private readonly PipelineTriggerOptions _options = new();
        private readonly ScriptedImapServer _server = new(User, Password);
        private ImapPort _port = default!;
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            _port = new ImapPort(_server);
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
            _server.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        /// <summary>The extraction port as the Connectivity module would answer it: the real IMAP connector against the scripted server, up to the cap.</summary>
        private sealed class ImapPort(ScriptedImapServer server) : IConnectorExtractionService
        {
            public ConcurrentQueue<string> Operations { get; } = new();

            public bool Down { get; set; }

            public async Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default)
            {
                Operations.Enqueue(operation + (parameters.TryGetValue("uids", out var uids) ? " " + uids : ""));
                if (Down || connectionId != ConnectionId)
                {
                    return ConnectorExtractionResult.Fail(ConnectorOutcome.Failed);
                }

                if (!new ImapConnector().Operations.Any(declared => declared.Name == operation))
                {
                    return ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined); // as the real port answers an operation the connector does not declare
                }

                var context = new ConnectorExtractContext("mail.example.com", new Dictionary<string, string> { ["port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["encryption"] = "none" },
                    TimeSpan.FromSeconds(10), new StubEgress(), operation ?? "read", parameters) { Credentials = new Dictionary<string, string> { ["username"] = User, ["password"] = Password } };
                var records = new List<ConnectorRecord>();
                await foreach (var record in new ImapConnector().ExtractAsync(context, cancellationToken))
                {
                    if (records.Count == maxRecords)
                    {
                        return ConnectorExtractionResult.Ok(records, truncated: true, maxRecords); // read one past the cap, as the real port does
                    }

                    records.Add(record);
                }

                return ConnectorExtractionResult.Ok(records, truncated: false, maxRecords);
            }
        }

        private static async Task<string> PublishAsync(IServiceProvider services, bool markRead = false, bool keepAttachments = false, string connection = "6f9b7d1e-2c4a-4b8e-9f1d-3a5c7e9b1d2f")
        {
            var trigger = new EmailReceived { Id = "mail", Connection = new(connection), Mailbox = new("INBOX"), MarkRead = new(markRead), KeepAttachments = new(keepAttachments) };
            trigger.SetCanStartPipeline(true);
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger } });
            definition.Name = "Mail in";
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
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

        // The messages each run of the pipeline was started with, oldest run first.
        private static async Task<JsonNode[]> StartedWithAsync(IServiceProvider services, string definitionId) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }))
                .OrderBy(run => run.CreatedAt)
                .Select(run => JsonNode.Parse(KeepRunInput.Of(run.PipelineState)[EmailReceived.MessageInputKey].ToString()!)!)
                .ToArray();

        private static string[] Subjects(JsonNode[] messages) => messages.Select(message => message["subject"]!.GetValue<string>()).ToArray();

        [Fact]
        public async Task A_message_arriving_starts_one_run_with_it_and_one_already_there_does_not_and_nothing_is_marked()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            _server.Add(Message("There first"));
            var watching = await PublishAsync(services);
            Assert.Contains("status", _port.Operations); // the publish recorded where the mailbox was

            var uid = _server.Add(Message("Invoice 1001"));
            await task.ExecuteAsync(CancellationToken.None);
            await task.ExecuteAsync(CancellationToken.None);
            var started = await StartedWithAsync(services, watching);
            Assert.Equal(new[] { "Invoice 1001" }, Subjects(started));
            var message = started[0];
            Assert.Equal(("INBOX", (long)uid, "\"Grace Supplier\" <grace@supplier.example>", "\"Orders\" <orders@example.com>"),
                (message["mailbox"]!.GetValue<string>(), message["uid"]!.GetValue<long>(), message["from"]!.GetValue<string>(), message["to"]!.GetValue<string>()));
            Assert.Equal("Invoice 1001, the text.", message["text"]!.GetValue<string>().Trim());
            Assert.False(_server.IsSeen(uid));
            Assert.DoesNotContain(_port.Operations, operation => operation.StartsWith("mark_read", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Marked_read_is_only_what_was_handed_on_and_a_look_starts_at_most_so_many()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            _options.EmailTriggers.MaxMessagesPerLook = 2;
            var watching = await PublishAsync(services, markRead: true);
            var first = _server.Add(Message("One"));
            var second = _server.Add(Message("Two"));
            var third = _server.Add(Message("Three"));

            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "One", "Two" }, Subjects(await StartedWithAsync(services, watching)));
            Assert.Equal((true, true, false), (_server.IsSeen(first), _server.IsSeen(second), _server.IsSeen(third))); // the third was read past the cap - never marked
            Assert.Contains($"mark_read {first},{second}", _port.Operations);

            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "One", "Two", "Three" }, Subjects(await StartedWithAsync(services, watching)));
            Assert.True(_server.IsSeen(third));
        }

        [Fact]
        public async Task Attachments_are_kept_in_the_pipeline_folder_when_it_says_and_their_content_never_rides_the_run()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            _options.EmailTriggers.MaxAttachmentBytes = 64;
            var watching = await PublishAsync(services, keepAttachments: true);
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice 1002");
            var uid = _server.Add(Message("Invoice 1002", ("invoice-1002.pdf", pdf)));
            _server.Add(Message("Large", ("scan.pdf", new byte[200])));

            await task.ExecuteAsync(CancellationToken.None);
            var started = await StartedWithAsync(services, watching);
            var kept = started[0]["attachments"]!.AsArray().Single()!;
            var path = kept["path"]!.GetValue<string>();
            Assert.Matches($"^mail/[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}/{uid}-[0-9a-f]{{8}}-invoice-1002\\.pdf$", path);
            Assert.Equal(pdf, File.ReadAllBytes(Path.Combine(_root, "default", path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(("invoice-1002.pdf", "application/pdf", (long)pdf.Length), (kept["name"]!.GetValue<string>(), kept["contentType"]!.GetValue<string>(), kept["size"]!.GetValue<long>()));
            Assert.Null(kept["content"]);
            var skipped = started[1]["attachments"]!.AsArray().Single()!;
            Assert.Equal(("larger than 64 bytes", null), (skipped["skipped"]!.GetValue<string>(), skipped["path"]));
        }

        [Fact]
        public async Task A_mailbox_made_again_is_watched_afresh_and_one_the_publish_could_not_reach_is_recorded_by_the_next_look()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedMailboxesTask>();
            _port.Down = true;
            _server.Add(Message("Before anything"));
            var watching = await PublishAsync(services);
            _port.Down = false;
            _server.Add(Message("While it could not be reached"));

            await task.ExecuteAsync(CancellationToken.None); // records, starts nothing
            Assert.Empty(await StartedWithAsync(services, watching));
            _server.Add(Message("After"));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "After" }, Subjects(await StartedWithAsync(services, watching)));

            // Made again: another UIDVALIDITY - the mailbox is anchored afresh and its messages start nothing; the next one does.
            _server.UidValidity = 8;
            _server.Add(Message("Renumbered"));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Single(await StartedWithAsync(services, watching));
            _server.Add(Message("Next"));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "After", "Next" }, Subjects(await StartedWithAsync(services, watching)));
        }

        [Fact]
        public async Task A_mailbox_no_trigger_watches_is_forgotten_and_the_connection_is_held_to_the_rules()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            await PublishAsync(services);
            var stale = WatchedMailboxes.SeenKey(null, "a-hash-no-trigger-has");
            await keyValues.SaveAsync(new Stomme.Engine.KeyValues.Entities.SerializedKeyValuePair { Key = stale, SerializedValue = "{}" }, CancellationToken.None);
            await services.GetRequiredService<WatchedMailboxesTask>().ExecuteAsync(CancellationToken.None);
            var kept = (await keyValues.FindManyAsync(new KeyValueFilter { Key = WatchedMailboxes.SeenKeyPrefix, StartsWith = true }, CancellationToken.None)).Select(pair => pair.Key).ToList();
            Assert.DoesNotContain(stale, kept);
            Assert.Single(kept);

            Assert.Equal("'inbox' is not a connection id - choose one of the site's IMAP or Gmail connections.", EmailReceived.ConnectionProblem("inbox"));
            Assert.Equal(new EmailReceivedStimulus(ConnectionId.ToString("D"), "INBOX", true, false), EmailReceived.Stimulus(" " + ConnectionId.ToString("B") + " ", " ", true, false));
            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "mail", Type = PipelineEmailEvent.StepType, Inputs = { ["Connection"] = "inbox" } } } };
            Assert.StartsWith("'inbox' is not a connection id", Assert.Throws<ArgumentException>(() =>
                PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())).Message);
        }

        // n8n's "Fetch test event": the newest message, shaped as a run gets it - its attachment named, not kept - and nothing marked;
        // a mailbox with none, a connection that is no id and a server that cannot be reached said so.
        [Fact]
        public async Task The_newest_message_is_fetched_for_a_test_and_nothing_is_marked_or_kept()
        {
            using var scope = _services.CreateScope();
            var fetch = new FetchNewestEmailQueryHandler(scope.ServiceProvider);
            Assert.Equal("'INBOX' holds no message to test with.", (await fetch.Handle(new FetchNewestEmailQuery(ConnectionId.ToString(), null), CancellationToken.None)).Problem);

            _server.Add(Message("Older"));
            var newest = _server.Add(Message("Invoice 1004", ("invoice-1004.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 1004"))));
            var fetched = await fetch.Handle(new FetchNewestEmailQuery(ConnectionId.ToString(), "INBOX"), CancellationToken.None);
            var message = JsonNode.Parse(fetched.Value!)!;
            Assert.Equal(("Invoice 1004", (long)newest, "invoice-1004.pdf", null), (message["subject"]!.GetValue<string>(), message["uid"]!.GetValue<long>(),
                message["attachments"]![0]!["name"]!.GetValue<string>(), message["attachments"]![0]!["path"]));
            Assert.False(_server.IsSeen(newest));
            Assert.DoesNotContain(_port.Operations, operation => operation.StartsWith("mark_read", StringComparison.Ordinal));
            Assert.False(Directory.Exists(_root));

            Assert.StartsWith("'inbox' is not a connection id", (await fetch.Handle(new FetchNewestEmailQuery("inbox", null), CancellationToken.None)).Problem);
            _port.Down = true;
            Assert.StartsWith("'INBOX' could not be read: ", (await fetch.Handle(new FetchNewestEmailQuery(ConnectionId.ToString(), null), CancellationToken.None)).Problem);
        }

        [Fact]
        public void The_sample_is_a_message_with_its_attachment_kept_when_the_trigger_keeps_them()
        {
            var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
            var kept = JsonNode.Parse(PipelineEmailEvent.Sample("Invoices", keepAttachments: true, now))!;
            Assert.Equal(("Invoices", "Invoice 1001", "mail/2026-10-10/1001-invoice-1001.pdf"),
                (kept["mailbox"]!.GetValue<string>(), kept["subject"]!.GetValue<string>(), kept["attachments"]![0]!["path"]!.GetValue<string>()));
            Assert.Null(JsonNode.Parse(PipelineEmailEvent.Sample(null, keepAttachments: false, now))!["attachments"]![0]!["path"]);
        }
    }
}
