#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's AI Agent with workflow tools, run by the real engine with the model scripted: a published pipeline starting
    /// with "AI agent tool" is a tool the model sees by its name and description; when the model calls it, the tool's pipeline RUNS
    /// with the model's argument (the agent's run its parent) and the result of its last step goes back to the model, which then
    /// answers. Bounded: tools named that are not published refused, at most so many tool runs (the model told to answer with what
    /// it has), an agent too deep refused; a tool that fails tells the model why.
    /// </summary>
    public sealed class AiAgentTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly ScriptedModel _model = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IAssistantModelAccess>(_model);
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<FindOrder>();
                module.AddStep<Broken>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        [Step("Stomme.Tests", "Tests", "Finds an order (agent tool).")]
        public sealed class FindOrder : CodeStep<string>
        {
            protected override void Execute(StepExecutionContext context) => context.Set(Result, $"order {context.GetLastResult()} is shipped");
        }

        [Step("Stomme.Tests", "Tests", "Always fails (agent tool).")]
        public sealed class Broken : CodeStep
        {
            protected override void Execute(StepExecutionContext context) => throw new InvalidOperationException("the warehouse is offline");
        }

        // A tool pipeline, published: the tool trigger, then the step that answers.
        private async Task<string> PublishToolAsync(string name, string description, IStep answer, string? calledBy = null)
        {
            using var scope = _services.CreateScope();
            var trigger = new AgentTool { Id = "tool", ToolName = new(name), Description = new(description) };
            trigger.SetCanStartPipeline(true);
            answer.Id = "answer";
            var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
            var flowchart = new Flowchart { Steps = { trigger, answer }, Connections = { new Connection(new Endpoint(trigger), new Endpoint(answer)) } };
            if (calledBy is not null)
            {
                flowchart.Metadata["designer"] = new Dictionary<string, object> { ["calledBy"] = calledBy }; // as the editor's codec keeps it
            }

            var definition = await publisher.NewAsync(flowchart);
            definition.Name = "Tool " + name;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private async Task<RunPipelineResult> RunAsync(IStep step, IDictionary<string, object>? input = null)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step, input is null ? null : new RunPipelineOptions { Input = input });
        }

        private async Task<IReadOnlyList<Stomme.Engine.Pipelines.Management.Entities.PipelineInstance>> RunsOfAsync(string definitionId)
        {
            using var scope = _services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineInstanceFilter { DefinitionId = definitionId })).ToList();
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static ChatResponse Calls(params (string Tool, string Request)[] calls) =>
            new(new ChatMessage(ChatRole.Assistant, calls.Select((call, index) => (AIContent)new FunctionCallContent("call-" + index, call.Tool, new Dictionary<string, object?> { ["request"] = call.Request })).ToList()));

        private static string ToolAnswer(IEnumerable<ChatMessage> messages, int index = 0) =>
            messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ElementAt(index).Result?.ToString() ?? "";

        [Fact]
        public async Task The_model_calls_a_tool_pipeline_and_answers_with_what_it_returned()
        {
            var tool = await PublishToolAsync("find_order", "Finds an order by its number and says where it is. Give it the order number.", new FindOrder());
            await PublishToolAsync("weather", "Says the weather in a city.", new FindOrder());
            _model.Script.Enqueue(_ => Calls(("find_order", "42")));
            _model.Script.Enqueue(messages => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Order 42: " + ToolAnswer(messages))));

            var run = await RunAsync(new AiAgent { Prompt = new("Where is order 42?"), Instructions = new("Answer in one line."), Tools = new("find_order") });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.Equal("Order 42: order 42 is shipped", OutputsOf<AiAgent>(run)["Result"]);
            Assert.Equal(1, OutputsOf<AiAgent>(run)["ToolCalls"]);
            Assert.Equal(new[] { AskAi.Agent }, _model.Opened);
            // The model saw the tool named - and only it - with its description; the system and user messages as for Ask AI.
            var first = _model.Calls[0];
            var offered = Assert.Single(first.Options!.Tools!);
            Assert.Equal(("find_order", "Finds an order by its number and says where it is. Give it the order number."), (offered.Name, offered.Description));
            Assert.Equal(new[] { ChatRole.System, ChatRole.User }, first.Messages.Select(message => message.Role));
            Assert.Equal(2, _model.Calls.Count);

            // The tool's pipeline ran once, the agent's run its parent, and the agent's log says so.
            var toolRun = Assert.Single(await RunsOfAsync(tool));
            Assert.Equal(run.PipelineState.Id, toolRun.ParentPipelineInstanceId);
            Assert.Contains(run.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "ToolCalled" && entry.Message == $"Called 'find_order' (run {toolRun.Id}): Finished.");
        }

        [Fact]
        public async Task With_no_tools_named_every_published_tool_is_offered_and_one_not_published_is_refused()
        {
            await PublishToolAsync("find_order", "Finds an order.", new FindOrder());
            await PublishToolAsync("weather", "Says the weather.", new FindOrder());
            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Nothing to do.")));

            var run = await RunAsync(new AiAgent { Prompt = new("Hello") });
            Assert.Equal("Nothing to do.", OutputsOf<AiAgent>(run)["Result"]);
            Assert.Equal(new[] { "find_order", "weather" }, _model.Calls[0].Options!.Tools!.Select(tool => tool.Name).OrderBy(name => name));
            Assert.Equal(0, OutputsOf<AiAgent>(run)["ToolCalls"]);

            var unknown = await RunAsync(new AiAgent { Prompt = new("Hello"), Tools = new("weather\nnope"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("AI_TOOL_UNKNOWN: No AI agent tool named 'nope' is published on the site.", OutputsOf<AiAgent>(unknown)["Error"]);
            Assert.Single(_model.Calls); // the refusal asked the model nothing
        }

        // A tool's run is a call from the agent's pipeline, so a tool whose pipeline does not let it call is never offered
        // - and, named, is refused saying why.
        [Fact]
        public async Task A_tool_whose_pipeline_may_not_be_called_from_here_is_left_out_or_refused()
        {
            var closed = await PublishToolAsync("find_order", "Finds an order.", new FindOrder(), Stomme.Models.Pipelines.PipelineCallers.Nobody);
            await PublishToolAsync("weather", "Says the weather.", new FindOrder());
            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Nothing to do.")));

            var run = await RunAsync(new AiAgent { Prompt = new("Hello") });
            Assert.Equal(new[] { "weather" }, _model.Calls[0].Options!.Tools!.Select(tool => tool.Name));
            Assert.Contains(run.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "ToolsLeftOut" && entry.Message == "Not offered - their pipelines may not be called by this one: 'find_order'.");

            var named = await RunAsync(new AiAgent { Prompt = new("Hello"), Tools = new("find_order"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("CALL_REFUSED: The AI agent tool 'find_order' may not be used here: 'Tool find_order' may not be called by another pipeline - its settings say so.", OutputsOf<AiAgent>(named)["Error"]);
            Assert.Single(_model.Calls); // the refusal asked the model nothing
            Assert.Empty(await RunsOfAsync(closed));
        }

        [Fact]
        public async Task Tool_runs_are_bounded_and_a_failing_tool_says_why()
        {
            var tool = await PublishToolAsync("find_order", "Finds an order.", new FindOrder());
            await PublishToolAsync("stock", "Says how many are in stock.", new Broken());
            _model.Script.Enqueue(_ => Calls(("find_order", "1"), ("find_order", "2"), ("stock", "cake")));
            _model.Script.Enqueue(messages => new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join(" | ", Enumerable.Range(0, 3).Select(index => ToolAnswer(messages, index))))));

            var run = await RunAsync(new AiAgent { Prompt = new("Check orders 1 and 2, and the cake stock."), MaxToolCalls = new(2) });

            Assert.Equal("order 1 is shipped | order 2 is shipped | No more tool calls are allowed for this answer - answer with what you have.", OutputsOf<AiAgent>(run)["Result"]);
            Assert.Equal(2, OutputsOf<AiAgent>(run)["ToolCalls"]);
            Assert.Equal(2, (await RunsOfAsync(tool)).Count);

            _model.Script.Enqueue(_ => Calls(("stock", "cake")));
            _model.Script.Enqueue(messages => new ChatResponse(new ChatMessage(ChatRole.Assistant, ToolAnswer(messages))));
            var failing = await RunAsync(new AiAgent { Prompt = new("How much cake is there?") });
            Assert.Equal("The tool 'stock' failed: the warehouse is offline", OutputsOf<AiAgent>(failing)["Result"]);
        }

        [Fact]
        public async Task An_agent_too_deep_is_refused_and_a_tool_without_a_description_is_never_offered()
        {
            var deep = await RunAsync(new AiAgent { Prompt = new("Hello"), OnFailure = new(FailurePolicy.Continue) }, new Dictionary<string, object> { [AgentTool.DepthInputKey] = "3" });
            Assert.Equal("AI_TOO_DEEP: This agent was reached through 3 tools in a row; an agent that deep may not run (at most 3).", OutputsOf<AiAgent>(deep)["Error"]);
            Assert.Empty(_model.Calls);

            using var scope = _services.CreateScope();
            var trigger = new AgentTool { Id = "tool", ToolName = new("silent"), Description = new(" ") };
            trigger.SetCanStartPipeline(true);
            var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Steps = { trigger } });
            // The engine's indexer logs and skips a trigger whose payload is refused, so a tool with no description is never offered
            // (the editor refuses to publish it first: a required input left empty).
            await publisher.PublishAsync(definition);
            var indexed = await scope.ServiceProvider.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = AgentTool.StepTypeName });
            Assert.DoesNotContain(indexed, trigger => AgentTool.Read(trigger)?.Tool == "silent");
            Assert.False(AgentTool.IsValidName("find order"));
            Assert.True(AgentTool.IsValidName("find-order_2"));
            Assert.False(AgentTool.IsValidName(new string('x', 65)));
        }

        // n8n's Simple Memory: with a memory key, the conversation is remembered - a later run with the key sees the earlier exchange
        // as the user's and the model's messages, between the instructions and the prompt; another key sees none; the newest so many.
        [Fact]
        public async Task With_a_memory_key_the_conversation_carries_on_in_a_later_run()
        {
            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello Ada.")));
            await RunAsync(new AiAgent { Id = "agent", Prompt = new("I am Ada."), MemoryKey = new("customer-7") });
            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "You are Ada.")));
            var second = await RunAsync(new AiAgent { Id = "agent", Prompt = new("Who am I?"), Instructions = new("Be brief."), MemoryKey = new("customer-7") });

            Assert.Equal(new[] { (ChatRole.System, "Be brief."), (ChatRole.User, "I am Ada."), (ChatRole.Assistant, "Hello Ada."), (ChatRole.User, "Who am I?") },
                _model.Calls[1].Messages.Select(message => (message.Role, message.Text)));
            Assert.Contains(second.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Remembered" && entry.Message == "2 earlier message(s) under 'customer-7'.");

            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "I do not know.")));
            await RunAsync(new AiAgent { Id = "agent", Prompt = new("Who am I?"), MemoryKey = new("customer-8") });
            Assert.Equal(new[] { ChatRole.User }, _model.Calls[2].Messages.Select(message => message.Role));

            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Noted.")));
            await RunAsync(new AiAgent { Id = "agent", Prompt = new("Third."), MemoryKey = new("customer-7"), MemoryMessages = new(2) });
            Assert.Equal(new[] { "Who am I?", "You are Ada.", "Third." }, _model.Calls[3].Messages.Select(message => message.Text));

            // No key, no memory.
            _model.Script.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi.")));
            await RunAsync(new AiAgent { Id = "agent", Prompt = new("Hello") });
            Assert.Equal(new[] { "Hello" }, _model.Calls[4].Messages.Select(message => message.Text));
        }

        // A stored run gives a step's result back as a JSON element: a string one goes to the model as its text, not JSON quoting
        // it (found driving on a scratch host: the answer arrived quoted twice).
        [Fact]
        public void A_tools_answer_goes_back_as_text_whatever_form_the_run_kept_it_in()
        {
            Assert.Equal("Order 42 shipped.", AiAgent.ToolAnswer(System.Text.Json.JsonDocument.Parse("\"Order 42 shipped.\"").RootElement));
            Assert.Equal("{\"id\":42}", AiAgent.ToolAnswer(System.Text.Json.JsonDocument.Parse("{\"id\":42}").RootElement));
            Assert.Equal("(the tool gave no result)", AiAgent.ToolAnswer(System.Text.Json.JsonDocument.Parse("null").RootElement));
            Assert.Equal("plain", AiAgent.ToolAnswer("plain"));
            Assert.Equal("(the tool gave no result)", AiAgent.ToolAnswer(null));
            Assert.Equal("[1,2]", AiAgent.ToolAnswer(new[] { 1, 2 }));
        }

        private sealed class ScriptedModel : IAssistantModelAccess
        {
            public Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> Script { get; } = new();

            public List<string> Opened { get; } = new();

            public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public Task<AssistantModel> OpenAsync(string agent, CancellationToken cancellationToken = default)
            {
                Opened.Add(agent);
                return Task.FromResult(new AssistantModel(new Client(this), "openai", "test-model", 0.2f));
            }

            private sealed class Client(ScriptedModel model) : IChatClient
            {
                public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    var seen = messages.ToList();
                    model.Calls.Add((seen, options));
                    return Task.FromResult(model.Script.Dequeue()(seen));
                }

                public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException();

                public object? GetService(Type serviceType, object? serviceKey = null) => null;

                public void Dispose()
                {
                }
            }
        }
    }
}
