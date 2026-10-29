#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The server holds the publish to the editor's rule: a graph with an ERROR (an input its step cannot do
    /// without, left empty) is not published, by the module's own command, whoever sends it; a WARNING (a step nothing leads to)
    /// holds nothing back. Read against the engine as the host composes it, the module's step families included.
    /// </summary>
    public sealed class PipelinePublishProblemsTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { }); // the archive's marks
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private sealed class Author : Stomme.Security.Abstractions.ICurrentUser
        {
            public int? UserId => 7;
            public string? UserName => "ada";
            public bool IsAuthenticated => true;
            public System.Collections.Generic.IReadOnlySet<string> Roles => new System.Collections.Generic.HashSet<string>();
            public int SiteId => 1;
        }

        [Fact]
        public async Task A_graph_with_an_error_is_not_published_and_a_warning_holds_nothing_back()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var registry = services.GetRequiredService<IStepRegistry>();
            var populator = services.GetRequiredService<IRegistriesPopulator>();

            var created = await new CreatePipelineCommandHandler(publisher).Handle(new CreatePipelineCommand("Problems", null), CancellationToken.None);
            var publish = new PublishPipelineCommandHandler(definitions, publisher, registry, populator,
                services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>());

            // Saved as the editor's save stores a graph: through the codec, judged by the engine.
            async Task SaveAsync(PipelineGraph graph)
            {
                var draft = await publisher.GetDraftAsync(created.DefinitionId, VersionOptions.Latest);
                draft!.StringData = PipelineGraphJudge.Body(graph, draft.StringData, registry, services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());
                await publisher.SaveDraftAsync(draft);
            }

            Task<bool> PublishedAsync() =>
                definitions.AnyAsync(new PipelineDefinitionFilter { DefinitionId = created.DefinitionId, VersionOptions = VersionOptions.Published });

            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 30, Y = 40 } } },
            };
            await SaveAsync(graph);

            var refused = await publish.Handle(new PublishPipelineCommand(created.DefinitionId), CancellationToken.None);
            Assert.False(refused!.Succeeded);
            Assert.Equal(new[] { "Say: Text needs a value." }, refused.Errors);
            Assert.False(await PublishedAsync());

            // The text given, and a second step nothing leads to - a warning: published.
            graph.Steps[0].Inputs["Text"] = "hello";
            graph.Steps.Add(new PipelineGraphStep { Id = "idle", Type = "Stomme.Delay", Position = new GraphPosition { X = 290, Y = 40 } });
            await SaveAsync(graph);

            var published = await publish.Handle(new PublishPipelineCommand(created.DefinitionId), CancellationToken.None);
            Assert.True(published!.Succeeded, string.Join("; ", published.Errors));
            Assert.True(await PublishedAsync());

            // n8n's archive: set aside, the pipeline is unpublished and marked - listed as archived, who did it kept; publishing
            // it is refused until it is restored, a draft, after which it publishes as before.
            var keyValues = services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>();
            var tenants = services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>();
            var archive = new ArchivePipelineCommandHandler(definitions, publisher, keyValues, tenants, new Author(), services.GetRequiredService<Stomme.Engine.Common.ISystemClock>());
            Assert.True(await archive.Handle(new ArchivePipelineCommand(created.DefinitionId), CancellationToken.None));
            Assert.False(await PublishedAsync());
            var listed = await new ListPipelinesQueryHandler(definitions, services.GetRequiredService<IPipelineInstanceStore>(), keyValues, tenants).Handle(new ListPipelinesQuery(), CancellationToken.None);
            var archivedView = Assert.Single(listed, view => view.DefinitionId == created.DefinitionId);
            Assert.Equal((true, "ada"), (archivedView.ArchivedOnUtc is not null, archivedView.ArchivedBy));
            var whileArchived = await publish.Handle(new PublishPipelineCommand(created.DefinitionId), CancellationToken.None);
            Assert.Equal((false, "The pipeline is archived - restore it to publish it."), (whileArchived!.Succeeded, whileArchived.Errors.Single()));
            Assert.False(await PublishedAsync());

            Assert.True(await new RestorePipelineCommandHandler(definitions, keyValues, tenants).Handle(new RestorePipelineCommand(created.DefinitionId), CancellationToken.None));
            Assert.Null(await PipelineArchiveStore.FindAsync(keyValues, tenants.TenantId, created.DefinitionId, CancellationToken.None));
            Assert.True((await publish.Handle(new PublishPipelineCommand(created.DefinitionId), CancellationToken.None))!.Succeeded);
            Assert.True(await PublishedAsync());
            Assert.False(await archive.Handle(new ArchivePipelineCommand("no-such-pipeline"), CancellationToken.None));

            // A pipeline that is not there is not there.
            Assert.Null(await publish.Handle(new PublishPipelineCommand("no-such-pipeline"), CancellationToken.None));
        }

        // The editor says "is not a whole number" exactly when the save would refuse the value: the checker's rule against the
        // codec's own, over the real catalog's typed inputs.
        [Theory]
        [InlineData("Stomme.Delay", "Seconds", new[] { "5", "-3", " 7 ", "5.5", "five", "1e3", "" })]
        [InlineData("Stomme.ParseCsv", "HasHeader", new[] { "true", "False", " TRUE ", "yes", "1", "" })]
        [InlineData("Stomme.SendHttpRequest", "Method", new[] { "GET", "post", "Patch", "FETCH", "2", "" })]
        public async Task A_value_the_editor_marks_is_exactly_one_the_save_refuses(string type, string input, string[] texts)
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            var entry = ListStepsQueryHandler.Catalog(registry).Single(candidate => candidate.Type == type);
            var view = entry.Inputs.Single(candidate => candidate.Name == input);

            foreach (var text in texts)
            {
                var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "s", Type = type, Inputs = { [input] = text } } } };
                bool saved;
                try
                {
                    PipelineGraphJudge.Body(graph, null, registry, services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());
                    saved = true;
                }
                catch (System.ArgumentException)
                {
                    saved = false;
                }

                Assert.True(saved == PipelineGraphProblems.Holds(view, text), $"'{text}' for {type}.{input}: the save {(saved ? "takes" : "refuses")} it, the editor says otherwise");
            }
        }
    }
}
