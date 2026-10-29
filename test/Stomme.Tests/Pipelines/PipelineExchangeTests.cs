#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A pipeline as a FILE and the pipelines a site starts from. The file is the editor's graph with a name, read
    /// back exactly; a file that is not one is refused with the reason, before anything is stored. Every shipped starter is held to the
    /// engine's save and the problem check, and the smallest one runs.
    /// </summary>
    public sealed class PipelineExchangeTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private sealed class User(string name, int id) : ICurrentUser
        {
            public int? UserId => id;
            public string? UserName => name;
            public bool IsAuthenticated => true;
            public IReadOnlySet<string> Roles => new HashSet<string>();
            public int SiteId => 1;
        }

        [Fact]
        public void A_file_carries_the_graph_its_name_and_its_description_and_reads_back_as_written()
        {
            var graph = PipelineStarters.Find("hourly-check")!.Graph();
            graph.Steps[1].Comment = "the address to watch";
            graph.Steps[1].Retries = 2;

            var text = PipelineExchange.Write("Hourly check", "Watches example.com", graph);
            var read = PipelineExchange.Read(text);

            Assert.Equal(("Hourly check", "Watches example.com"), (read.Name, read.Description));
            Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(read.Graph));
            // The format names itself, and an input's name keeps its spelling (a dictionary key is not camel-cased).
            var json = JsonNode.Parse(text)!;
            Assert.Equal((PipelineExchange.FormatName, PipelineExchange.CurrentVersion), (json["format"]!.GetValue<string>(), json["formatVersion"]!.GetValue<int>()));
            Assert.Equal("https://example.com/", json["graph"]!["steps"]![1]!["inputs"]!["Url"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("Hourly HTTP check", "hourly-http-check.pipeline.json")]
        [InlineData("  Rapport: Q3 / 2026 ", "rapport-q3-2026.pipeline.json")]
        [InlineData("åäö", "pipeline.pipeline.json")]
        [InlineData(null, "pipeline.pipeline.json")]
        public void A_files_name_is_the_pipelines_name_in_plain_letters(string? name, string expected) =>
            Assert.Equal(expected, PipelineExchange.FileName(name));

        public static TheoryData<string, string> Refusals => new()
        {
            { "", "The file is empty." },
            { "{ not json", "The file is not a pipeline's JSON" },
            { "{\"format\":\"n8n\",\"graph\":{}}", "is not a Nova Globen Stomme pipeline (its format is 'n8n'" },
            { "{\"format\":\"stomme-pipeline\",\"formatVersion\":2,\"graph\":{}}", "written by a newer Nova Globen Stomme (format version 2)" },
            { "{\"format\":\"stomme-pipeline\"}", "The file holds no pipeline." },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\"}]}}", "Step 1 in the file has no type." },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"type\":\"Stomme.WriteLine\"}]}}", "Step 1 in the file has no id." },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\",\"type\":\"T\"},{\"id\":\"a\",\"type\":\"T\"}]}}", "Two steps in the file have the id 'a'." },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\",\"type\":\"T\"}],\"connections\":[{\"sourceId\":\"a\",\"targetId\":\"gone\"}]}}", "names a step the file does not hold" },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\",\"type\":\"T\",\"parent\":\"gone\",\"parentPort\":\"Body\"}]}}", "sits inside 'gone', which the file does not hold" },
            { "{\"format\":\"stomme-pipeline\",\"graph\":{\"variables\":[{\"id\":\"v\"},{\"id\":\"v\"}]}}", "Two variables in the file have the id 'v'." },
        };

        [Theory]
        [MemberData(nameof(Refusals))]
        public void A_file_that_is_not_a_pipeline_is_refused_with_the_reason(string text, string reason) =>
            Assert.Contains(reason, Assert.Throws<PipelineExchangeException>(() => PipelineExchange.Read(text)).Message);

        [Fact]
        public void A_file_with_too_many_steps_is_refused_and_a_sparse_one_reads_with_its_lists_filled()
        {
            var big = new PipelineGraph();
            big.Steps.AddRange(Enumerable.Range(0, PipelineExchange.MaxSteps + 1).Select(index => new PipelineGraphStep { Id = "s" + index, Type = "Stomme.WriteLine" }));
            Assert.Contains("a pipeline holds at most", Assert.Throws<PipelineExchangeException>(() => PipelineExchange.Read(PipelineExchange.Write("Big", null, big))).Message);

            var sparse = PipelineExchange.Read("{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\",\"type\":\"Stomme.WriteLine\"}],\"notes\":[{\"text\":\"hi\",\"parent\":\"gone\"}]}}");
            var step = Assert.Single(sparse.Graph!.Steps);
            Assert.NotNull(step.Position);
            Assert.Empty(step.Inputs);
            Assert.Empty(step.InputVariables);
            Assert.Empty(step.InputExpressions);
            Assert.Empty(step.OutputVariables);
            Assert.Empty(sparse.Graph.Connections);
            Assert.Empty(sparse.Graph.Variables);
            // A note without an id gets one; one in a body the file lacks moves to the pipeline's own canvas.
            var note = Assert.Single(sparse.Graph.Notes);
            Assert.False(string.IsNullOrEmpty(note.Id));
            Assert.Null(note.Parent);
        }

        // A test value kept with the pipeline can be real data, so a file (an export, a copy to the clipboard) never
        // carries one - not written, and not read from a file that holds one anyway; the canvas's graph is left as it was.
        [Fact]
        public void A_file_carries_no_kept_test_values_either_way()
        {
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.WriteLine" } },
                Variables = { new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world", TestValue = "Ada" } },
            };

            var text = PipelineExchange.Write("Kept", null, graph);
            Assert.DoesNotContain("Ada", text);
            Assert.Equal("Ada", graph.Variables.Single().TestValue);
            Assert.Equal(("who", "world", (string?)null), PipelineExchange.Read(text).Graph!.Variables.Select(variable => (variable.Name, variable.Value, (string?)variable.TestValue)).Single());

            var written = "{\"format\":\"stomme-pipeline\",\"graph\":{\"steps\":[{\"id\":\"a\",\"type\":\"Stomme.WriteLine\"}],\"variables\":[{\"id\":\"who\",\"name\":\"who\",\"testValue\":\"Ada\"}]}}";
            Assert.Null(PipelineExchange.Read(written).Graph!.Variables.Single().TestValue);
        }

        // A folder as ONE file: each pipeline with its id and its folder below the one exported, no kept test value, read
        // back as n8n's list of workflows is; a folder in the file that cannot be one is none, and below one too deep it is not.
        [Fact]
        public void Many_pipelines_are_one_file_with_their_ids_and_folders()
        {
            var calls = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "c", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "notify" } } },
                Variables = { new PipelineGraphVariable { Id = "v", Name = "who", Value = "world", TestValue = "Ada" } },
            };
            var text = PipelineExchange.WriteMany(new[]
            {
                new PipelineExchangeDocument { Id = "charge", Name = "Charge", Description = "Charges a card", Graph = calls },
                new PipelineExchangeDocument { Id = "notify", Name = "Notify", Folder = "Mail", Graph = new PipelineGraph { CalledBy = "charge" } },
                new PipelineExchangeDocument { Name = "No graph" },
            });

            Assert.DoesNotContain("Ada", text);
            var read = PipelineExchange.ReadMany(text);
            Assert.Equal(new[] { ("charge", "Charge", (string?)null), ("notify", "Notify", "Mail") }, read.Select(document => (document.Id!, document.Name!, document.Folder)).ToArray());
            Assert.Equal("Charges a card", read[0].Description);
            Assert.Equal("notify", read[0].Graph!.Steps.Single().Inputs["PipelineDefinitionId"]);
            Assert.Equal("charge", read[1].Graph!.CalledBy);

            Assert.Null(PipelineExchange.Read("{\"format\":\"stomme-pipeline\",\"folder\":\"a|b\",\"graph\":{\"steps\":[]}}").Folder);
            Assert.Equal("Billing/Mail", PipelineExchange.FolderIn("Billing", "Mail"));
            Assert.Equal("Mail", PipelineExchange.FolderIn("", "Mail"));
            Assert.Equal("Billing", PipelineExchange.FolderIn("Billing", null));
            Assert.Equal("A/B/C", PipelineExchange.FolderIn("A/B/C", "D/E")); // five deep: kept where it is imported
        }

        // What a graph names of other pipelines, pointed at the ones they became: a call step's pipeline (Execute, Dispatch,
        // Bulk dispatch), a "Pipeline failed"'s, and the ones that may call it - a pipeline the file did not hold left as named, an
        // expression left to the run.
        [Fact]
        public void What_a_pipeline_names_of_others_in_its_file_is_pointed_at_the_new_ones()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "old-1" } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.DispatchPipeline", Inputs = { ["PipelineDefinitionId"] = "elsewhere" } },
                    new PipelineGraphStep { Id = "c", Type = PipelineFailure.StepType, Inputs = { ["Pipeline"] = "old-2" } },
                    new PipelineGraphStep { Id = "d", Type = "Stomme.ExecutePipeline", InputExpressions = { ["PipelineDefinitionId"] = "{{ which }}" } },
                    new PipelineGraphStep { Id = "e", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "old-1" } },
                },
                CalledBy = "old-2,outside",
            };

            Assert.Equal(new[] { "old-1", "elsewhere", "old-2", "outside" }, PipelineExchange.References(graph));
            var changed = PipelineExchange.Repoint(graph, new Dictionary<string, string> { ["old-1"] = "new-1", ["old-2"] = "new-2" });

            Assert.Equal(3, changed);
            Assert.Equal(new[] { "new-1", "elsewhere", "new-2" }, graph.Steps.Take(3).Select(step => step.Inputs.Values.Single()));
            Assert.Equal("{{ which }}", graph.Steps[3].InputExpressions["PipelineDefinitionId"]);
            Assert.Equal("old-1", graph.Steps[4].Inputs["Text"]); // a text is not a reference
            Assert.Equal("new-2,outside", graph.CalledBy);
            Assert.Equal(0, PipelineExchange.Repoint(graph, new Dictionary<string, string>()));
        }

        // A starter cannot name one of the site's connections, so a step that needs one is left for the author to choose - the one
        // problem a starter may carry: a connection picker left empty, which the editor says until it is chosen.
        private static bool NamesNoConnection(PipelineGraph graph, IReadOnlyList<StepCatalogEntry> catalog, PipelineGraphProblem problem) =>
            problem.Code == PipelineGraphProblems.MissingInput
            && graph.Steps.FirstOrDefault(step => step.Id == problem.StepId) is { } step
            && catalog.FirstOrDefault(entry => entry.Type == step.Type)?.Inputs.FirstOrDefault(input => input.Name == problem.Input)?.UIHint == PipelineInputHints.ConnectionPicker;

        [Fact]
        public void Every_starter_is_one_the_save_accepts_with_no_problem_that_keeps_it_from_being_published()
        {
            using var scope = _engine.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var catalog = ListStepsQueryHandler.Catalog(registry);
            Assert.True(PipelineStarters.All.Count >= 5);
            Assert.Equal(PipelineStarters.All.Count, PipelineStarters.All.Select(starter => starter.Id).Distinct().Count());

            // And the failure handler the editor makes from the pipeline that fails (n8n's "Error workflow" setting).
            var starters = PipelineStarters.All.Select(starter => (starter.Id, Graph: (Func<PipelineGraph>)starter.Graph))
                .Append(("failure-handler", () => PipelineStarters.FailureHandler("def-1", "Orders")));
            foreach (var (id, make) in starters)
            {
                var graph = make();
                Assert.All(graph.Steps, step => Assert.Contains(catalog, entry => entry.Type == step.Type));
                var body = PipelineGraphJudge.Body(graph, null, registry, scope.ServiceProvider.GetRequiredService<IIdentityGenerator>(), scope.ServiceProvider.GetRequiredService<IStepSerializer>());
                var errors = PipelineGraphProblems.Find(PipelineGraphCodec.ToGraph(body), catalog).Where(problem => problem.Severity == PipelineGraphProblems.Error && !NamesNoConnection(graph, catalog, problem)).ToList();
                Assert.True(errors.Count == 0, id + ": " + string.Join("; ", errors.Select(problem => problem.Step + ": " + problem.Message)));
                Assert.True(PipelineGraphProblems.Find(graph, catalog).All(problem => problem.Severity != PipelineGraphProblems.Error || NamesNoConnection(graph, catalog, problem)), id);

                // Each call is a fresh copy: a change made to one does not reach the next.
                graph.Steps[0].Name = "changed";
                Assert.NotEqual("changed", make().Steps[0].Name);
            }

            // The handler watches the pipeline it was made from - as written in the body the save keeps - and is named for it.
            var handler = PipelineGraphCodec.ToGraph(PipelineGraphJudge.Body(PipelineStarters.FailureHandler("def-1", "Orders"), null, registry,
                scope.ServiceProvider.GetRequiredService<IIdentityGenerator>(), scope.ServiceProvider.GetRequiredService<IStepSerializer>()));
            var watching = handler.Steps.Single(step => step.Type == PipelineFailure.StepType);
            Assert.Equal(("def-1", "When 'Orders' fails"), (watching.Inputs["Pipeline"], watching.Name));
            Assert.Equal("When it fails", PipelineStarters.FailureHandler("def-1", " ").Steps.Single(step => step.Type == PipelineFailure.StepType).Name);
        }

        [Fact]
        public async Task The_smallest_starter_runs_and_says_hello_to_its_variable()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphJudge.Body(PipelineStarters.Find("greeting")!.Graph(), null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());

            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains(result.Journal.PipelineExecutionLogEntries, entry => entry.Message == "Hello, world!");
        }

        // An n8n workflow brought over is a graph this installation's save accepts - every step a type it offers, the
        // placeholders and the conditions left to set no refusal - and an import stores it as one draft, its notes with it.
        [Fact]
        public async Task An_n8n_workflow_brought_over_is_one_the_save_accepts_and_the_import_stores()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            var catalog = ListStepsQueryHandler.Catalog(registry);
            var document = PipelineExchange.Read(N8nWorkflowImportTests.Workflow);
            Assert.All(document.Graph!.Steps, step => Assert.Contains(catalog, entry => entry.Type == step.Type));
            PipelineGraphJudge.Body(document.Graph, null, registry, services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());

            var import = new ImportPipelineCommandHandler(services.GetRequiredService<IPipelineDefinitionPublisher>(), registry, services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new User("ada", 7));
            var created = await import.Handle(new ImportPipelineCommand(document.Name!, document.Description, document.Graph), CancellationToken.None);

            var stored = await services.GetRequiredService<IPipelineDefinitionStore>().FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = created.DefinitionId, VersionOptions = Stomme.Engine.Common.Models.VersionOptions.Latest });
            var graph = PipelineGraphCodec.ToGraph(stored!.StringData);
            Assert.Equal(("Order alerts", false), (created.Name, created.IsPublished));
            Assert.Equal(document.Graph.Steps.Select(step => step.Name), graph.Steps.Select(step => step.Name));
            Assert.Equal(document.Graph.Connections.Count, graph.Connections.Count);
            Assert.Equal(2, graph.Notes.Count);
            Assert.True(graph.Steps.Single(step => step.Name == "LINE").Disabled); // a node with no step here
        }

        [Fact]
        public async Task An_import_is_judged_before_anything_is_stored_and_a_good_one_is_one_draft_with_its_body_and_author()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var import = new ImportPipelineCommandHandler(publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new User("ada", 7));
            var before = (await definitions.FindManyAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter())).Count();

            // A step type this installation lacks: refused by the save's rule, and nothing stored.
            var foreign = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Acme.Unknown" } } };
            var refused = await Assert.ThrowsAnyAsync<System.ArgumentException>(() => import.Handle(new ImportPipelineCommand("Foreign", null, foreign), CancellationToken.None));
            Assert.Equal("'Acme.Unknown' version 1 is not a step type this installation offers.", refused.Message);
            await Assert.ThrowsAnyAsync<System.ArgumentException>(() => import.Handle(new ImportPipelineCommand(" ", null, PipelineStarters.Find("greeting")!.Graph()), CancellationToken.None));
            Assert.Equal(before, (await definitions.FindManyAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter())).Count());

            var created = await import.Handle(new ImportPipelineCommand("Hello", "From a starter", PipelineStarters.Find("greeting")!.Graph()), CancellationToken.None);

            Assert.Equal(("Hello", 1, false), (created.Name, created.Version, created.IsPublished));
            var stored = await definitions.FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = created.DefinitionId, VersionOptions = Stomme.Engine.Common.Models.VersionOptions.Latest });
            var graph = PipelineGraphCodec.ToGraph(stored!.StringData);
            Assert.Equal("Hello, {{ who }}!", Assert.Single(graph.Steps).InputExpressions["Message"]);
            Assert.Equal("world", Assert.Single(graph.Variables).Value);
            Assert.Single(graph.Notes);
            Assert.Equal("ada", PipelineWire.ToVersion(stored).SavedBy);
            Assert.Equal(before + 1, (await definitions.FindManyAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter())).Count());
        }
    }
}
