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
    /// BL-229 - Send email (n8n's Gmail node, its Send and Reply), run by the real engine with the extraction port scripted: the
    /// connection, the connector's <c>send</c> operation and the message's recipients, subject, bodies and reply reach the port -
    /// a body of many lines as one value, the text the previous step's result when no body is given - and the message's id is the
    /// result and an output, its thread another; the mail service's refusal is the step's failure in its words; a connection that
    /// sends no email is said so; the catalog says which connections the step takes. The connector's half is
    /// <c>GmailConnectorTests</c>.
    /// </summary>
    public sealed class SendEmailTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("7b1e5c9f-3d2e-4f8b-8c4a-6e9d2f3b8c01");
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

        [Step("Stomme.Tests", "Tests", "Captures the last result (email).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        [Step("Stomme.Tests", "Tests", "Emits a fixed result (email).")]
        public sealed class Emit : CodeStep<string>
        {
            protected override void Execute(StepExecutionContext context) => context.Set(Result, "Order 42 shipped.\nIt arrives Tuesday.");
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
        public async Task The_previous_result_is_sent_as_the_text_through_the_connection_and_the_messages_id_is_the_result()
        {
            _extraction.Result = Record(("id", "18c2f5a1"), ("thread_id", "18c2f000"), ("labels", "[\"SENT\"]"));
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new Emit(),
                    new SendEmail { Connection = new(ConnectionId.ToString()), To = new(" ada@example.com "), Cc = new("lin@example.com"), Bcc = new(" "), Subject = new("Your order") },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_extraction.Calls);
            Assert.Equal((ConnectionId, "send", 1), (call.ConnectionId, call.Operation, call.MaxRecords));
            Assert.Equal(new Dictionary<string, string> { ["to"] = "ada@example.com", ["cc"] = "lin@example.com", ["subject"] = "Your order", ["text"] = "Order 42 shipped.\nIt arrives Tuesday." }, call.Parameters);
            Assert.Equal("18c2f5a1", _services.GetRequiredService<ResultSink>().Value);
            Assert.Equal(("18c2f5a1", "18c2f000"), (OutputsOf<SendEmail>(run)["MessageId"], OutputsOf<SendEmail>(run)["ThreadId"]));
        }

        [Fact]
        public async Task An_html_body_is_sent_as_it_is_and_a_reply_needs_no_recipient()
        {
            _extraction.Result = Record(("id", "m2"), ("thread_id", "t1"));
            await RunAsync(new Sequence
            {
                Steps =
                {
                    new Emit(),
                    new SendEmail { Connection = new(ConnectionId.ToString()), ReplyTo = new("m1"), Html = new("<p>Shipped.</p>\n<p>Tuesday.</p>") },
                },
            });

            // HTML alone: the previous result is not made its text as well.
            Assert.Equal(new Dictionary<string, string> { ["reply_to"] = "m1", ["html"] = "<p>Shipped.</p>\n<p>Tuesday.</p>" }, Assert.Single(_extraction.Calls).Parameters);

            await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada@example.com"), Text = new("Shipped."), Html = new("<p>Shipped.</p>") });
            Assert.Equal(new Dictionary<string, string> { ["to"] = "ada@example.com", ["html"] = "<p>Shipped.</p>", ["text"] = "Shipped." }, _extraction.Calls[1].Parameters);
        }

        [Fact]
        public async Task The_mail_services_refusal_is_the_failure_in_its_words_and_a_connection_that_sends_none_is_said_so()
        {
            _extraction.Result = Record(("refused", "Gmail: 'ada' is not a list of addresses - for to."));
            var refused = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("MESSAGE_REFUSED: Gmail: 'ada' is not a list of addresses - for to.", OutputsOf<SendEmail>(refused)["Error"]);

            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined);
            var notion = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada@example.com"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("EXTRACTION_OPERATIONUNDEFINED: The connection's connector does not send email - choose a Gmail or a Microsoft Outlook one.", OutputsOf<SendEmail>(notion)["Error"]);

            _extraction.Result = Record(("id_of_something", "x"));
            var odd = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada@example.com"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("MESSAGE_UNSUPPORTED: The connection's connector does not send email - choose a Gmail or a Microsoft Outlook one.", OutputsOf<SendEmail>(odd)["Error"]);

            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.DeniedByPolicy);
            var denied = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada@example.com"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("EXTRACTION_DENIEDBYPOLICY: " + ConnectorOutcomes.Message(ConnectorOutcome.DeniedByPolicy), OutputsOf<SendEmail>(denied)["Error"]);
        }

        [Fact]
        public async Task Nothing_reaches_the_port_without_a_connection_a_recipient_or_a_body()
        {
            var badId = await RunAsync(new SendEmail { Connection = new("mailbox"), To = new("ada@example.com"), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("CONNECTION_INVALID:", (string)OutputsOf<SendEmail>(badId)["Error"]!);
            var nobody = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new(" "), Text = new("hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TO_MISSING:", (string)OutputsOf<SendEmail>(nobody)["Error"]!);
            var empty = await RunAsync(new SendEmail { Connection = new(ConnectionId.ToString()), To = new("ada@example.com"), Html = new(" "), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TEXT_MISSING:", (string)OutputsOf<SendEmail>(empty)["Error"]!);
            Assert.Empty(_extraction.Calls);
        }

        [Fact]
        public void The_catalog_says_which_connections_it_takes()
        {
            using var scope = _services.CreateScope();
            var email = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>()).Single(entry => entry.Type == "Stomme.SendEmail");
            Assert.Equal(("Notifications", "Send email"), (email.Category, email.DisplayName));
            Assert.Equal(new[] { "gmail", "outlook" }, email.Inputs.Single(input => input.Name == "Connection").ConnectorTypes);
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
