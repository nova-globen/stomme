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
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A pipeline calling another (n8n's sub-workflows), through the engine as the host composes it: the caller sends its
    /// variables, the called pipeline takes them from its run's input and gives back what it sets as its output, and the caller
    /// reads that back - whether the called one finished at once or after a pause. Found building the editor's sub-pipelines: a
    /// caller whose called pipeline had paused read nothing back, and a variable of any kind read after a pause read wrong.
    /// </summary>
    public sealed class PipelineSubPipelineTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

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
                Stomme.Modules.Pipelines.Reliability.StepReliability.UseStepReliability(module); // a step's Failed path, as in the host
            });
            PipelineVariableStorage.Wrap(services); // as the host composes the engine
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IPipelineDispatcher, InlineDispatcher>();
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        /// <summary>Runs a dispatched definition or run at once, as the engine's worker would later.</summary>
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

        private static async Task<string> PublishAsync(IServiceProvider services, string name, PipelineGraph graph)
        {
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            definition.Name = name;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> StartAsync(IServiceProvider services, string definitionId)
        {
            var started = await new StartPipelineCommandHandler(
                services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(),
                new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), services.GetRequiredService<ISystemClock>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services))
                .Handle(new StartPipelineCommand(definitionId, null, null), CancellationToken.None);
            return await RunOfAsync(services, started!.RunId);
        }

        private static async Task<PipelineInstance> RunOfAsync(IServiceProvider services, string id) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = id }))!;

        // The called pipeline's due wait passes, it finishes, and the run waiting for it is resumed - as the host's task and the
        // engine's queue worker do.
        private async Task PassTheWaitAsync(IServiceProvider services)
        {
            _clock.UtcNow += TimeSpan.FromHours(3);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
            await services.GetRequiredService<IBookmarkQueueProcessor>().ProcessAsync(CancellationToken.None);
        }

        private string[] Said(string start) => _console.ToString().Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(start, StringComparison.Ordinal)).ToArray();

        // Takes 'who' from its run's input (a record), waits two hours when it pauses, and gives back a greeting as its output.
        private static PipelineGraph Called(bool pauses)
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "c-who", Name = "who", TypeName = "Object" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "called", Type = PipelineCallContracts.WhenCalledType },
                    new PipelineGraphStep { Id = "take", Type = "Stomme.SetVariable", InputVariables = { ["Variable"] = "c-who" }, InputExpressions = { ["Value"] = "{{ $input.who }}" } },
                    new PipelineGraphStep { Id = "give", Type = "Stomme.SetOutput", Inputs = { ["OutputName"] = "greeting" }, InputExpressions = { ["OutputValue"] = "Hello {{ who.name }}" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "called", TargetId = "take" } },
            };
            if (pauses)
            {
                graph.Steps.Add(new PipelineGraphStep { Id = "wait", Type = "Stomme.Wait", Inputs = { ["For"] = "2 hours" } });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "take", TargetId = "wait" });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "wait", TargetId = "give" });
            }
            else
            {
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "take", TargetId = "give" });
            }

            return graph;
        }

        // Sends its 'who' to the called pipeline, waits for it, and says what came back - and what its own run's input holds.
        private static PipelineGraph Caller(string called, string said, string callType = "Stomme.ExecutePipeline", string readBack = "{{ sub.output.greeting }} ({{ sub.subStatus }})") => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "p-who", Name = "who", TypeName = "Object", Value = "{\"name\":\"Ada\"}" },
                new PipelineGraphVariable { Id = "p-sub", Name = "sub", TypeName = "Object" },
            },
            Steps =
            {
                new PipelineGraphStep
                {
                    Id = "call", Type = callType, Inputs = { ["PipelineDefinitionId"] = called, ["WaitForCompletion"] = "true" },
                    InputExpressions = { ["Input"] = "{\"who\": {{ who | json }}}" }, OutputVariables = { ["Result"] = "p-sub" },
                },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = said + " " + readBack + " input {{ $input | json }}" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "call", TargetId = "say" } },
        };

        [Fact]
        public async Task A_called_pipeline_gives_back_what_it_sets_when_it_finishes_at_once_and_after_a_pause()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var quick = await StartAsync(services, await PublishAsync(services, "Quick caller", Caller(await PublishAsync(services, "Quick", Called(pauses: false)), "QUICK")));
            var slow = await StartAsync(services, await PublishAsync(services, "Slow caller", Caller(await PublishAsync(services, "Slow", Called(pauses: true)), "SLOW")));
            Assert.Equal(PipelineStatus.Running, slow.Status); // waiting for the called pipeline's wait

            await PassTheWaitAsync(services);

            Assert.Equal(PipelineSubStatus.Finished, quick.SubStatus);
            Assert.Equal(PipelineSubStatus.Finished, (await RunOfAsync(services, slow.Id)).SubStatus);
            Assert.Equal(new[] { "QUICK Hello Ada (Finished) input {}" }, Said("QUICK"));
            // The same result after the pause - until this, empty - and the called pipeline's output not in the caller's $input.
            Assert.Equal(new[] { "SLOW Hello Ada (Finished) input {}" }, Said("SLOW"));
        }

        // Dispatch pipeline, waiting: its result is the called pipeline's output - not that mixed into the caller's own input.
        [Fact]
        public async Task A_dispatched_pipeline_waited_for_gives_back_its_output_alone()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var called = await PublishAsync(services, "Dispatched", Called(pauses: true));
            var caller = await StartAsync(services, await PublishAsync(services, "Dispatcher", Caller(called, "DISPATCHED", "Stomme.DispatchPipeline", "{{ sub | json }}")));
            Assert.Equal(PipelineStatus.Running, caller.Status);

            await PassTheWaitAsync(services);

            Assert.Equal(PipelineSubStatus.Finished, (await RunOfAsync(services, caller.Id)).SubStatus);
            Assert.Equal(new[] { "DISPATCHED {\"greeting\":\"Hello Ada\"} input {}" }, Said("DISPATCHED"));
        }

        // A called pipeline that fails fails the call - at once and after a pause - saying why, as n8n's Execute Workflow fails with
        // its sub-workflow (upstream went on, with nothing to go on with); ticked to go on, the result says how it finished.
        [Fact]
        public async Task A_called_pipeline_that_fails_fails_the_call_unless_it_goes_on()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            PipelineGraph Failing(bool pauses)
            {
                var graph = Called(pauses);
                var give = graph.Steps.Single(step => step.Id == "give");
                give.Type = "Stomme.DateAndTime";
                give.Inputs = new() { ["Operation"] = "Add", ["Date"] = "not a date", ["Duration"] = "1 day" };
                give.InputExpressions.Clear();
                return graph;
            }

            var atOnce = await StartAsync(services, await PublishAsync(services, "Fails at once", Caller(await PublishAsync(services, "Failing", Failing(pauses: false)), "AT ONCE")));
            var later = await StartAsync(services, await PublishAsync(services, "Fails later", Caller(await PublishAsync(services, "Failing slowly", Failing(pauses: true)), "LATER")));
            var goingOn = Caller(await PublishAsync(services, "Failing too", Failing(pauses: false)), "GOES ON", readBack: "{{ sub.subStatus }}");
            goingOn.Steps.Single(step => step.Id == "call").Inputs["GoOnWhenItFails"] = "true";
            var goesOn = await StartAsync(services, await PublishAsync(services, "Goes on", goingOn));

            await PassTheWaitAsync(services);

            foreach (var failed in new[] { await RunOfAsync(services, atOnce.Id), await RunOfAsync(services, later.Id) })
            {
                Assert.Equal(PipelineSubStatus.Faulted, failed.SubStatus);
                Assert.StartsWith("The called pipeline failed: ", Assert.Single(failed.PipelineState.Incidents).Message);
            }

            Assert.Empty(Said("AT ONCE"));
            Assert.Empty(Said("LATER"));
            Assert.Equal(PipelineSubStatus.Finished, (await RunOfAsync(services, goesOn.Id)).SubStatus);
            Assert.Equal(new[] { "GOES ON Faulted input {}" }, Said("GOES ON"));
        }

        // A call whose pipeline fails takes the call's Failed path, or goes on when it says so - at once and after a pause. After
        // the pause the failure comes from the call's resume (the engine's callback, the run's execute delegate then), and the
        // step the run goes on to must run its own work, not that callback again (it did: it failed with the call's failure).
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public async Task A_called_pipeline_that_fails_takes_the_calls_failed_path_or_goes_on(bool pauses, bool failedPath)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var called = Called(pauses);
            var give = called.Steps.Single(step => step.Id == "give");
            give.Type = "Stomme.Fault";
            give.Inputs = new() { ["Message"] = "Out of stock" };
            give.InputExpressions.Clear();
            var said = (failedPath ? "HANDLED" : "WENT ON") + (pauses ? " LATER" : " AT ONCE");
            var caller = Caller(await PublishAsync(services, "Fails " + said, called), said, readBack: "after");
            var call = caller.Steps.Single(step => step.Id == "call");
            if (failedPath)
            {
                call.FailedPath = true;
                caller.Steps.Add(new PipelineGraphStep { Id = "handled", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = said + " path" } });
                caller.Connections.Add(new PipelineGraphConnection { SourceId = "call", SourcePort = PipelineOutcomes.Failed, TargetId = "handled" });
            }
            else
            {
                call.ContinueOnError = true;
            }

            var run = await StartAsync(services, await PublishAsync(services, "Handles " + said, caller));
            await PassTheWaitAsync(services);

            var ended = await RunOfAsync(services, run.Id);
            Assert.True(ended.SubStatus == PipelineSubStatus.Finished, string.Join("; ", ended.PipelineState.Incidents.Select(incident => incident.Message)));
            Assert.Equal(new[] { failedPath ? said + " path" : said + " after input {}" }, Said(said));
        }

        private static string NewId() => Guid.NewGuid().ToString("N");

        // An order said loud and reported: 'who' and 'count' are the pipeline's - 'count' changed before the steps that move, so
        // only what is sent reaches them - 'shout' is set and read among them, 'report' is set among them and read after them;
        // and, pausing, the moved steps wait two hours between.
        private static PipelineGraph Orders(string hook, bool pauses)
        {
            var graph = new PipelineGraph
            {
                Variables =
                {
                    new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "ada" },
                    new PipelineGraphVariable { Id = "v-count", Name = "count", TypeName = "Int32", Value = "2" },
                    new PipelineGraphVariable { Id = "v-shout", Name = "shout", TypeName = "Object" },
                    new PipelineGraphVariable { Id = "v-report", Name = "report", TypeName = "Object" },
                },
                Steps =
                {
                    new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "When an order comes", Inputs = { ["WebhookName"] = hook } },
                    new PipelineGraphStep { Id = "more", Type = PipelineSubPipelines.SetVariableType, Name = "One more", Position = new GraphPosition { X = 40, Y = 100 }, InputVariables = { ["Variable"] = "v-count" }, InputExpressions = { ["Value"] = "{{ count | plus: 1 }}" } },
                    new PipelineGraphStep { Id = "shout", Type = PipelineSubPipelines.SetVariableType, Name = "Shout", Position = new GraphPosition { X = 300, Y = 100 }, InputVariables = { ["Variable"] = "v-shout" }, InputExpressions = { ["Value"] = "{{ who | upper }}" } },
                    new PipelineGraphStep { Id = "report", Type = PipelineSubPipelines.SetVariableType, Name = "Report", Position = new GraphPosition { X = 560, Y = 100 }, InputVariables = { ["Variable"] = "v-report" }, InputExpressions = { ["Value"] = "{{ shout }} x{{ count }}" } },
                    new PipelineGraphStep { Id = "said", Type = "Stomme.WriteLine", Name = "Say it", Position = new GraphPosition { X = 820, Y = 100 }, InputExpressions = { ["Text"] = "MOVED {{ shout }}" } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Name = "After", Position = new GraphPosition { X = 1080, Y = 100 }, InputExpressions = { ["Text"] = "AFTER {{ report }}" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "hook", TargetId = "more" },
                    new PipelineGraphConnection { SourceId = "more", TargetId = "shout" },
                    new PipelineGraphConnection { SourceId = "shout", TargetId = "report" },
                    new PipelineGraphConnection { SourceId = "said", TargetId = "after" },
                },
            };
            if (pauses)
            {
                graph.Steps.Add(new PipelineGraphStep { Id = "wait", Type = "Stomme.Wait", Name = "Wait", Inputs = { ["For"] = "2 hours" } });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "report", TargetId = "wait" });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "wait", TargetId = "said" });
            }
            else
            {
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "report", TargetId = "said" });
            }

            return graph;
        }

        // n8n's "Convert to sub-workflow": the steps moved into a pipeline of their own, called where they stood - and the pipeline
        // says what it said before, at once and across a pause among the moved steps.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Steps_moved_into_a_sub_pipeline_do_what_they_did(bool pauses)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var catalog = ListStepsQueryHandler.Catalog(services.GetRequiredService<IStepRegistry>());
            var moving = pauses ? new[] { "shout", "report", "wait", "said" } : new[] { "shout", "report", "said" };

            await PublishAsync(services, "Orders before", Orders("before", pauses));
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("before"));
            await PassTheWaitAsync(services);
            var before = (Said("MOVED"), Said("AFTER"));
            _console.GetStringBuilder().Clear();

            var original = Orders("after", pauses);
            var plan = PipelineSubPipelines.Make(original, moving, catalog, NewId);
            Assert.Null(plan.Refusal);
            Assert.Equal(moving.OrderBy(id => id, StringComparer.Ordinal), plan.Moved.OrderBy(id => id, StringComparer.Ordinal));
            // 'shout' is read among them too, but always empty when the call comes - set only among them, not given back.
            Assert.Equal(new[] { "who", "count" }, plan.Sent.Select(variable => variable.Name));
            Assert.Equal(new[] { "report" }, plan.GivenBack.Select(variable => variable.Name));
            var called = await PublishAsync(services, "Shout and report", plan.Called!);
            var calling = PipelineSubPipelines.Call(original, plan, called, "Shout and report", NewId);
            await PublishAsync(services, "Orders after", calling);
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("after"));
            await PassTheWaitAsync(services);

            Assert.Equal(new[] { "MOVED ADA" }, before.Item1);
            Assert.Equal(new[] { "AFTER ADA x3" }, before.Item2);
            Assert.Equal(before.Item1, Said("MOVED"));
            Assert.Equal(before.Item2, Said("AFTER"));
            // In the pipeline, the call stands where the steps did and what they set comes back after it; nothing moved is left.
            var call = calling.Steps.Single(step => step.Type == PipelineSubPipelines.CallType);
            Assert.Equal((300d, 100d, called, "true"), (call.Position.X, call.Position.Y, call.Inputs["PipelineDefinitionId"], call.Inputs["WaitForCompletion"]));
            Assert.Equal("{\"who\": {{ who | json }}, \"count\": {{ count | json }}}", call.InputExpressions["Input"]);
            var back = calling.Steps.Single(step => step.Name == "report from 'Shout and report'");
            Assert.Equal(("v-report", "{{ subPipeline.output.report }}"), (back.InputVariables["Variable"], back.InputExpressions["Value"]));
            Assert.Equal(calling.Variables.Single(variable => variable.Name == "subPipeline").Id, call.OutputVariables["Result"]);
            Assert.DoesNotContain(calling.Steps, step => moving.Contains(step.Id));
            Assert.Equal(new[] { "hook>more", "more>" + call.Id, call.Id + ">" + back.Id, back.Id + ">after" }.OrderBy(text => text, StringComparer.Ordinal),
                calling.Connections.Select(connection => connection.SourceId + ">" + connection.TargetId).OrderBy(text => text, StringComparer.Ordinal));
        }

        private static PipelineGraphStep Step(string id, string type, string name, string? parent = null) => new() { Id = id, Type = type, Name = name, Parent = parent, ParentPort = parent is null ? null : "Body" };

        private static PipelineGraphConnection Link(string from, string to, string? port = null) => new() { SourceId = from, SourcePort = port, TargetId = to };

        // Steps that cannot move so - the moved ones would no longer do what they did - are refused, saying why.
        [Fact]
        public void Steps_that_cannot_move_so_are_refused_saying_why()
        {
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            string? Refusal(PipelineGraph graph, params string[] chosen) => PipelineSubPipelines.Make(graph, chosen, catalog, NewId).Refusal;
            var orders = Orders("orders", pauses: false);

            Assert.Equal("Choose the steps to move first.", Refusal(orders));
            Assert.Equal("'When an order comes' starts the pipeline - a sub-pipeline is started by its caller.", Refusal(orders, "hook", "shout"));
            Assert.Equal("They are reached from outside at 'Shout' and 'After' - the steps must have one way in.", Refusal(orders, "shout", "after"));

            // A decision among them: both ways going on outside, or one ending among them while the other goes on.
            PipelineGraph Decision(bool falseGoesOn) => new()
            {
                Steps =
                {
                    Step("hook", "Stomme.Webhook", "Hook"), Step("big", "Stomme.FlowDecision", "Big?"), Step("yes", "Stomme.WriteLine", "Yes"),
                    Step("no", "Stomme.WriteLine", "No"), Step("then", "Stomme.WriteLine", "Then"), Step("else", "Stomme.WriteLine", "Else"),
                },
                Connections = new List<PipelineGraphConnection> { Link("hook", "big"), Link("big", "yes", "True"), Link("yes", "then"), Link("no", "else") }
                    .Concat(falseGoesOn ? new[] { Link("big", "no", "False") } : Array.Empty<PipelineGraphConnection>()).ToList(),
            };
            Assert.Equal("They go on from 'Yes' and 'No' - the steps must have one way out.", Refusal(Decision(falseGoesOn: true), "big", "yes", "no"));
            Assert.Equal("The run can end at 'Big?' (False) among them while the rest goes on after them - moved, the rest would go on either way. Move the steps after it as well, or fewer.",
                Refusal(Decision(falseGoesOn: false), "big", "yes"));

            var forked = new PipelineGraph { Steps = { Step("hook", "Stomme.Webhook", "Hook"), Step("a", "Stomme.WriteLine", "A"), Step("b", "Stomme.WriteLine", "B"), Step("c", "Stomme.WriteLine", "C") }, Connections = { Link("hook", "a"), Link("a", "b"), Link("a", "c") } };
            Assert.Equal("'A' goes on both among them and after them - the steps must have one way out.", Refusal(forked, "a", "b"));

            var reading = new PipelineGraph { Steps = { Step("hook", "Stomme.Webhook", "Hook"), Step("read", "Stomme.WriteLine", "Read it") }, Connections = { Link("hook", "read") } };
            reading.Steps[1].InputExpressions["Text"] = "Order {{ $input.order }}";
            Assert.Equal("'Read it' reads {{ $input }}, what this run was started with - moved, it would read what the call sends. Save what it needs to a variable first.", Refusal(reading, "read"));

            var looped = new PipelineGraph { Steps = { Step("each", "Stomme.ForEach", "Each"), Step("inner", "Stomme.WriteLine", "Inner", parent: "each"), Step("after", "Stomme.WriteLine", "After") }, Connections = { Link("each", "after") } };
            Assert.Equal("The steps stand in different flowcharts - choose steps of one: the pipeline's own, or one body's.", Refusal(looped, "inner", "after"));
            Assert.Null(Refusal(looped, "each")); // a step with a body moves with what is inside it
            Assert.Equal(new[] { "each", "inner" }, PipelineSubPipelines.Make(looped, new[] { "each" }, catalog, NewId).Moved);

            var answering = new PipelineGraph { Steps = { Step("hook", "Stomme.Webhook", "Hook"), Step("answer", PipelineWebhookAnswering.RespondStepType, "Answer") }, Connections = { Link("hook", "answer") } };
            Assert.Equal("'Answer' answers this pipeline's webhook call, so it stays with the Webhook.", Refusal(answering, "answer"));

            // What they set, read before them, given back from one place - but the run can end at two.
            var twoEnds = Decision(falseGoesOn: true);
            twoEnds.Variables.Add(new PipelineGraphVariable { Id = "v-r", Name = "report", TypeName = "Object" });
            twoEnds.Connections.RemoveAll(connection => connection.SourceId is "yes" or "no");
            foreach (var setter in twoEnds.Steps.Where(step => step.Id is "yes" or "no"))
            {
                setter.Type = PipelineSubPipelines.SetVariableType;
                setter.InputVariables["Variable"] = "v-r";
                setter.Inputs["Value"] = setter.Name;
            }

            twoEnds.Steps.Single(step => step.Id == "then").InputExpressions["Text"] = "{{ report }}";
            twoEnds.Connections.Add(Link("then", "big"));
            twoEnds.Connections.RemoveAll(connection => connection.SourceId == "hook");
            twoEnds.Connections.Add(Link("hook", "then"));
            Assert.Equal("The run can end at several of them, and what they set ('report') is given back from one place - move fewer steps, or bring the ends together.", Refusal(twoEnds, "big", "yes", "no"));
        }

        // A variable of any kind reads after a pause as it did before it - a text, a record kept as text, and a text output.
        [Fact]
        public async Task A_variable_of_any_kind_reads_the_same_after_a_pause()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = new PipelineGraph
            {
                Variables =
                {
                    new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "Object" },
                    new PipelineGraphVariable { Id = "v-record", Name = "record", TypeName = "Object" },
                    new PipelineGraphVariable { Id = "v-began", Name = "began", TypeName = "String" },
                },
                Steps =
                {
                    new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Inputs = { ["WebhookName"] = "kept" } },
                    new PipelineGraphStep { Id = "who", Type = "Stomme.SetVariable", InputVariables = { ["Variable"] = "v-who" }, Inputs = { ["Value"] = "Ada" } },
                    new PipelineGraphStep { Id = "record", Type = "Stomme.SetVariable", InputVariables = { ["Variable"] = "v-record" }, InputExpressions = { ["Value"] = "{\"name\": \"{{ who }}\", \"town\": \"London\"}" } },
                    new PipelineGraphStep { Id = "began", Type = "Stomme.DateAndTime", Inputs = { ["Operation"] = "Format", ["Date"] = "2026-10-01", ["Format"] = "yyyy-MM-dd" }, OutputVariables = { ["Result"] = "v-began" } },
                    new PipelineGraphStep { Id = "before", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "BEFORE {{ who }} of {{ record.town }}, {{ began }}" } },
                    new PipelineGraphStep { Id = "wait", Type = "Stomme.Wait", Inputs = { ["For"] = "2 hours" } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "AFTER {{ who }} of {{ record.town }}, {{ began }}" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "hook", TargetId = "who" },
                    new PipelineGraphConnection { SourceId = "who", TargetId = "record" },
                    new PipelineGraphConnection { SourceId = "record", TargetId = "began" },
                    new PipelineGraphConnection { SourceId = "began", SourcePort = DataStep.DoneOutcome, TargetId = "before" },
                    new PipelineGraphConnection { SourceId = "before", TargetId = "wait" },
                    new PipelineGraphConnection { SourceId = "wait", TargetId = "after" },
                },
            };
            await PublishAsync(services, "Kept", graph);

            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("kept"));
            await PassTheWaitAsync(services);

            Assert.Equal(new[] { "BEFORE Ada of London, 2026-10-01" }, Said("BEFORE"));
            Assert.Equal(new[] { "AFTER Ada of London, 2026-10-01" }, Said("AFTER")); // was "\"Ada\" of , 2026-10-01"
        }
    }
}
