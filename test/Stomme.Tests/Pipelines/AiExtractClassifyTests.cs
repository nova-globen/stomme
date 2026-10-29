#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Information Extractor and Text Classifier, on the site's model seam as Ask AI is, run by the real engine with
    /// the seam scripted: Extract information asks for the author's fields and holds the answer to them (each value as its field
    /// holds it, a key not asked for dropped, a value that will not convert empty); Classify text puts the text in one category and
    /// the run goes on through that category's port - the categories are the step's outcomes, from a graph through the codec.
    /// </summary>
    public sealed class AiExtractClassifyTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly ScriptedModel _model = new();
        private readonly StringWriter _console = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" } });
            services.AddSingleton<IAssistantModelAccess>(_model);
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { }); // the Evaluation step records there
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddExpressionDescriptorProvider<ConditionExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private async Task<RunPipelineResult> RunAsync(IStep step, IDictionary<string, object>? input = null)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step, input is null ? null : new Stomme.Engine.Pipelines.Options.RunPipelineOptions { Input = input });
        }

        private async Task<RunPipelineResult> RunAsync(PipelineGraph graph)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            return await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static string Incidents(RunPipelineResult run) => string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message));

        [Fact]
        public void The_fields_are_read_as_the_author_writes_them()
        {
            var fields = ExtractInformation.ParseFields("name: who wrote\ntotal (number): the order's total\npaid (true or false)\ndue (Date): when it is due\nnote", out var error)!;
            Assert.Null(error);
            Assert.Equal(new[] { ("name", PipelineTableColumnType.Text, "who wrote"), ("total", PipelineTableColumnType.Number, "the order's total"), ("paid", PipelineTableColumnType.Boolean, ""), ("due", PipelineTableColumnType.Date, "when it is due"), ("note", PipelineTableColumnType.Text, "") },
                fields.Select(field => (field.Name, field.Type, field.Description)));

            Assert.Null(ExtractInformation.ParseFields("the total: a number", out error));
            Assert.StartsWith("'the total: a number' is not a field", error);
            Assert.Null(ExtractInformation.ParseFields("total (money): the total", out error));
            Assert.Equal("'money' is not what a field holds: text, number, true or false, or date.", error);
            Assert.Null(ExtractInformation.ParseFields("total\nTotal (number)", out error));
            Assert.Equal("The field 'Total' is there twice.", error);
            Assert.Null(ExtractInformation.ParseFields(" \n ", out error));
            Assert.Equal("The step needs at least one field to extract.", error);
        }

        [Fact]
        public async Task Extract_information_asks_for_the_fields_and_holds_the_answer_to_them()
        {
            _model.Answer = "```json\n{\"name\": \"Ada\", \"total\": \"12.50\", \"paid\": \"yes\", \"due\": \"2026-10-09\", \"extra\": \"not asked for\"}\n```";
            var run = await RunAsync(new ExtractInformation
            {
                Text = new("Order from Ada, 12.50 paid, due 9 October."),
                Fields = new("name: who ordered\ntotal (number): the order's total\npaid (true or false)\ndue (date)\nemail: their address"),
                Instructions = new("The text is an order confirmation."),
            });

            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished, Incidents(run));
            Assert.Equal("""{"name":"Ada","total":12.5,"paid":true,"due":"2026-10-09T00:00:00Z","email":null}""", OutputsOf<ExtractInformation>(run)["Result"]);
            Assert.Equal(1, OutputsOf<ExtractInformation>(run)["MissingCount"]);
            var call = Assert.Single(_model.Calls);
            Assert.Equal(ChatResponseFormat.Json, call.Options?.ResponseFormat);
            Assert.Equal(AskAi.Agent, Assert.Single(_model.Opened)); // metered and quota-gated as Ask AI's calls
            var system = call.Messages[0].Text;
            Assert.Contains("- \"total\": a number - the order's total", system);
            Assert.Contains("- \"due\": an ISO 8601 date (ISO 8601, like 2026-10-07 or 2026-10-07T14:30:00Z)", system);
            Assert.EndsWith("The text is an order confirmation." + Environment.NewLine, system);
            Assert.Equal((ChatRole.User, "Order from Ada, 12.50 paid, due 9 October."), (call.Messages[1].Role, call.Messages[1].Text));

            // A value that will not convert is left empty, not invented; an answer that is not JSON fails the step.
            _model.Answer = """{"name": "Ada", "total": "$12.50"}""";
            run = await RunAsync(new ExtractInformation { Text = new("..."), Fields = new("name\ntotal (number)") });
            Assert.Equal("""{"name":"Ada","total":null}""", OutputsOf<ExtractInformation>(run)["Result"]);
            _model.Answer = "Ada ordered.";
            run = await RunAsync(new ExtractInformation { Text = new("..."), Fields = new("name"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("AI_NOT_JSON: The model's answer is not JSON", (string)OutputsOf<ExtractInformation>(run)["Error"]!);

            // The seam's refusal is the step's failure, in its words.
            _model.Refusal = new AssistantUnavailableException(AssistantUnavailableReason.QuotaExceeded, "The site's AI quota for this month is spent.");
            run = await RunAsync(new ExtractInformation { Text = new("..."), Fields = new("name"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("AI_QUOTA_EXCEEDED: The site's AI quota for this month is spent.", OutputsOf<ExtractInformation>(run)["Error"]);
        }

        // n8n's Correctness and Helpfulness metrics: the site's model judges an evaluation's answer from 1 to 5 - told the
        // scale, the question, the reference (correctness) and the answer, asked for JSON - its score the step's Score; outside an
        // evaluation the model is not asked at all (a call costs the site's quota); a score that is not from 1 to 5 fails the step.
        [Fact]
        public async Task The_evaluation_step_has_the_model_judge_an_answer_only_in_an_evaluation()
        {
            var evaluating = new Dictionary<string, object> { [EvaluationTrigger.EvaluationInputKey] = "eval-1", [EvaluationTrigger.RowIdInputKey] = "row-1" };
            Evaluation Judge(EvaluationOperation operation) =>
                new() { Operation = new(operation), Question = new("What is 2+2?"), Expected = new("4"), Actual = new("Four."), OnFailure = new(FailurePolicy.Continue) };

            _model.Answer = """{"score": 4, "reason": "Right, said in words."}""";
            var run = await RunAsync(Judge(EvaluationOperation.JudgeCorrectness), evaluating);
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished, Incidents(run));
            Assert.Equal("4", OutputsOf<Evaluation>(run)["Score"]);
            var call = Assert.Single(_model.Calls);
            Assert.Equal(ChatResponseFormat.Json, call.Options?.ResponseFormat);
            Assert.Equal((Evaluation.CorrectnessPrompt, "QUESTION:\nWhat is 2+2?\n\nREFERENCE:\n4\n\nANSWER:\nFour."), (call.Messages[0].Text, call.Messages[1].Text));

            _model.Answer = """{"score": "5"}""";
            run = await RunAsync(Judge(EvaluationOperation.JudgeHelpfulness), evaluating);
            Assert.Equal("5", OutputsOf<Evaluation>(run)["Score"]);
            Assert.Equal((Evaluation.HelpfulnessPrompt, "QUESTION:\nWhat is 2+2?\n\nANSWER:\nFour."), (_model.Calls[1].Messages[0].Text, _model.Calls[1].Messages[1].Text));

            _model.Answer = """{"score": 9}""";
            run = await RunAsync(Judge(EvaluationOperation.JudgeCorrectness), evaluating);
            Assert.Equal("AI_NO_SCORE: The model gave no score from 1 to 5.", OutputsOf<Evaluation>(run)["Error"]);

            run = await RunAsync(Judge(EvaluationOperation.JudgeCorrectness));
            Assert.Equal(3, _model.Calls.Count); // not asked
            Assert.Contains(run.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Not evaluating");
        }

        [Fact]
        public async Task Classify_text_goes_on_through_the_port_of_the_category_the_model_chose()
        {
            PipelineGraph Graph() => new()
            {
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "sort", Type = "Stomme.ClassifyText",
                        Inputs =
                        {
                            ["Text"] = "My invoice is charged twice.",
                            ["Branches"] = PipelineOutcomes.WriteBranches(new[] { "Billing", "Support", "Other" }),
                            ["Descriptions"] = "Billing: invoices, charges, refunds\nSupport: something does not work",
                        },
                    },
                    new PipelineGraphStep { Id = "billing", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "to billing" } },
                    new PipelineGraphStep { Id = "support", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "to support" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "sort", SourcePort = "Billing", TargetId = "billing" },
                    new PipelineGraphConnection { SourceId = "sort", SourcePort = "Support", TargetId = "support" },
                },
            };

            _model.Answer = """{"category": "billing"}""";
            var run = await RunAsync(Graph());
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished, Incidents(run));
            Assert.Contains("to billing", _console.ToString());
            Assert.DoesNotContain("to support", _console.ToString());
            Assert.Equal("Billing", OutputsOf<ClassifyText>(run)["Category"]);
            Assert.Equal("My invoice is charged twice.", OutputsOf<ClassifyText>(run)["Result"]); // the text passes through
            var system = Assert.Single(_model.Calls).Messages[0].Text;
            Assert.Contains("- \"Billing\": invoices, charges, refunds", system);
            Assert.Contains("- \"Other\"" + Environment.NewLine, system);

            _model.Answer = "Support.";
            run = await RunAsync(Graph());
            Assert.Contains("to support", _console.ToString());

            // An answer that is none of the categories fails the step - "Other" is the author's way to catch what fits nowhere.
            _model.Answer = """{"category": "Shipping"}""";
            run = await RunAsync(Graph());
            Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
            Assert.Contains("which is none of: Billing, Support, Other", Incidents(run));
        }

        private sealed class ScriptedModel : IAssistantModelAccess
        {
            public string Answer { get; set; } = "";

            public AssistantUnavailableException? Refusal { get; set; }

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
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, model.Answer)));
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
