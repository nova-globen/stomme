#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Collaboration.Realtime;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Versions you can trust: every version the engine keeps is listed, newest first, with who saved it
    /// (the module stamps it - the engine keeps no author); one version's graph can be read; and the difference between two
    /// graphs is told in words, the fields a merge sees, positions left out.
    /// </summary>
    public sealed class PipelineVersionsTests : IAsyncLifetime
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
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { }); // the archive's marks (GetPipelineQuery)
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
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

        private sealed class QuietNotifier : ICollaborationNotifier
        {
            public Task NotifyAsync(string resourceType, string resourceId, string kind, int byUserId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private static PipelineGraph Graph(string text) => new()
        {
            Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Text"] = text } } },
        };

        [Fact]
        public async Task Every_version_is_listed_newest_first_with_who_saved_it_and_each_ones_graph_reads()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var created = await new CreatePipelineCommandHandler(publisher).Handle(new CreatePipelineCommand("Versions", null), CancellationToken.None);
            SaveGraphCommandHandler Save(string who) => new(publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new QuietNotifier(), new User(who, 7));

            await Save("ada").Handle(new SaveGraphCommand(created.DefinitionId, Graph("first")), CancellationToken.None);
            Assert.True((await publisher.PublishAsync(created.DefinitionId)).Succeeded);
            await Save("grace").Handle(new SaveGraphCommand(created.DefinitionId, Graph("second")), CancellationToken.None);

            var versions = await new ListVersionsQueryHandler(definitions).Handle(new ListVersionsQuery(created.DefinitionId), CancellationToken.None);
            Assert.Equal(new[] { (2, false, true, "grace"), (1, true, false, "ada") }, versions.Select(version => (version.Version, version.IsPublished, version.IsLatest, version.SavedBy)));

            var first = await new GetVersionGraphQueryHandler(definitions, new Guard(author: true)).Handle(new GetVersionGraphQuery(created.DefinitionId, 1), CancellationToken.None);
            Assert.Equal("first", first!.Steps.Single().Inputs["Text"]);
            Assert.Null(await new GetVersionGraphQueryHandler(definitions, new Guard(author: true)).Handle(new GetVersionGraphQuery(created.DefinitionId, 9), CancellationToken.None));
        }

        // What changed, said with a publish - kept with the version it publishes and listed with it; a draft made over it
        // is a copy, its note not its own, so shows none; a publish that says nothing leaves its version none (never the last
        // one's); a note too long is refused and nothing is published.
        [Fact]
        public async Task A_publish_keeps_what_the_author_said_changed_with_its_version()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var created = await new CreatePipelineCommandHandler(publisher).Handle(new CreatePipelineCommand("Noted", null), CancellationToken.None);
            SaveGraphCommandHandler Save() => new(publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new QuietNotifier(), new User("ada", 7));
            PublishPipelineCommandHandler Publish() => new(definitions, publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>());
            async Task<(int, bool, string?)[]> VersionsAsync() =>
                (await new ListVersionsQueryHandler(definitions).Handle(new ListVersionsQuery(created.DefinitionId), CancellationToken.None))
                    .Select(version => (version.Version, version.IsPublished, version.Note)).ToArray();

            await Save().Handle(new SaveGraphCommand(created.DefinitionId, Graph("first")), CancellationToken.None);
            Assert.True((await Publish().Handle(new PublishPipelineCommand(created.DefinitionId, "  Says hello first  "), CancellationToken.None))!.Succeeded);
            Assert.Equal(new[] { (1, true, (string?)"Says hello first") }, await VersionsAsync());

            await Save().Handle(new SaveGraphCommand(created.DefinitionId, Graph("second")), CancellationToken.None);
            Assert.Equal(new[] { (2, false, (string?)null), (1, true, "Says hello first") }, await VersionsAsync()); // the draft's copy is not its own

            var tooLong = new string('x', PublishPipelineRequest.MaxNoteLength + 1);
            await Assert.ThrowsAsync<System.ArgumentException>(() => Publish().Handle(new PublishPipelineCommand(created.DefinitionId, tooLong), CancellationToken.None));
            Assert.Equal(new[] { (2, false, (string?)null), (1, true, "Says hello first") }, await VersionsAsync());

            Assert.True((await Publish().Handle(new PublishPipelineCommand(created.DefinitionId), CancellationToken.None))!.Succeeded);
            Assert.Equal(new[] { (2, true, (string?)null), (1, false, "Says hello first") }, await VersionsAsync());
        }

        // n8n's workflow name, edited in the editor: a rename and a description go into the draft - a new one over the
        // published version, which keeps its name until the draft is published - saved by whoever renamed it, the body as it was;
        // a draft is renamed in place, and a name the pipeline cannot have is refused.
        [Fact]
        public async Task A_pipeline_is_renamed_in_its_draft_and_the_published_version_keeps_its_name_until_published()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var created = await new CreatePipelineCommandHandler(publisher).Handle(new CreatePipelineCommand("Old name", "Old words"), CancellationToken.None);
            await new SaveGraphCommandHandler(publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new QuietNotifier(), new User("ada", 7))
                .Handle(new SaveGraphCommand(created.DefinitionId, Graph("first")), CancellationToken.None);
            Assert.True((await publisher.PublishAsync(created.DefinitionId)).Succeeded);
            UpdatePipelineCommandHandler Update(string who) => new(publisher, services.GetRequiredService<IStepSerializer>(), new QuietNotifier(), new User(who, 9));

            var renamed = await Update("grace").Handle(new UpdatePipelineCommand(created.DefinitionId, "  New name ", " What it does ", null), CancellationToken.None);
            Assert.Equal(("New name", "What it does", 2, false), (renamed!.Name, renamed.Description, renamed.Version, renamed.IsPublished));
            var versions = await new ListVersionsQueryHandler(definitions).Handle(new ListVersionsQuery(created.DefinitionId), CancellationToken.None);
            Assert.Equal(new[] { (2, false, "grace"), (1, true, "ada") }, versions.Select(version => (version.Version, version.IsPublished, version.SavedBy)));
            var published = await definitions.FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = created.DefinitionId, VersionOptions = VersionOptions.Published }, CancellationToken.None);
            Assert.Equal(("Old name", "Old words"), (published!.Name, published.Description));
            Assert.Equal("first", (await new GetGraphQueryHandler(definitions, new Guard(author: true)).Handle(new GetGraphQuery(created.DefinitionId), CancellationToken.None))!.Steps.Single().Inputs["Text"]);

            // The draft renamed again: in place, no new version.
            var again = await Update("grace").Handle(new UpdatePipelineCommand(created.DefinitionId, "Newer name", null, null), CancellationToken.None);
            Assert.Equal(("Newer name", (string?)null, 2), (again!.Name, again.Description, again.Version));

            await Assert.ThrowsAsync<System.ArgumentException>(() => Update("grace").Handle(new UpdatePipelineCommand(created.DefinitionId, "  ", null, null), CancellationToken.None));
        }

        // A version's pinned test values (n8n's pinned data) reach an author; a viewer's graph of the same version, and
        // of the latest, holds none - and the pipeline's detail body none either.
        [Fact]
        public async Task Pinned_test_values_reach_an_author_and_no_one_else()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var created = await new CreatePipelineCommandHandler(publisher).Handle(new CreatePipelineCommand("Pinned", null), CancellationToken.None);
            var graph = Graph("hello");
            graph.Variables.Add(new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world", TestValue = "Ada" });
            await new SaveGraphCommandHandler(publisher, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(),
                services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>(), new QuietNotifier(), new User("ada", 7))
                .Handle(new SaveGraphCommand(created.DefinitionId, graph), CancellationToken.None);
            var version = (await new ListVersionsQueryHandler(definitions).Handle(new ListVersionsQuery(created.DefinitionId), CancellationToken.None)).First().Version;

            Assert.Equal("Ada", (await new GetGraphQueryHandler(definitions, new Guard(author: true)).Handle(new GetGraphQuery(created.DefinitionId), CancellationToken.None))!.Variables.Single().TestValue);
            Assert.Equal("Ada", (await new GetVersionGraphQueryHandler(definitions, new Guard(author: true)).Handle(new GetVersionGraphQuery(created.DefinitionId, version), CancellationToken.None))!.Variables.Single().TestValue);

            var viewers = await new GetGraphQueryHandler(definitions, new Guard(author: false)).Handle(new GetGraphQuery(created.DefinitionId), CancellationToken.None);
            Assert.Null(viewers!.Variables.Single().TestValue);
            Assert.Equal("world", viewers.Variables.Single().Value);
            Assert.Null((await new GetVersionGraphQueryHandler(definitions, new Guard(author: false)).Handle(new GetVersionGraphQuery(created.DefinitionId, version), CancellationToken.None))!.Variables.Single().TestValue);

            var keyValues = services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>();
            var tenants = services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>();
            Assert.Contains("Ada", (await new GetPipelineQueryHandler(definitions, new Guard(author: true), keyValues, tenants).Handle(new GetPipelineQuery(created.DefinitionId), CancellationToken.None))!.Body);
            var detail = await new GetPipelineQueryHandler(definitions, new Guard(author: false), keyValues, tenants).Handle(new GetPipelineQuery(created.DefinitionId), CancellationToken.None);
            Assert.DoesNotContain("Ada", detail!.Body);
            Assert.Contains("world", detail.Body);

            // The pipeline opened says its folder - where what the editor makes from it goes (a failure handler).
            Assert.Equal("", detail.Folder);
            await PipelineFolderStore.SaveAsync(keyValues, tenants.TenantId, created.DefinitionId, "Finance/Invoices", CancellationToken.None);
            Assert.Equal("Finance/Invoices", (await new GetPipelineQueryHandler(definitions, new Guard(author: false), keyValues, tenants).Handle(new GetPipelineQuery(created.DefinitionId), CancellationToken.None))!.Folder);
        }

        private sealed class Guard(bool author) : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == global::Stomme.Pipelines.PipelinesPermissions.Manage ? author : true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        [Fact]
        public void The_difference_between_two_graphs_is_told_in_words_and_positions_are_left_out()
        {
            var from = Graph("first");
            from.Steps.Add(new PipelineGraphStep { Id = "old", Type = "Stomme.Delay", Name = "Wait" });
            from.Connections.Add(new PipelineGraphConnection { SourceId = "say", TargetId = "old" });

            var to = Graph("second");
            to.Steps[0].Position = new GraphPosition { X = 500, Y = 500 };
            to.Steps.Add(new PipelineGraphStep { Id = "new", Type = "Stomme.LogMessage", Name = "Log it", Inputs = { ["Message"] = "hi" } });
            to.Variables.Add(new PipelineGraphVariable { Id = "v", Name = "who", TypeName = "String" });
            to.Steps[0].InputExpressions["Text"] = "{{ who }}";
            to.Steps[0].Inputs.Remove("Text");

            Assert.Equal(new[]
            {
                "Removed the connection from step 'Say' to step 'Wait'",
                "Added step 'Log it'",
                "Removed step 'Wait'",
                "step 'Say', the expression of input 'Text': (none) → '{{ who }}'",
                "step 'Say', input 'Text': 'first' → (none)",
                "Added variable 'who'",
            }, PipelineGraphDiff.Lines(from, to));
            Assert.Empty(PipelineGraphDiff.Lines(from, from));

            // A binding names the variable, not its id (found driving: the line read 'var-24681c15cdec').
            var unbound = Graph("x");
            unbound.Variables.Add(new PipelineGraphVariable { Id = "var-1", Name = "who", TypeName = "String" });
            var bound = Graph("x");
            bound.Variables.Add(new PipelineGraphVariable { Id = "var-1", Name = "who", TypeName = "String" });
            bound.Steps[0].InputVariables["Text"] = "var-1";
            Assert.Equal(new[] { "step 'Say', the variable input 'Text' reads: (none) → 'who'" }, PipelineGraphDiff.Lines(unbound, bound));
        }

        // Each change says what it is about, for the canvas to mark while a version is compared - the step (a connection's:
        // the step it leaves, with the outcome it leaves by and the step it goes to; a variable's: none) - and they are counted.
        [Fact]
        public void Each_change_says_what_it_is_about_and_the_changes_are_counted()
        {
            var from = Graph("first");
            from.Steps.Add(new PipelineGraphStep { Id = "old", Type = "Stomme.Delay", Name = "Wait" });
            from.Connections.Add(new PipelineGraphConnection { SourceId = "say", TargetId = "old" });

            var to = Graph("second");
            to.Steps.Add(new PipelineGraphStep { Id = "a/b", Type = "Stomme.LogMessage", Name = "Log it" }); // an id its path escapes
            to.Connections.Add(new PipelineGraphConnection { SourceId = "say", SourcePort = "Done", TargetId = "a/b" });
            to.Variables.Add(new PipelineGraphVariable { Id = "v", Name = "who", TypeName = "String" });

            var changes = PipelineGraphDiff.Changes(from, to);
            Assert.Equal(new (string, string, string?, string?, string?)[]
            {
                ("Removed the connection from step 'Say' to step 'Wait'", "removed", "say", "", "old"),
                ("Added the connection from step 'Say' (Done) to step 'Log it'", "added", "say", "Done", "a/b"),
                ("Added step 'Log it'", "added", "a/b", null, null),
                ("Removed step 'Wait'", "removed", "old", null, null),
                ("step 'Say', input 'Text': 'first' → 'second'", "changed", "say", null, null),
                ("Added variable 'who'", "added", null, null, null),
            }, changes.Select(change => (change.Line, change.Kind, change.StepId, change.SourcePort, change.TargetId)));
            Assert.Equal(PipelineGraphDiff.Lines(from, to), changes.Select(change => change.Line));

            Assert.Equal("1 new step · 1 changed step · 1 removed step · 1 new connection · 1 removed connection · 1 other change", PipelineGraphDiff.Summary(changes));
            Assert.Equal("no difference", PipelineGraphDiff.Summary(PipelineGraphDiff.Changes(to, to)));
        }

        // Found driving the compare on the canvas: a switch read "(none) → '1'" and a retry "'3/5'" - each is said in words.
        [Fact]
        public void A_switch_a_retry_and_keeping_runs_are_said_in_words()
        {
            var from = Graph("x");
            var to = Graph("x");
            to.Steps[0].Disabled = true;
            to.Steps[0].Retries = 3;
            to.Steps[0].RetryDelaySeconds = 5;
            to.KeepSuccessfulRuns = false;
            Assert.Equal(new[]
            {
                "whether the pipeline keeps the runs that finish well: yes → no",
                "step 'Say', whether it is switched off: no → yes",
                "step 'Say', how often and how far apart it is tried again: not again → 3 times, 5 s apart",
            }, PipelineGraphDiff.Lines(from, to));

            from.Steps[0].Retries = 1;
            Assert.Contains("step 'Say', how often and how far apart it is tried again: once → 3 times, 5 s apart", PipelineGraphDiff.Lines(from, to));
        }
    }
}
