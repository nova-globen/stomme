#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's AI workflow builder, on the canvas: the tools a model changes the pipeline with are held to the editor's own
    /// rules over this installation's real step catalog - a step type it offers, an input that step takes and a value that input
    /// holds, an outcome the step has, one flowchart at a time - and the model never reads or writes what the author alone enters.
    /// The request around them needs the assistant's key as well as the module's, and what comes back is what the save would take.
    /// </summary>
    public sealed class PipelineGraphDesignTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddExpressionDescriptorProvider<ConditionExpressionDescriptorProvider>();
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private (List<StepCatalogEntry> Catalog, Func<PipelineGraph, string?> Judge) Engine()
        {
            var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            string? Judge(PipelineGraph graph)
            {
                try
                {
                    PipelineGraphJudge.Body(graph, null, registry, services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());
                    return null;
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            return (ListStepsQueryHandler.Catalog(registry), Judge);
        }

        private PipelineGraphWorkbench Bench(PipelineGraph? graph = null)
        {
            var (catalog, judge) = Engine();
            return new PipelineGraphWorkbench(graph ?? new PipelineGraph(), catalog, judge);
        }

        // What a model does for "log every order a webhook sends": a trigger, its output saved, a step after it reading it.
        [Fact]
        public void A_pipeline_built_through_the_tools_is_one_the_save_takes()
        {
            var bench = Bench();

            Assert.Contains(JsonDocument.Parse(bench.FindStepTypes("webhook")).RootElement.EnumerateArray(), type => type.GetProperty("type").GetString() == "Stomme.Webhook");
            Assert.Contains("\"name\":\"WebhookName\"", bench.DescribeStepType("Stomme.Webhook"));
            Assert.Equal("No step type matches 'zzqx'. Try another word.", bench.FindStepTypes("zzqx"));
            Assert.StartsWith("Added 'When an order comes' as s1", bench.AddStep("Stomme.Webhook", "When an order comes", inputs: new() { ["WebhookName"] = "orders" }));
            Assert.Equal("'When an order comes' saves Result to {{ order }}.", bench.SaveOutput("s1", "result", "order"));
            Assert.StartsWith("Added 'Log it' as s2, after 'When an order comes'", bench.AddStep("Stomme.LogMessage", "Log it", after: "s1", inputs: new() { ["Message"] = "Order {{ order.id }} came in" }));

            Assert.True(bench.Changed);
            Assert.DoesNotContain("Problem", bench.Check());
            var graph = bench.Graph;
            var (hook, log) = (graph.Steps[0], graph.Steps[1]);
            Assert.Equal(32, hook.Id.Length); // a real id, as the editor makes one
            Assert.Equal("orders", hook.Inputs["WebhookName"]);
            Assert.Equal("Order {{ order.id }} came in", log.InputExpressions["Message"]);
            Assert.False(log.Inputs.ContainsKey("Message"));
            var order = Assert.Single(graph.Variables);
            Assert.Equal("order", order.Name);
            Assert.Equal(order.Id, hook.OutputVariables["Result"]);
            var connection = Assert.Single(graph.Connections);
            Assert.Equal((hook.Id, log.Id), (connection.SourceId, connection.TargetId));
            Assert.True(log.Position.X > hook.Position.X); // to its right, where the canvas shows the flow going on
            Assert.Null(Engine().Judge(graph));
        }

        [Fact]
        public void The_tools_refuse_what_the_editor_refuses_and_change_nothing_for_it()
        {
            var bench = Bench();
            Assert.Equal("Not added: there is no step type 'Acme.Unknown'. Use FindStepTypes.", bench.AddStep("Acme.Unknown", "x"));
            Assert.StartsWith("Added", bench.AddStep("Stomme.LogMessage", "Say", inputs: new() { ["Level"] = "Loud", ["Colour"] = "red" }));
            var say = bench.Graph.Steps.Single();
            Assert.False(say.Inputs.ContainsKey("Level")); // not one of its choices
            Assert.False(say.Inputs.ContainsKey("Colour")); // not one of its inputs

            Assert.Contains("'Level' takes one of", bench.SetInputs("s1", new() { ["Level"] = "Loud" }));
            Assert.Equal("Set Level on 'Say'.", bench.SetInputs("s1", new() { ["Level"] = "warning" }));
            Assert.Equal("Warning", say.Inputs["Level"]); // the choice as the step names it

            Assert.StartsWith("Added", bench.AddStep("Stomme.FlowDecision", "Big?"));
            Assert.StartsWith("Not added: 'Big?' has the outcomes True, False - name one", bench.AddStep("Stomme.LogMessage", "After", after: "s2"));
            Assert.Equal("Not connected: a step cannot lead to itself.", bench.Connect("s2", "s2", "True"));
            Assert.Equal("Not connected: 'Big?' has no outcome 'Maybe' - it has True, False.", bench.Connect("s2", "s1", "Maybe"));
            Assert.Equal("Connected 'Big?' (True) to 'Say'.", bench.Connect("s2", "s1", "true"));
            Assert.Equal("They are connected already.", bench.Connect("s2", "s1", "True"));
            Assert.Single(bench.Graph.Connections);
            Assert.Equal("Not connected: both steps must be in the pipeline. ReadPipeline lists them.", bench.Connect("s2", "s9"));
        }

        // A secret or a connection is the author's: the model reads it as set, cannot set it, and the change keeps it as it was.
        [Fact]
        public void What_the_author_alone_enters_never_reaches_the_model_and_survives_its_change()
        {
            var (catalog, _) = Engine();
            var (entry, input) = catalog.SelectMany(entry => entry.Inputs.Select(input => (Entry: entry, Input: input)))
                .First(pair => pair.Input.IsSensitive || pair.Input.UIHint == PipelineInputHints.ConnectionPicker);
            var step = new PipelineGraphStep { Id = "secretive", Type = entry.Type, Name = "Holds a secret", Inputs = { [input.Name] = "hunter2-not-for-models" } };
            var bench = Bench(new PipelineGraph { Steps = { step } });

            var read = bench.ReadPipeline();
            Assert.DoesNotContain("hunter2-not-for-models", read);
            Assert.Contains("(set by the author)", read);
            Assert.Equal($"Nothing set. Not set: '{input.Name}' is the author's to enter in the editor.", bench.SetInputs("s1", new() { [input.Name] = "the model's own" }));
            Assert.Contains("\"authorOnly\":true", bench.DescribeStepType(entry.Type));
            bench.RenameStep("s1", "Still holds it");

            Assert.Equal("hunter2-not-for-models", bench.Graph.Steps.Single().Inputs[input.Name]);
        }

        // The model is given short ids (s1, s2...) in the order the steps stand; the canvas keeps the real ones, and a real one works too.
        [Fact]
        public void A_model_names_steps_by_short_ids_and_the_canvas_keeps_the_real_ones()
        {
            var existing = Guid.NewGuid().ToString("N");
            var bench = Bench(new PipelineGraph { Steps = { new PipelineGraphStep { Id = existing, Type = "Stomme.LogMessage", Name = "First", Inputs = { ["Message"] = "one" } } } });

            var read = bench.ReadPipeline();
            Assert.Equal("s1", JsonDocument.Parse(read).RootElement.GetProperty("steps")[0].GetProperty("id").GetString());
            Assert.DoesNotContain(existing, read);
            bench.RenameStep("s1", "Ada's first");
            Assert.Contains("\"name\":\"Ada's first\"", bench.ReadPipeline()); // an apostrophe as written, not an escape
            bench.AddStep("Stomme.LogMessage", "Second", after: "s1", inputs: new() { ["Message"] = "two" });

            var connection = Assert.Single(bench.Graph.Connections);
            Assert.Equal((existing, bench.Graph.Steps[1].Id), (connection.SourceId, connection.TargetId));
            Assert.Equal("They are connected already.", bench.Connect(existing, "s2"));
            Assert.Equal("s2 is now called 'Next'.", bench.RenameStep("S2", "Next"));
        }

        // n8n's IF and Switch, and a loop's body: built from rules and cases, steps put inside, and all of it gone with its holder.
        [Fact]
        public void Conditions_cases_and_bodies_are_built_as_the_editor_builds_them()
        {
            var bench = Bench(new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-total", Name = "total", TypeName = "Int32", Value = "250" } } });
            bench.AddStep("Stomme.FlowDecision", "Big?");
            Assert.Equal("Condition of 'Big?' is now: {{ total }} is greater than 300.",
                bench.SetCondition("s1", "condition", new() { new PipelineGraphWorkbench.RuleArg { Left = "{{ total }}", Operator = "greater", Right = "300" } }));
            Assert.Equal("Not set: The condition 'bigger' is not a comparison.", bench.SetCondition("s1", "Condition", new() { new PipelineGraphWorkbench.RuleArg { Left = "{{ total }}", Operator = "bigger", Right = "3" } }));
            Assert.Equal("'Big?' has no true-or-false input 'Message'.", bench.SetCondition("s1", "Message", new()));
            bench.AddStep("Stomme.LogMessage", "Yes", after: "s1", port: "True", inputs: new() { ["Message"] = "big" });

            bench.AddStep("Stomme.FlowSwitch", "Which?");
            Assert.Equal("'Which?' has the cases Gold, Silver, then Default.", bench.SetCases("s3", new()
            {
                new PipelineGraphWorkbench.CaseArg { Label = "Gold", Rules = { new PipelineGraphWorkbench.RuleArg { Left = "{{ total }}", Operator = "at-least", Right = "1000" } } },
                new PipelineGraphWorkbench.CaseArg { Label = "Silver", Rules = { new PipelineGraphWorkbench.RuleArg { Left = "{{ total }}", Operator = "at-least", Right = "100" } } },
            }));
            Assert.Equal("Connected 'Which?' (Silver) to 'Yes'.", bench.Connect("s3", "s2", "silver"));

            bench.AddStep("Stomme.ForEach", "Each", inputs: new() { ["Items"] = "{{ total }}" });
            Assert.StartsWith("Added 'Inside' as s5", bench.AddStep("Stomme.LogMessage", "Inside", inside: "s4", inputs: new() { ["Message"] = "x" }));
            Assert.Equal("Not connected: the two steps are in different flowcharts (one is inside a body the other is not in).", bench.Connect("s1", "s5", "False"));
            var inside = bench.Graph.Steps.Single(step => step.Name == "Inside");
            Assert.Equal((bench.Graph.Steps.Single(step => step.Name == "Each").Id, "Body"), (inside.Parent, inside.ParentPort));

            var graph = bench.Graph;
            Assert.Equal("{{ total }} is greater than 300", PipelineConditions.Describe(graph.Steps[0].InputConditions["Condition"]));
            Assert.Equal(new[] { "Gold", "Silver" }, graph.Steps[2].Cases.Select(@case => @case.Label));
            Assert.Null(Engine().Judge(graph));

            Assert.Equal("Removed 'Each' and the step inside it.", bench.RemoveStep("s4"));
            Assert.DoesNotContain(bench.Graph.Steps, step => step.Name is "Each" or "Inside");
        }

        // A variable renamed through the tools - and what reads it with it, as the editor renames one.
        [Fact]
        public void A_variable_renamed_through_the_tools_is_renamed_where_it_is_read()
        {
            var bench = Bench(new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "ada" } } });
            bench.AddStep("Stomme.LogMessage", "Say", inputs: new() { ["Message"] = "Hi {{ who }}" });

            Assert.Equal("{{ who }} is now {{ person }}, in the one expression or condition reading it.", bench.RenameVariable("who", "person"));
            Assert.Equal("Hi {{ person }}", bench.Graph.Steps.Single().InputExpressions["Message"]);
            Assert.Equal("Not renamed: 'my name' could not be read in an expression - use letters, digits and _, starting with a letter or _.", bench.RenameVariable("person", "my name"));
            Assert.Equal("There is no variable 'who'. ReadPipeline lists them.", bench.RenameVariable("who", "someone"));
            Assert.Equal("No problems.", bench.Check());
        }

        // Set Variable's Variable is the variable the step sets, not a value: named - as ReadPipeline shows it, or as an expression
        // reads it - and made when the pipeline has none by that name.
        [Fact]
        public void The_variable_a_step_sets_is_named_and_made_when_the_pipeline_has_none()
        {
            var bench = Bench(new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "ada" } } });
            Assert.Contains("\"takes\":\"the name of the variable this step sets", bench.DescribeStepType("Stomme.SetVariable"));
            Assert.StartsWith("Added 'Shout' as s1", bench.AddStep("Stomme.SetVariable", "Shout", inputs: new() { ["Variable"] = "loud", ["Value"] = "{{ who | upper }}" }));
            bench.AddStep("Stomme.LogMessage", "Say", after: "s1", inputs: new() { ["Message"] = "{{ loud }}" });

            var set = bench.Graph.Steps[0];
            var loud = Assert.Single(bench.Graph.Variables, variable => variable.Name == "loud");
            Assert.Equal(loud.Id, set.InputVariables["Variable"]);
            Assert.False(set.Inputs.ContainsKey("Variable"));
            Assert.Contains("\"Variable\":\"loud\"", bench.ReadPipeline());
            Assert.Equal("No problems.", bench.Check()); // loud starts empty, and the step sets it
            Assert.Null(Engine().Judge(bench.Graph));

            Assert.Equal("Set Variable on 'Shout'.", bench.SetInputs("s1", new() { ["Variable"] = "{{ who }}" }));
            Assert.Equal("v-who", set.InputVariables["Variable"]);
            Assert.Contains("'Variable' takes the name of the variable it sets", bench.SetInputs("s1", new() { ["Variable"] = "not a name" }));
            Assert.Equal("v-who", set.InputVariables["Variable"]);
        }

        // Each answer reaches the model as the tool wrote it. By default a tool's string goes as a JSON string - quoted, every inner
        // quote escaped (measured on the first drive) - and JSON returned as JSON goes pretty-printed (the second); every character
        // is a metered token.
        [Fact]
        public async Task A_tools_answer_reaches_the_model_as_written()
        {
            var bench = Bench(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.LogMessage", Name = "Greet" } } });
            var tools = bench.AsTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);

            var read = await tools["ReadPipeline"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
            var renamed = await tools["RenameStep"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["step"] = "s1", ["name"] = "Ada's greeting" });

            Assert.StartsWith("{\"variables\":[],\"steps\":[{\"id\":\"s1\"", Assert.IsType<string>(read));
            Assert.Equal("s1 is now called 'Ada's greeting'.", Assert.IsType<string>(renamed));
        }

        // Check says what the editor would mark, and the save's refusal; Finish keeps what the model says it did.
        [Fact]
        public void Check_says_what_the_editor_and_the_save_would_say_and_finish_keeps_the_summary()
        {
            var bench = Bench(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Acme.Unknown", Name = "Foreign" } } });
            bench.AddStep("Stomme.LogMessage", "Say");

            var check = bench.Check();
            Assert.Contains("Problem at 'Say' (s2): Message needs a value.", check);
            Assert.Contains("The save would refuse it: ", check);
            Assert.Equal("Finished.", bench.Finish("  I added a log step - give it a message.  "));
            Assert.Equal("I added a log step - give it a message.", bench.Summary);
        }

        [Fact]
        public async Task The_request_needs_the_assistants_key_says_why_it_keeps_nothing_and_answers_only_what_the_save_takes()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            DesignPipelineGraphCommandHandler Handler(IPipelineGraphDesigner designer, bool mayUseAi) =>
                new(designer, new Guard(mayUseAi), services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                    services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());
            var adds = new ScriptedDesigner(bench => bench.AddStep("Stomme.LogMessage", "Hello", inputs: new() { ["Message"] = "hi" }), "Added a greeting.");
            var empty = new PipelineGraph();

            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(adds, false).Handle(new DesignPipelineGraphCommand(empty, "Say hello"), CancellationToken.None));
            Assert.Equal("Building a pipeline with AI needs assistant:use as well.", denied.Message);
            Assert.Equal(0, adds.Asked);
            await Assert.ThrowsAsync<ArgumentException>(() => Handler(adds, true).Handle(new DesignPipelineGraphCommand(empty, "  "), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => Handler(adds, true).Handle(new DesignPipelineGraphCommand(empty, new string('x', 2001)), CancellationToken.None));

            var designed = await Handler(adds, true).Handle(new DesignPipelineGraphCommand(empty, "Say hello"), CancellationToken.None);
            Assert.Equal(("Hello", "Added a greeting."), (Assert.Single(designed.Graph.Steps).Name, designed.Summary));
            Assert.Empty(empty.Steps); // the canvas sent is not the one changed

            // An answer without a change - here a question asked back - comes back as the answer alone, no graph: the canvas stays.
            var asksBack = new ScriptedDesigner(_ => { }, "Which e-mail address should it use?");
            var answered = await Handler(asksBack, true).Handle(new DesignPipelineGraphCommand(empty, "Mail it"), CancellationToken.None);
            Assert.Null(answered.Graph);
            Assert.Equal("Which e-mail address should it use?", answered.Summary);
            Assert.Equal("The AI changed nothing.", (await Handler(new ScriptedDesigner(_ => { }, ""), true).Handle(new DesignPipelineGraphCommand(empty, "Mail it"), CancellationToken.None)).Summary);

            var foreign = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Acme.Unknown" } } };
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(adds, true).Handle(new DesignPipelineGraphCommand(foreign, "Say hello"), CancellationToken.None));
            Assert.StartsWith("The AI's change was not kept - the save would refuse it: ", refused.Message);

            var none = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new NoPipelineGraphDesigner(), true).Handle(new DesignPipelineGraphCommand(empty, "Say hello"), CancellationToken.None));
            Assert.Equal("No AI is composed into this installation.", none.Message);

            // The conversation so far reaches the model - the latest six exchanges, each cut to the instruction's bound, blank ones
            // left out: a page sends it, and a page could send anything.
            var earlier = Enumerable.Range(1, 8).Select(at => new PipelineAiTurn { Asked = "asked " + at, Said = "said " + at })
                .Append(new PipelineAiTurn { Asked = " ", Said = "an answer to nothing" })
                .Append(new PipelineAiTurn { Asked = new string('x', 2500), Said = "a long one" })
                .ToList();
            await Handler(adds, true).Handle(new DesignPipelineGraphCommand(empty, "Say hello", earlier), CancellationToken.None);
            Assert.Equal(new[] { "asked 4", "asked 5", "asked 6", "asked 7", "asked 8" }, adds.Earlier.Take(5).Select(turn => turn.Asked));
            Assert.Equal((6, 2000, "a long one"), (adds.Earlier.Count, adds.Earlier[5].Asked.Length, adds.Earlier[5].Said));
        }

        // The editor offers the AI only where the site's model can be asked, and to a person holding the assistant's key - which the
        // module's page cannot read itself, so the server answers it; with no AI composed, it offers nothing.
        [Fact]
        public async Task The_editor_is_told_whether_the_sites_model_can_build_for_the_person_asking()
        {
            var steps = Options.Create(new PipelineStepOptions());
            var triggers = Options.Create(new PipelineTriggerOptions());
            async Task<bool> Ai(IPipelineGraphDesigner designer, bool mayUseAi) =>
                (await new GetCapabilitiesQueryHandler(steps, triggers, designer, new Guard(mayUseAi)).Handle(new GetCapabilitiesQuery(), CancellationToken.None)).Ai;
            var available = new ScriptedDesigner(_ => { }, "");

            Assert.True(await Ai(available, mayUseAi: true));
            Assert.False(await Ai(available, mayUseAi: false));
            Assert.False(await Ai(new NoPipelineGraphDesigner(), mayUseAi: true));
        }

        private sealed class ScriptedDesigner(Action<PipelineGraphWorkbench> work, string says) : IPipelineGraphDesigner
        {
            public int Asked { get; private set; }

            /// <summary>The conversation the last request told the model.</summary>
            public IReadOnlyList<PipelineAiTurn> Earlier { get; private set; } = Array.Empty<PipelineAiTurn>();

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public Task<string> DesignAsync(PipelineGraphWorkbench workbench, string instruction, IReadOnlyList<PipelineAiTurn>? earlier = null, CancellationToken cancellationToken = default)
            {
                Asked++;
                Earlier = earlier ?? Array.Empty<PipelineAiTurn>();
                work(workbench);
                return Task.FromResult(says);
            }
        }

        private sealed class Guard(bool mayUseAi) : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission != Stomme.AiAssistant.AssistantPermissions.Use || mayUseAi);

            public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }
    }
}
