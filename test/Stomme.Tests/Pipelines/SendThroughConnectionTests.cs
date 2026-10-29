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
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A Connection with a vaulted secret: Send through connection, run by the real
    /// engine with the extraction port scripted - the connection, the connector's <c>send</c> operation and the request's
    /// parameters reach the port (the body the previous step's result when none is given, none with GET), the response body is
    /// the result and the status an output; a refusal is the port's category; a connector that does not answer a send with a
    /// status is refused by name. The connector's half is <c>RestApiConnectorExtractTests</c>.
    /// </summary>
    public sealed class SendThroughConnectionTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("2d1b5e83-6c0f-4a4e-9a51-3b7e0f2c9a41");
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

        [Step("Stomme.Tests", "Tests", "Captures the last result (send).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        [Step("Stomme.Tests", "Tests", "Emits a fixed result (send).")]
        public sealed class Emit : CodeStep<string>
        {
            protected override void Execute(StepExecutionContext context) => context.Set(Result, "{\"event\":\"deployed\"}");
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static ConnectorExtractionResult Answer(long status, string body) =>
            ConnectorExtractionResult.Ok(new[] { new ConnectorRecord(new Dictionary<string, object?> { ["status"] = status, ["body"] = body }) }, truncated: false, cap: 1);

        [Fact]
        public async Task The_previous_result_is_posted_through_the_connection_and_the_response_is_the_result()
        {
            _extraction.Result = Answer(201, "{\"id\":5}");

            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new Emit(),
                    new SendThroughConnection { Connection = new(ConnectionId.ToString()), Path = new(" /v1/events "), Headers = new("X-Source: pipelines") },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_extraction.Calls);
            Assert.Equal((ConnectionId, "send", 1), (call.ConnectionId, call.Operation, call.MaxRecords));
            Assert.Equal(new Dictionary<string, string>
            {
                ["method"] = "POST",
                ["path"] = "/v1/events",
                ["body"] = "{\"event\":\"deployed\"}",
                ["content_type"] = "application/json",
                ["headers"] = "X-Source: pipelines",
            }, call.Parameters);
            Assert.Equal("{\"id\":5}", _services.GetRequiredService<ResultSink>().Value);
            Assert.Equal(201, (int)OutputsOf<SendThroughConnection>(run)["StatusCode"]);
        }

        [Fact]
        public async Task A_GET_carries_no_body()
        {
            _extraction.Result = Answer(200, "[]");

            await RunAsync(new Sequence { Steps = { new Emit(), new SendThroughConnection { Connection = new(ConnectionId.ToString()), Method = new(HttpRequestMethod.GET) } } });

            var call = Assert.Single(_extraction.Calls);
            Assert.Equal("GET", call.Parameters["method"]);
            Assert.False(call.Parameters.ContainsKey("body"));
        }

        [Fact]
        public async Task A_refusal_is_the_ports_category_and_a_connector_without_a_send_is_refused_by_name()
        {
            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.OperationUndefined);
            var undefined = await RunAsync(new SendThroughConnection { Connection = new(ConnectionId.ToString()), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("EXTRACTION_OPERATIONUNDEFINED: " + ConnectorOutcomes.Message(ConnectorOutcome.OperationUndefined), OutputsOf<SendThroughConnection>(undefined)["Error"]);

            _extraction.Result = ConnectorExtractionResult.Ok(new[] { new ConnectorRecord(new Dictionary<string, object?> { ["id"] = 1L }) }, truncated: false, cap: 1);
            var rows = await RunAsync(new SendThroughConnection { Connection = new(ConnectionId.ToString()), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("SEND_UNSUPPORTED:", (string)OutputsOf<SendThroughConnection>(rows)["Error"]!);

            var badId = await RunAsync(new SendThroughConnection { Connection = new("not-a-guid"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("CONNECTION_INVALID:", (string)OutputsOf<SendThroughConnection>(badId)["Error"]!);
            Assert.Equal(2, _extraction.Calls.Count); // the bad id never reached the port
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
