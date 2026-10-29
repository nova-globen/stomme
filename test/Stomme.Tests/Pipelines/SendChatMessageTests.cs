#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Send chat message (n8n's Slack, Telegram and Discord nodes, their message sent), run by the real engine with the
    /// extraction port scripted: the connection, the connectors' <c>message</c> operation and the message's to, text and reply
    /// reach the port - the text the previous step's result when none is given - and the message's id is the result and an
    /// output; the service's refusal is the step's failure in its words; a connection that sends no chat messages is said so;
    /// the catalog says which connections the step takes. The connectors' half is <c>ChatConnectorsTests</c>.
    /// </summary>
    public sealed class SendChatMessageTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("6a0f4b8e-2c1d-4e7a-9b3f-5d8c1e2a7b90");
        private ServiceProvider _services = default!;
        private readonly ScriptedExtraction _extraction = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IConnectorExtractionService>(_extraction);
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<CaptureLastResult>();
                module.AddStep<Emit>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        public sealed class ResultSink
        {
            public string? Value { get; set; }
        }

        [Step("Stomme.Tests", "Tests", "Captures the last result (chat).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        [Step("Stomme.Tests", "Tests", "Emits a fixed result (chat).")]
        public sealed class Emit : CodeStep<string>
        {
            protected override void Execute(StepExecutionContext context) => context.Set(Result, "Order 42 shipped");
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static ConnectorExtractionResult Record(params (string Name, object? Value)[] fields) =>
            ConnectorExtractionResult.Ok(new[] { new ConnectorRecord(fields.ToDictionary(field => field.Name, field => field.Value)) }, truncated: false, cap: 1);

        [Fact]
        public async Task The_previous_result_is_sent_through_the_connection_and_the_messages_id_is_the_result()
        {
            _extraction.Result = Record(("id", "1700000000.000100"), ("to", "C0123"));
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new Emit(),
                    new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new(" #orders "), ReplyTo = new("1699999999.000001") },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_extraction.Calls);
            Assert.Equal((ConnectionId, "message", 1), (call.ConnectionId, call.Operation, call.MaxRecords));
            Assert.Equal(new Dictionary<string, string> { ["to"] = "#orders", ["text"] = "Order 42 shipped", ["reply_to"] = "1699999999.000001" }, call.Parameters);
            Assert.Equal("1700000000.000100", _services.GetRequiredService<ResultSink>().Value);
            Assert.Equal("1700000000.000100", OutputsOf<SendChatMessage>(run)["MessageId"]);
        }

        [Fact]
        public async Task The_services_refusal_is_the_failure_in_its_words_and_a_connection_that_sends_none_is_said_so()
        {
            _extraction.Result = Record(("refused", "Slack: channel_not_found"));
            var refused = await RunAsync(new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new("#nowhere"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("MESSAGE_REFUSED: Slack: channel_not_found", OutputsOf<SendChatMessage>(refused)["Error"]);

            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined);
            var rest = await RunAsync(new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new("#a"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("EXTRACTION_OPERATIONUNDEFINED: The connection's connector does not send chat messages - choose a Slack, Telegram, Discord, Mattermost, Twilio, Microsoft Teams or WhatsApp Business one.", OutputsOf<SendChatMessage>(rest)["Error"]);

            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.DeniedByPolicy);
            var denied = await RunAsync(new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new("#a"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("EXTRACTION_DENIEDBYPOLICY: " + ConnectorOutcomes.Message(ConnectorOutcome.DeniedByPolicy), OutputsOf<SendChatMessage>(denied)["Error"]);
        }

        [Fact]
        public async Task Nothing_reaches_the_port_without_a_connection_somewhere_to_go_or_a_text()
        {
            var badId = await RunAsync(new SendChatMessage { Connection = new("orders-bot"), To = new("#a"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("CONNECTION_INVALID:", (string)OutputsOf<SendChatMessage>(badId)["Error"]!);
            var nowhere = await RunAsync(new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new(" "), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TO_MISSING:", (string)OutputsOf<SendChatMessage>(nowhere)["Error"]!);
            var silent = await RunAsync(new SendChatMessage { Connection = new(ConnectionId.ToString()), To = new("#a"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TEXT_MISSING:", (string)OutputsOf<SendChatMessage>(silent)["Error"]!);
            Assert.Empty(_extraction.Calls);
        }

        [Fact]
        public void The_catalog_says_which_connections_it_takes_and_a_step_naming_none_takes_any()
        {
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            var chat = catalog.Single(entry => entry.Type == "Stomme.SendChatMessage");
            Assert.Equal("Notifications", chat.Category);
            Assert.Equal(new[] { "slack", "telegram", "discord", "mattermost", "twilio", "teams", "whatsapp" }, chat.Inputs.Single(input => input.Name == "Connection").ConnectorTypes);
            Assert.Null(catalog.Single(entry => entry.Type == "Stomme.ExtractFromConnection").Inputs.Single(input => input.UIHint == Stomme.Models.Pipelines.PipelineInputHints.ConnectionPicker).ConnectorTypes);
        }

        private sealed class ScriptedExtraction : IConnectorExtractionService
        {
            public ConnectorExtractionResult Result { get; set; } = ConnectorExtractionResult.Ok(Array.Empty<ConnectorRecord>(), false, 1);

            public List<(Guid ConnectionId, string? Operation, IReadOnlyDictionary<string, string> Parameters, int MaxRecords)> Calls { get; } = new();

            public Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default)
            {
                Calls.Add((connectionId, operation, new Dictionary<string, string>(parameters), maxRecords));
                return Task.FromResult(Result);
            }
        }
    }
}
