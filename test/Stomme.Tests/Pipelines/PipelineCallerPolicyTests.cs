#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Reliability;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's "This workflow can be called by": a pipeline says which pipelines may call it, and a call it does not allow
    /// fails at the caller's step, saying why, before the called pipeline runs anything - through the engine as the host composes
    /// it (the check inside the retry, so the step's Failed path takes a refusal). A step test's call is the tested pipeline's.
    /// The setting is kept with the graph, merged as a field of its own, travels in an export and comes in from n8n.
    /// </summary>
    public sealed class PipelineCallerPolicyTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
                PipelineCallerPolicy.UseCallerPolicy(module); // as the host composes it: before the retry, so inside it
                StepReliability.UseStepReliability(module);
            });
            PipelineVariableStorage.Wrap(services);
            StepTestCommitGate.Wrap(services);
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IPipelineDispatcher, InlineDispatcher>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class InlineDispatcher(ICommandSender commands) : IPipelineDispatcher
        {
            public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineDefinitionRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default)
            {
                await commands.SendAsync(new DispatchPipelineDefinitionCommand(request.DefinitionVersionId)
                {
                    Input = request.Input, CorrelationId = request.CorrelationId, InstanceId = request.InstanceId, Properties = request.Properties, ParentPipelineInstanceId = request.ParentPipelineInstanceId,
                }, cancellationToken);
                return DispatchPipelineResponse.Success();
            }

            public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineInstanceRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default)
            {
                await commands.SendAsync(new DispatchPipelineInstanceCommand(request.InstanceId) { StepHandle = request.StepHandle, Input = request.Input }, cancellationToken);
                return DispatchPipelineResponse.Success();
            }

            public Task<DispatchPipelineResponse> DispatchAsync(DispatchTriggerPipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<DispatchPipelineResponse> DispatchAsync(DispatchResumePipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class Guard : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        // Published under the definition id given, so a pipeline can name its callers before they exist.
        private static async Task PublishAsync(IServiceProvider services, string id, string name, PipelineGraph graph)
        {
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            definition.DefinitionId = id;
            definition.Name = name;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
        }

        private static async Task<PipelineInstance> StartAsync(IServiceProvider services, string definitionId)
        {
            var started = await new StartPipelineCommandHandler(
                services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(),
                new PipelineRunStops(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), services.GetRequiredService<ISystemClock>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services))
                .Handle(new StartPipelineCommand(definitionId, null, null), CancellationToken.None);
            return (await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = started!.RunId }))!;
        }

        private static async Task<long> RunsOfAsync(IServiceProvider services, string definitionId) =>
            await services.GetRequiredService<IPipelineInstanceStore>().CountAsync(new PipelineInstanceFilter { DefinitionId = definitionId });

        private string[] Said(string start) => _console.ToString().Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(start, StringComparison.Ordinal)).ToArray();

        // Says it ran; who may call it is the setting given.
        private static PipelineGraph Called(string said, string? calledBy) => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "called", Type = PipelineCallContracts.WhenCalledType },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Inputs = { ["Text"] = said } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "called", TargetId = "say" } },
            CalledBy = calledBy,
        };

        // Calls the pipeline named and says so after; its Failed path, when it has one, says that instead.
        private static PipelineGraph Caller(string called, string said, string callType = "Stomme.ExecutePipeline", bool failedPath = false)
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "call", Type = callType, Inputs = { ["PipelineDefinitionId"] = called, ["WaitForCompletion"] = "true" }, FailedPath = failedPath },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = said + " called" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "call", TargetId = "after" } },
            };
            if (failedPath)
            {
                graph.Steps.Add(new PipelineGraphStep { Id = "refused", Type = "Stomme.WriteLine", Inputs = { ["Text"] = said + " refused" } });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "call", SourcePort = PipelineOutcomes.Failed, TargetId = "refused" });
            }

            return graph;
        }

        [Fact]
        public async Task A_pipeline_is_called_only_by_the_pipelines_it_lets_call_it()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "open", "Open", Called("OPEN ran", calledBy: null));
            await PublishAsync(services, "closed", "Closed", Called("CLOSED ran", PipelineCallers.Nobody));
            await PublishAsync(services, "listed", "Listed", Called("LISTED ran", PipelineCallers.Only(new[] { "caller-listed" })));
            await PublishAsync(services, "caller-open", "To open", Caller("open", "TO OPEN"));
            await PublishAsync(services, "caller-closed", "To closed", Caller("closed", "TO CLOSED"));
            await PublishAsync(services, "caller-listed", "Listed one", Caller("listed", "LISTED ONE"));
            await PublishAsync(services, "caller-other", "Not listed", Caller("listed", "NOT LISTED"));
            await PublishAsync(services, "dispatcher", "Dispatcher", Caller("closed", "DISPATCHER", "Stomme.DispatchPipeline"));

            var open = await StartAsync(services, "caller-open");
            var closed = await StartAsync(services, "caller-closed");
            var listed = await StartAsync(services, "caller-listed");
            var other = await StartAsync(services, "caller-other");
            var dispatcher = await StartAsync(services, "dispatcher");

            Assert.Equal(PipelineSubStatus.Finished, open.SubStatus);
            Assert.Equal(new[] { "TO OPEN called" }, Said("TO OPEN"));
            Assert.Equal(PipelineSubStatus.Finished, listed.SubStatus);
            Assert.Equal(new[] { "LISTED ONE called" }, Said("LISTED ONE"));
            Assert.Single(Said("LISTED ran")); // run once - by the caller it names, not by the other

            foreach (var refused in new[] { closed, dispatcher })
            {
                Assert.Equal(PipelineSubStatus.Faulted, refused.SubStatus);
                Assert.Equal("'Closed' may not be called by another pipeline - its settings say so.", Assert.Single(refused.PipelineState.Incidents).Message);
            }

            Assert.Equal(PipelineSubStatus.Faulted, other.SubStatus);
            Assert.Equal("'Listed' may be called only by the pipelines its settings name, and this pipeline is not one of them.", Assert.Single(other.PipelineState.Incidents).Message);

            // Refused before anything ran: the called pipeline has no run, and nothing after the call ran either.
            Assert.Empty(Said("CLOSED ran"));
            Assert.Equal(0, await RunsOfAsync(services, "closed"));
            Assert.Empty(Said("TO CLOSED"));
            Assert.Empty(Said("NOT LISTED"));
            Assert.Empty(Said("DISPATCHER"));
        }

        // A refused call is a failure of the step like any other: its Failed path takes it.
        [Fact]
        public async Task A_refused_call_takes_the_steps_failed_path()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "private", "Private", Called("PRIVATE ran", PipelineCallers.Nobody));
            await PublishAsync(services, "handles-it", "Handles it", Caller("private", "HANDLES IT", failedPath: true));

            var run = await StartAsync(services, "handles-it");

            Assert.Equal(PipelineSubStatus.Finished, run.SubStatus);
            Assert.Equal(new[] { "HANDLES IT refused" }, Said("HANDLES IT"));
            Assert.Empty(Said("PRIVATE ran"));
        }

        // A step test runs a copy of the graph under an identity of its own: its call is the tested pipeline's.
        [Fact]
        public async Task A_step_test_calls_as_the_pipeline_tested()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await PublishAsync(services, "only-for-tested", "Only for the tested one", Called("TESTED CALL ran", PipelineCallers.Only(new[] { "tested" })));
            var handler = new TestStepCommandHandler(
                services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(), services.GetRequiredService<IIdentityGenerator>(),
                services.GetRequiredService<IStepSerializer>(), services.GetRequiredService<IPipelineRunner>(), services.GetRequiredService<IStepExecutionMapper>(), new Guard());

            var asTested = await handler.Handle(new TestStepCommand(Caller("only-for-tested", "AS TESTED"), "call", null, DefinitionId: "tested"), CancellationToken.None);
            var asAnother = await handler.Handle(new TestStepCommand(Caller("only-for-tested", "AS ANOTHER"), "call", null, DefinitionId: "another"), CancellationToken.None);
            var asNone = await handler.Handle(new TestStepCommand(Caller("only-for-tested", "AS NONE"), "call", null), CancellationToken.None);

            Assert.Equal("Finished", asTested.Status);
            Assert.Single(Said("TESTED CALL ran"));
            foreach (var refused in new[] { asAnother, asNone })
            {
                Assert.Equal("Faulted", refused.Status);
                Assert.Equal("'Only for the tested one' may be called only by the pipelines its settings name, and this pipeline is not one of them.", refused.Error);
            }
        }

        [Fact]
        public void The_setting_is_read_admitting_any_none_or_the_ones_named_and_closed_when_unreadable()
        {
            Assert.Null(PipelineCallers.Normalize(null));
            Assert.Null(PipelineCallers.Normalize(" "));
            Assert.Null(PipelineCallers.Normalize("Any"));
            Assert.Equal(PipelineCallers.Nobody, PipelineCallers.Normalize(" NONE "));
            Assert.Equal("a,b", PipelineCallers.Normalize(" a , b,a,,"));
            Assert.Equal(PipelineCallers.Nobody, PipelineCallers.Normalize(", ,")); // names callers it cannot read: nobody, not everybody
            Assert.Equal(PipelineCallers.Nobody, PipelineCallers.Only(Array.Empty<string>()));
            Assert.Equal("x", PipelineCallers.Only(new[] { "x", "x" }));
            Assert.Equal(new[] { "a", "b" }, PipelineCallers.Ids("a,b"));
            Assert.Empty(PipelineCallers.Ids(PipelineCallers.Nobody));

            Assert.True(PipelineCallers.Admits(null, "anyone"));
            Assert.True(PipelineCallers.Admits(null, null));
            Assert.False(PipelineCallers.Admits(PipelineCallers.Nobody, "anyone"));
            Assert.True(PipelineCallers.Admits("a,b", "b"));
            Assert.False(PipelineCallers.Admits("a,b", "B")); // ids are compared as they are
            Assert.False(PipelineCallers.Admits("a,b", null)); // a pipeline not yet saved is none of them
        }

        [Fact]
        public void The_setting_is_kept_with_the_graph_merged_on_its_own_and_carried_in_an_export()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = Called("KEPT", PipelineCallers.Only(new[] { "b", "a" }));

            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            Assert.Equal("b,a", PipelineGraphCodec.ToGraph(body).CalledBy);
            Assert.Equal("b,a", PipelineGraphCodec.CalledBy(body));
            Assert.Null(PipelineGraphCodec.CalledBy(PipelineGraphCodec.ToBody(Called("OPEN", null), null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())));
            // A setting the body holds as anything but text admits nobody.
            Assert.Equal(PipelineCallers.Nobody, PipelineCallers.Normalize(PipelineGraphCodec.CalledBy(body.Replace("\"calledBy\":\"b,a\"", "\"calledBy\":42", StringComparison.Ordinal))));

            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("b,a", fields["pipeline/called-by"]);
            Assert.Equal("b,a", PipelineGraphFields.ToGraph(fields, graph).CalledBy);
            Assert.DoesNotContain("pipeline/called-by", PipelineGraphFields.ToFields(Called("OPEN", null)).Keys);

            // A field of its own: closing the pipeline changes that and nothing else, so it merges with another editor's change.
            var change = Assert.Single(PipelineGraphFields.Changes(graph, Called("KEPT", PipelineCallers.Nobody)));
            Assert.Equal(("pipeline/called-by", PipelineCallers.Nobody), (change.Path, change.ProposedValue));
            Assert.Equal("which pipelines may call the pipeline", PipelineGraphFields.Describe(change.Path, graph));

            Assert.Equal("b,a", PipelineExchange.Read(PipelineExchange.Write("Kept", null, graph)).Graph!.CalledBy); // a file carries it
        }

        [Fact]
        public void N8n_s_caller_policy_comes_in_closed_where_it_was_closed()
        {
            static PipelineGraph Imported(string settings) => PipelineExchange.Read(
                "{\"name\":\"Sub\",\"nodes\":[{\"id\":\"1\",\"name\":\"When Executed by Another Workflow\",\"type\":\"n8n-nodes-base.executeWorkflowTrigger\",\"typeVersion\":1.1,\"position\":[0,0],\"parameters\":{}}],\"connections\":{},\"settings\":" + settings + "}").Graph!;

            Assert.Null(Imported("{\"callerPolicy\":\"workflowsFromSameOwner\"}").CalledBy);
            Assert.Null(Imported("{\"callerPolicy\":\"any\"}").CalledBy);
            Assert.Null(Imported("{}").CalledBy);
            Assert.Equal(PipelineCallers.Nobody, Imported("{\"callerPolicy\":\"none\"}").CalledBy);
            var listed = Imported("{\"callerPolicy\":\"workflowsFromAList\",\"callerIds\":\"14, 18\"}");
            Assert.Equal(PipelineCallers.Nobody, listed.CalledBy); // n8n's ids mean nothing here
            Assert.Contains(listed.Notes, note => note.Text.Contains("n8n let only the workflows it listed call it (14, 18)", StringComparison.Ordinal));
        }

        [Fact]
        public void The_callers_editor_is_warned_of_a_call_the_called_pipeline_would_refuse()
        {
            var caller = Caller("closed", "WARNED");
            var catalog = Array.Empty<StepCatalogEntry>();
            PipelineGraphProblem[] Warned(PipelineGraph called, string? self) => PipelineGraphProblems
                .Find(caller, catalog, new Dictionary<string, PipelineCallContract> { ["closed"] = PipelineCallContracts.Known(called)! }, self)
                .Where(problem => problem.Code == PipelineGraphProblems.BadCall).ToArray();

            var none = Assert.Single(Warned(Called("x", PipelineCallers.Nobody), "me"));
            Assert.Equal(PipelineGraphProblems.Warning, none.Severity);
            Assert.Equal(PipelineCallContracts.CalledInput, none.Input);
            Assert.StartsWith("The pipeline this calls may not be called by another pipeline", none.Message, StringComparison.Ordinal);
            Assert.StartsWith("The pipeline this calls may be called only by the pipelines its settings name",
                Assert.Single(Warned(Called("x", "someone-else"), "me")).Message, StringComparison.Ordinal);
            Assert.Empty(Warned(Called("x", "me"), "me"));
            Assert.Null(PipelineCallContracts.Known(Called("x", null))); // nothing to hold a call to
            Assert.Single(Warned(Called("x", "me"), null)); // not yet saved: none of the pipelines named
        }
    }
}
