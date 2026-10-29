#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Ask AI, n8n's AI node on Stomme's model seam, run by the real engine with the seam scripted: the instructions
    /// and the prompt reach the site's model as the system and user messages, opened as the "pipelines" agent (so its calls are
    /// metered and quota-gated like the assistant's); the answer is the result; an empty prompt asks about the previous result;
    /// JSON is checked (a fenced block unwrapped); a refusal of the seam is <c>AI_&lt;REASON&gt;</c> in its words, a model that fails
    /// <c>AI_FAILED</c>. The seam's own half - switches, key, quota, metering - is the AiAssistant module's suites.
    /// </summary>
    public sealed class AskAiTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly ScriptedModel _model = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IAssistantModelAccess>(_model);
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

        [Step("Stomme.Tests", "Tests", "Captures the last result (ask AI).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        [Step("Stomme.Tests", "Tests", "Emits a fixed result (ask AI).")]
        public sealed class Emit : CodeStep<string>
        {
            protected override void Execute(StepExecutionContext context) => context.Set(Result, "Ada wrote the first program.");
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        [Fact]
        public async Task The_instructions_and_the_prompt_reach_the_sites_model_and_the_answer_is_the_result()
        {
            _model.Answer = "Positive.";

            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new AskAi { Prompt = new("Is this review positive? Great service."), Instructions = new("Answer in one word.") },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.Equal(AskAi.Agent, Assert.Single(_model.Opened));
            var call = Assert.Single(_model.Calls);
            Assert.Equal(new[] { (ChatRole.System, "Answer in one word."), (ChatRole.User, "Is this review positive? Great service.") }, call.Messages.Select(message => (message.Role, message.Text)));
            Assert.Null(call.Options?.ResponseFormat);
            Assert.Equal(0.2f, call.Options?.Temperature); // the site's temperature
            Assert.Equal("Positive.", _services.GetRequiredService<ResultSink>().Value);
        }

        [Fact]
        public async Task An_empty_prompt_asks_about_the_previous_result_and_json_is_checked()
        {
            _model.Answer = "```json\n{\"person\":\"Ada\"}\n```";

            var run = await RunAsync(new Sequence { Steps = { new Emit(), new AskAi { AnswerAs = new(AiAnswerFormat.Json) }, new CaptureLastResult() } });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_model.Calls);
            Assert.Equal("Ada wrote the first program.", Assert.Single(call.Messages).Text);
            Assert.Equal(ChatResponseFormat.Json, call.Options?.ResponseFormat);
            Assert.Equal("{\"person\":\"Ada\"}", _services.GetRequiredService<ResultSink>().Value); // the fence unwrapped

            _model.Answer = "Ada, I think.";
            var notJson = await RunAsync(new AskAi { Prompt = new("Who?"), AnswerAs = new(AiAnswerFormat.Json), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("AI_NOT_JSON: The model's answer is not JSON", (string)OutputsOf<AskAi>(notJson)["Error"]!);
        }

        [Fact]
        public async Task A_refusal_of_the_seam_and_a_failing_model_are_named_and_fault_the_run_unless_it_goes_on()
        {
            _model.Refusal = new AssistantUnavailableException(AssistantUnavailableReason.QuotaExceeded, "The site's AI quota (ai.tokens) is spent: 10 of 10.");
            var spent = await RunAsync(new AskAi { Prompt = new("Hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("AI_QUOTA_EXCEEDED: The site's AI quota (ai.tokens) is spent: 10 of 10.", OutputsOf<AskAi>(spent)["Error"]);
            Assert.Empty(_model.Calls);

            var faulted = await RunAsync(new AskAi { Prompt = new("Hi") });
            Assert.Equal(PipelineSubStatus.Faulted, faulted.PipelineState.SubStatus);

            _model.Refusal = null;
            _model.Failure = new InvalidOperationException("The model answered 500.");
            var failed = await RunAsync(new AskAi { Prompt = new("Hi"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("AI_FAILED: The model answered 500.", OutputsOf<AskAi>(failed)["Error"]);
        }

        [Fact]
        public void A_fenced_answer_is_unwrapped_and_anything_else_left_as_it_is()
        {
            Assert.Equal("[1, 2]", AskAi.Unfenced("```\n[1, 2]\n```"));
            Assert.Equal("{\"a\":1}", AskAi.Unfenced("  ```json\n{\"a\":1}\n```  "));
            Assert.Equal("{\"a\":1}", AskAi.Unfenced("{\"a\":1}"));
            Assert.Equal("```", AskAi.Unfenced("```"));
        }

        // n8n's Structured Output Parser: the shape told the model in the system message, after the instructions; an answer
        // that fits is the result at once.
        [Fact]
        public async Task An_answer_shape_is_told_the_model_and_an_answer_that_fits_is_the_result()
        {
            _model.Answer = "```json\n{\"customer\": \"Ada\", \"total\": 120.5, \"paid\": false, \"items\": [{\"sku\": \"A-1\", \"qty\": 2}]}\n```";
            var run = await RunAsync(new Sequence
            {
                Steps =
                {
                    new AskAi { Prompt = new("Read this invoice: ..."), Instructions = new("You read invoices."), Shape = new("{\"customer\": \"\", \"total\": 0, \"paid\": true, \"items\": [{\"sku\": \"\", \"qty\": 1}]}") },
                    new CaptureLastResult(),
                },
            });

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var call = Assert.Single(_model.Calls);
            Assert.Equal(ChatResponseFormat.Json, call.Options?.ResponseFormat);
            var system = call.Messages[0].Text;
            Assert.StartsWith("You read invoices.\n\nAnswer with JSON only, of exactly this shape", system);
            Assert.Contains("\"qty\": 1", system);
            Assert.Equal("{\"customer\": \"Ada\", \"total\": 120.5, \"paid\": false, \"items\": [{\"sku\": \"A-1\", \"qty\": 2}]}", _services.GetRequiredService<ResultSink>().Value);
        }

        // n8n's Auto-fixing Output Parser: an answer that misses the shape is put to the model once more - its answer and what was
        // wrong, by path - and the second answer is the result; a second miss fails, naming where.
        [Fact]
        public async Task An_answer_that_misses_the_shape_is_asked_once_more_and_a_second_miss_fails()
        {
            const string shape = "{\"customer\": \"\", \"total\": 0, \"items\": [{\"sku\": \"\"}]}";
            _model.Answers.Enqueue("{\"customer\": \"Ada\", \"total\": \"120\", \"items\": [{\"sku\": \"A-1\"}, {}]}");
            _model.Answers.Enqueue("{\"customer\": \"Ada\", \"total\": 120, \"items\": [{\"sku\": \"A-1\"}]}");
            var fixedRun = await RunAsync(new Sequence { Steps = { new AskAi { Prompt = new("Read it."), Shape = new(shape) }, new CaptureLastResult() } });
            Assert.Equal(PipelineSubStatus.Finished, fixedRun.PipelineState.SubStatus);
            Assert.Equal(2, _model.Calls.Count);
            var again = _model.Calls[1].Messages;
            Assert.Equal((ChatRole.Assistant, ChatRole.User), (again[^2].Role, again[^1].Role));
            Assert.Equal("That answer does not fit the shape:\n- $.total: a number, not text.\n- $.items[1].sku: missing.\nAnswer again with JSON of the shape alone.", again[^1].Text);
            Assert.Equal("{\"customer\": \"Ada\", \"total\": 120, \"items\": [{\"sku\": \"A-1\"}]}", _services.GetRequiredService<ResultSink>().Value);

            _model.Calls.Clear();
            _model.Answers.Enqueue("not json at all");
            _model.Answer = "{\"customer\": 7}";
            var failed = await RunAsync(new AskAi { Prompt = new("Read it."), Shape = new(shape), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal(2, _model.Calls.Count);
            Assert.StartsWith("- it is not JSON: ", _model.Calls[1].Messages[^1].Text.Split('\n')[1]);
            Assert.Equal("AI_WRONG_SHAPE: The model's answer does not fit the shape, asked twice: $.customer: text, not a whole number. $.total: missing. $.items: missing.",
                OutputsOf<AskAi>(failed)["Error"]);

            var badShape = await RunAsync(new AskAi { Prompt = new("Read it."), Shape = new("{not json") });
            Assert.Equal(PipelineSubStatus.Faulted, badShape.PipelineState.SubStatus);
            Assert.StartsWith("The answer shape is not JSON", badShape.PipelineState.Incidents.Single().Message);
        }

        // A JSON Schema as n8n's parser takes one: type (one or several), required, properties, items, enum, an integer a whole
        // number; an example that happens to have a field named "type" is still an example.
        [Fact]
        public void A_json_schema_is_held_and_an_example_with_a_type_field_stays_an_example()
        {
            var schema = AiAnswerShape.Read("""
                { "type": "object", "required": ["kind", "count"],
                  "properties": { "kind": { "enum": ["invoice", "receipt"] }, "count": { "type": "integer" }, "note": { "type": ["string", "null"] },
                                  "lines": { "type": "array", "items": { "type": "object", "required": ["amount"], "properties": { "amount": { "type": "number" } } } } } }
                """, out _)!;
            Assert.True(AiAnswerShape.IsSchema(schema));
            Assert.Empty(AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\": \"invoice\", \"count\": 2, \"note\": null, \"lines\": [{\"amount\": 9.5}]}"), schema));
            Assert.Equal(new[] { "$.count: missing.", "$.kind: one of \"invoice\", \"receipt\", not \"order\".", "$.lines[0].amount: a number, not text." },
                AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\": \"order\", \"lines\": [{\"amount\": \"9\"}]}"), schema));
            Assert.Equal(new[] { "$.count: a whole number, not a number." }, AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\": \"receipt\", \"count\": 2.5}"), schema));

            var example = AiAnswerShape.Read("{\"type\": \"invoice\", \"number\": 0}", out _)!;
            Assert.False(AiAnswerShape.IsSchema(example));
            Assert.Equal(new[] { "$.number: a number, not text." }, AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse("{\"type\": \"receipt\", \"number\": \"7\"}"), example));
            Assert.Equal(new[] { "$: an object, not a list." }, AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse("[]"), example));
        }

        private sealed class ScriptedModel : IAssistantModelAccess
        {
            public string Answer { get; set; } = "";

            /// <summary>Answers given one per call before <see cref="Answer"/> - a model asked again answering otherwise.</summary>
            public Queue<string> Answers { get; } = new();

            public AssistantUnavailableException? Refusal { get; set; }

            public Exception? Failure { get; set; }

            public List<string> Opened { get; } = new();

            public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Refusal is null);

            public Task<AssistantModel> OpenAsync(string agent, CancellationToken cancellationToken = default)
            {
                if (Refusal is not null)
                {
                    throw Refusal;
                }

                Opened.Add(agent);
                return Task.FromResult(new AssistantModel(new Client(this), "openai", "test-model", 0.2f));
            }

            private sealed class Client(ScriptedModel model) : IChatClient
            {
                public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    model.Calls.Add((messages.ToList(), options));
                    if (model.Failure is not null)
                    {
                        throw model.Failure;
                    }

                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, model.Answers.Count > 0 ? model.Answers.Dequeue() : model.Answer)));
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
