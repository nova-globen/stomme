#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    /// BL-95's residue "a Connection with a vaulted secret": the Extract from connection step over the extraction port,
    /// run by the real engine with the port scripted - the connection id, operation, parameters and the clamped cap reach the
    /// port; the records become the step's result for the next step; a refused extraction is the port's category; a
    /// truncated extract says so. The installed drive is <see cref="ExtractFromConnectionDriveTests"/>.
    /// </summary>
    public sealed class ExtractFromConnectionTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("7b5f1a52-4b0a-4bd2-9e7c-2f0f5b1c3d11");
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

        [Step("Stomme.Tests", "Tests", "Captures the last result (extraction).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        [Fact]
        public async Task The_records_become_the_result_and_the_port_gets_the_connection_operation_parameters_and_the_clamped_cap()
        {
            _extraction.Result = ConnectorExtractionResult.Ok(new[] { Record(1), Record(2) }, truncated: false, cap: 100_000);

            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new ExtractFromConnection { Connection = new(ConnectionId.ToString()), Operation = new(" list "), Parameters = new("region = north\nlimit=2"), MaxRecords = new(500_000) },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_extraction.Calls);
            Assert.Equal((ConnectionId, "list", ConnectorExtractionLimits.MaxMaxRecords), (call.ConnectionId, call.Operation, call.MaxRecords));
            Assert.Equal(new Dictionary<string, string> { ["region"] = "north", ["limit"] = "2" }, call.Parameters);
            var records = JsonNode.Parse(_services.GetRequiredService<ResultSink>().Value!)!.AsArray();
            Assert.Equal(new long[] { 1, 2 }, records.Select(record => record!["id"]!.GetValue<long>()));
            var outputs = OutputsOf<ExtractFromConnection>(run);
            Assert.Equal((2, false), ((int)outputs["RecordCount"], (bool)outputs["Truncated"]));
        }

        // A SQL statement is seldom one line, and a parameter line holds one - the Query box is the operation's query, as
        // many lines as it takes; given there and as a 'query' line too, the step will not guess which.
        [Fact]
        public async Task The_query_in_its_own_box_is_the_query_parameter_and_given_twice_is_refused()
        {
            await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), Query = new("SELECT *\nFROM people\nWHERE id = @id\n"), Parameters = new("@id = 7") });
            Assert.Equal(new Dictionary<string, string> { ["query"] = "SELECT *\nFROM people\nWHERE id = @id", ["@id"] = "7" }, Assert.Single(_extraction.Calls).Parameters);

            var twice = await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), Query = new("SELECT 1"), Parameters = new("query = SELECT 2"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("PARAMETER_INVALID: The query is given twice: in Query and as a 'query' parameter.", OutputsOf<ExtractFromConnection>(twice)["Error"]);
            Assert.Single(_extraction.Calls);
        }

        [Fact]
        public async Task A_truncated_extract_succeeds_and_says_so()
        {
            _extraction.Result = ConnectorExtractionResult.Ok(new[] { Record(1) }, truncated: true, cap: 1);

            var run = await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), MaxRecords = new(1) });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.True((bool)OutputsOf<ExtractFromConnection>(run)["Truncated"]);
            Assert.Null(Assert.Single(_extraction.Calls).Operation); // empty: the connector's default
            Assert.Contains(run.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Truncated" && entry.Message!.Contains("cap of 1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_refused_extraction_is_the_ports_category_faulting_by_default_or_through_the_failed_port()
        {
            _extraction.Result = ConnectorExtractionResult.Fail(ConnectorOutcome.CredentialsMissing);

            var continued = await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal(PipelineSubStatus.Finished, continued.PipelineState.SubStatus);
            Assert.Equal("EXTRACTION_CREDENTIALSMISSING: " + ConnectorOutcomes.Message(ConnectorOutcome.CredentialsMissing), OutputsOf<ExtractFromConnection>(continued)["Error"]);

            var faulted = await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()) });
            Assert.Equal(PipelineSubStatus.Faulted, faulted.PipelineState.SubStatus);
            Assert.Contains(faulted.PipelineState.Incidents, incident => incident.Message.Contains(ConnectorOutcomes.Message(ConnectorOutcome.CredentialsMissing), StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_bad_connection_id_or_parameter_is_refused_before_the_port_is_asked()
        {
            var badId = await RunAsync(new ExtractFromConnection { Connection = new("not-a-guid"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("CONNECTION_INVALID:", (string)OutputsOf<ExtractFromConnection>(badId)["Error"]!);

            var badParameter = await RunAsync(new ExtractFromConnection { Connection = new(ConnectionId.ToString()), Parameters = new("no equals sign"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("PARAMETER_INVALID:", (string)OutputsOf<ExtractFromConnection>(badParameter)["Error"]!);

            Assert.Empty(_extraction.Calls);
        }

        private static ConnectorRecord Record(long id) => new(new Dictionary<string, object?> { ["id"] = id, ["name"] = "row " + id });

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
