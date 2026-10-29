#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Erasure;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Audit;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The site's pipeline activity (n8n Enterprise's audit log): every command of the module is recorded once it
    /// succeeds, or is said not to be and why; what each did is said in a sentence that never holds a value; the ledger is read
    /// newest first, all of it or one pipeline's, aged out no sooner than its floor, and erased with the subject after its
    /// retention.
    /// </summary>
    public sealed class PipelineAuditTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc);
        private readonly InMemoryAudit _audit = new();
        private readonly SiteState _site = new() { Alias = new Alias { SiteId = 1, TenantId = 1 } };
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            // The mediator the host composes, the module's handlers it needs here, and the ledger as the startup wires it.
            services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
            services.AddScoped<IRequestHandler<ArchivePipelineCommand, bool>, ArchivePipelineCommandHandler>();
            services.AddScoped<IRequestHandler<DeletePipelineCommand, bool>, DeletePipelineCommandHandler>();
            services.AddSingleton<Stomme.Modules.Pipelines.Comments.IPipelineStepCommentRepository>(new NoStepComments()); // the delete clears a discussion
            services.AddSingleton<IUnitOfWork>(new NoUnitOfWork());
            services.AddScoped<IRequestHandler<SetSiteVariablesCommand, Dictionary<string, string>>, SetSiteVariablesCommandHandler>();
            services.AddScoped<IRequestHandler<UpdatePipelineCommand, PipelineView?>, RefusingUpdate>();
            services.AddSingleton<IPipelineAuditRepository>(_audit);
            services.AddSingleton<ICurrentUser>(new Someone());
            services.AddSingleton<ITenantContext>(new OneTenant());
            services.AddSingleton(_site);
            services.AddSingleton<TimeProvider>(new FixedTime());
            services.AddScoped<PipelineAuditRecorder>();
            PipelineAuditRegistration.AddBehaviors(services);
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        // The gate: a command of the module in neither map fails the build, so a new change cannot skip the ledger by being
        // forgotten - validated both ways with a planted command.
        [Fact]
        public void Every_command_of_the_module_is_recorded_or_said_why_not()
        {
            static List<string> Uncovered(IEnumerable<Type> commands, IReadOnlyDictionary<Type, string> recorded, IReadOnlyDictionary<Type, string> notRecorded) =>
                commands.Where(command => recorded.ContainsKey(command) == notRecorded.ContainsKey(command)).Select(command => command.Name).ToList();

            var commands = typeof(PipelineAudit).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && (type.Namespace ?? "").StartsWith("Stomme.Modules.Pipelines", StringComparison.Ordinal)
                    && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>)))
                .ToList();
            Assert.True(commands.Count >= 36, $"expected the module's commands, found {commands.Count}");
            var uncovered = Uncovered(commands, PipelineAudit.Actions, PipelineAudit.NotRecorded);
            Assert.True(uncovered.Count == 0, "Pipelines commands neither recorded in the activity ledger nor said why not: " + string.Join(", ", uncovered));
            Assert.All(PipelineAudit.NotRecorded.Values, why => Assert.False(string.IsNullOrWhiteSpace(why)));

            // Both ways: a planted command is caught, and one in both maps is too.
            Assert.Equal(new[] { nameof(PlantedCommand) }, Uncovered(commands.Append(typeof(PlantedCommand)), PipelineAudit.Actions, PipelineAudit.NotRecorded));
            var both = PipelineAudit.NotRecorded.ToDictionary(pair => pair.Key, pair => pair.Value);
            both[typeof(ArchivePipelineCommand)] = "planted";
            Assert.Equal(new[] { nameof(ArchivePipelineCommand) }, Uncovered(commands, PipelineAudit.Actions, both));

            // Each recorded command closes the behaviour, as the startup registers it.
            Assert.All(PipelineAudit.Actions.Keys, command =>
                typeof(PipelineAuditBehavior<,>).MakeGenericType(command, PipelineAuditRegistration.ResponseOf(command)));
        }

        [Fact]
        public void What_each_command_did_is_said_and_what_did_nothing_is_not()
        {
            var before = new PipelineAuditBefore("def-1", "Orders");
            Assert.Equal(new PipelineAuditFact("published", "def-1", Detail: "what changed: Reads time in Stockholm"),
                PipelineAudit.Describe(new PublishPipelineCommand("def-1", " Reads time in Stockholm "), new PublishResult { Succeeded = true }, before));
            Assert.Null(PipelineAudit.Describe(new PublishPipelineCommand("def-1"), new PublishResult { Succeeded = false }, before)); // refused: nothing published
            Assert.Null(PipelineAudit.Describe(new ArchivePipelineCommand("nope"), false, PipelineAuditBefore.Nothing)); // no such pipeline
            Assert.Null(PipelineAudit.Describe(new StartPipelineCommand("def-1", null, null), null, before)); // nothing started
            Assert.Null(PipelineAudit.Describe(new SaveGraphMergedCommand("def-1", new PipelineGraph(), new PipelineGraph()), new GraphSaveResult { Status = "Rejected" }, before));
            Assert.Equal("merged with a collaborator's changes",
                PipelineAudit.Describe(new SaveGraphMergedCommand("def-1", new PipelineGraph(), new PipelineGraph()), new GraphSaveResult { Status = "Merged" }, before)!.Detail);
            Assert.Null(PipelineAudit.Describe(new TestStepCommand(new PipelineGraph(), "a", null), new StepTestResult(), before)); // not recorded

            Assert.Equal(new PipelineAuditFact("started a run", "def-1", "run-9", "correlation id order-7"),
                PipelineAudit.Describe(new StartPipelineCommand("def-1", "order-7", new Dictionary<string, string> { ["card"] = "4111" }), new PipelineRunStarted { RunId = "run-9" }, before));
            Assert.Equal(new PipelineAuditFact("deleted a run", "def-1", "run-3"), PipelineAudit.Describe(new DeleteRunCommand("run-3"), true, before));
            Assert.Equal("to Finance/Invoices", PipelineAudit.Describe(new SetPipelineFolderCommand("def-1", "Finance/Invoices"), "Finance/Invoices", before)!.Detail);
            Assert.Equal("to the top of the list", PipelineAudit.Describe(new SetPipelineFolderCommand("def-1", ""), "", before)!.Detail);
            Assert.Equal(new PipelineAuditFact("removed a folder", Detail: "'Old', what was in it moved up - 2 pipelines"),
                PipelineAudit.Describe(new MovePipelineFolderCommand("Old", null, true), new PipelineFolderMoveResult { Folder = "", Moved = 2 }, before));
            Assert.Equal("renamed from 'Orders'", PipelineAudit.Describe(new UpdatePipelineCommand("def-1", "Sales orders", null, null), new PipelineView(), before)!.Detail);
            Assert.Equal(new PipelineAuditFact("created the pipeline", "def-2", PipelineName: "New one"),
                PipelineAudit.Describe(new CreatePipelineCommand("New one", null), new PipelineView { DefinitionId = "def-2", Name = "New one" }, PipelineAuditBefore.Nothing));
            Assert.Equal("rejected a run's question", PipelineAudit.Describe(new DecideApprovalCommand("b-1", false, "no"), new PipelineApprovalDecided { RunId = "run-4" }, PipelineAuditBefore.Nothing)!.Action);
            Assert.Equal("now: nightly, finance", PipelineAudit.Describe(new SetPipelineTagsCommand("def-1", new[] { "nightly", "finance" }), (IReadOnlyList<string>)new[] { "nightly", "finance" }, before)!.Detail);

            // The site's variables: the names added, changed and removed - never a value; saved as they were, nothing to say.
            var was = new PipelineAuditBefore(null, null, new Dictionary<string, string> { ["region"] = "eu", ["token"] = "s3cret", ["same"] = "1" });
            var now = new Dictionary<string, string> { ["region"] = "us", ["base"] = "https://api.example", ["same"] = "1" };
            var said = PipelineAudit.Describe(new SetSiteVariablesCommand(now), now, was)!;
            Assert.Equal(new PipelineAuditFact("changed the site's variables", Detail: "added base; changed region; removed token"), said);
            Assert.Null(PipelineAudit.Describe(new SetSiteVariablesCommand(now), now, new PipelineAuditBefore(null, null, now)));
        }

        [Fact]
        public async Task A_command_is_recorded_through_the_mediator_once_it_succeeds_with_who_and_which_pipeline()
        {
            var orders = await MakeAsync("Orders");
            var gone = await MakeAsync("Gone");

            Assert.True(await SendAsync(new ArchivePipelineCommand(orders)));
            Assert.True(await SendAsync(new DeletePipelineCommand(gone)));
            Assert.False(await SendAsync(new ArchivePipelineCommand("no-such-pipeline"))); // nothing archived, nothing recorded
            await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(new UpdatePipelineCommand(orders, "Renamed", null, null))); // refused: nothing recorded

            Assert.Equal(new[] { ("archived", orders, "Orders"), ("deleted for good", gone, "Gone") }, // its name read BEFORE it was deleted
                _audit.Entries.Select(entry => (entry.Action, entry.DefinitionId, entry.PipelineName!)).ToArray());
            Assert.All(_audit.Entries, entry => Assert.Equal((1, 7, "ada", Now, TenantId.Default), (entry.SiteId, entry.UserId, entry.UserName!, entry.OccurredOnUtc, entry.TenantId)));
        }

        [Fact]
        public async Task The_sites_variables_are_recorded_by_name_and_never_by_value()
        {
            using (var scope = _services.CreateScope())
            {
                var tenant = scope.ServiceProvider.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>().TenantId;
                await PipelineSiteVariableStore.SaveAsync(scope.ServiceProvider.GetRequiredService<IKeyValueStore>(), tenant,
                    new Dictionary<string, string> { ["region"] = "eu", ["token"] = "s3cret" }, CancellationToken.None);
            }

            await SendAsync(new SetSiteVariablesCommand(new Dictionary<string, string> { ["region"] = "us", ["base"] = "https://api.example" }));

            var entry = Assert.Single(_audit.Entries);
            Assert.Equal(("changed the site's variables", "", "added base; changed region; removed token"), (entry.Action, entry.DefinitionId, entry.Detail!));
            Assert.DoesNotContain(new[] { "s3cret", "eu", "us", "https" }, value => entry.Detail!.Contains(value, StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_change_the_ledger_cannot_record_still_happens_and_says_so()
        {
            var orders = await MakeAsync("Orders");
            _audit.Failing = true; // the ledger's store is down: the archive happened, so it is not failed
            Assert.True(await SendAsync(new ArchivePipelineCommand(orders)));
            Assert.Empty(_audit.Entries);

            _audit.Failing = false;
            _site.Alias = null; // no site resolved: nothing to record against, and nothing thrown
            var other = await MakeAsync("Other");
            Assert.True(await SendAsync(new ArchivePipelineCommand(other)));
            Assert.Empty(_audit.Entries);
        }

        [Fact]
        public async Task The_ledger_is_read_newest_first_all_of_it_or_one_pipelines()
        {
            var audit = new InMemoryAudit();
            PipelineAuditEntryAggregate Line(int site, string pipeline, int minutes, string action) =>
                PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, site, 7, "ada", action, pipeline, pipeline.ToUpperInvariant(), null, null, Now.AddMinutes(minutes));
            foreach (var line in new[] { Line(1, "p1", 1, "saved"), Line(1, "p2", 2, "archived"), Line(1, "p1", 3, "published"), Line(2, "p1", 4, "theirs"), Line(1, "p1", 5, "started a run") })
            {
                await audit.AddAsync(line);
            }

            var handler = new ListPipelineAuditQueryHandler(audit, _site);
            Assert.Equal(new[] { "started a run", "published", "archived", "saved" }, (await handler.Handle(new ListPipelineAuditQuery(null), CancellationToken.None)).Select(entry => entry.Action));
            Assert.Equal(new[] { "started a run", "published" }, (await handler.Handle(new ListPipelineAuditQuery("p1", 2), CancellationToken.None)).Select(entry => entry.Action));
            Assert.Single(await handler.Handle(new ListPipelineAuditQuery(null, 0), CancellationToken.None)); // at least one
            Assert.Equal("P1", (await handler.Handle(new ListPipelineAuditQuery("p1"), CancellationToken.None))[0].PipelineName);
            _site.Alias = null;
            Assert.Empty(await handler.Handle(new ListPipelineAuditQuery(null), CancellationToken.None)); // no site, nothing read
        }

        [Fact]
        public async Task The_ledger_is_aged_out_no_sooner_than_its_floor_and_erased_with_its_subject_after_retention()
        {
            var audit = new InMemoryAudit();
            var old = PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 7, "ada", "published", "p1", null, null, null, DateTime.UtcNow.AddDays(-400));
            var recent = PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 7, "ada", "saved", "p1", null, null, null, DateTime.UtcNow.AddDays(-10));
            var someoneElse = PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 8, "bo", "saved", "p1", null, null, null, DateTime.UtcNow.AddDays(-10));
            foreach (var entry in new[] { old, recent, someoneElse })
            {
                await audit.AddAsync(entry);
            }

            // A setting of 0 is read as the floor: the record cannot be emptied through a text box.
            Assert.Equal(PipelineAuditRetentionPurger.MinimumRetentionDays, PipelineAuditRetentionPurger.ClampRetention(0));
            Assert.Equal(1, await new PipelineAuditRetentionPurger(audit).PurgeAsync(1, 0));
            Assert.Equal(new[] { recent.Id, someoneElse.Id }, audit.Entries.Select(entry => entry.Id));

            // Erasure: retained for a year as a security record, counted, then purged - the subject's lines only.
            var erasure = new PipelineAuditErasureParticipant(audit, new NoUnitOfWork(), new FixedTime());
            var outcome = Assert.Single(await erasure.EraseAsync(new ErasureSubject(1, 7, null)));
            Assert.Equal(("PipelineAuditEntryAggregate", ErasureHandling.Retain, 1, RetentionReason.Security), (outcome.Store, outcome.Handling, outcome.Affected, outcome.Reason));
            Assert.Equal(1, await erasure.PurgeRetainedAsync(new ErasureSubject(1, 7, null)));
            Assert.Equal(new[] { someoneElse.Id }, audit.Entries.Select(entry => entry.Id));
        }

        [Fact]
        public void A_line_is_cut_never_refused_and_must_say_who_where_and_when()
        {
            var line = PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 0, null, "  published  ", null, new string('n', 400), null, new string('d', 2000), Now);
            Assert.Equal(("published", "", PipelineAuditEntryAggregate.MaximumPipelineNameLength, PipelineAuditEntryAggregate.MaximumDetailLength, 0),
                (line.Action, line.DefinitionId, line.PipelineName!.Length, line.Detail!.Length, line.UserId));
            Assert.Throws<ArgumentException>(() => PipelineAuditEntryAggregate.Record(Guid.Empty, TenantId.Default, 1, 7, "ada", "x", null, null, null, null, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 0, 7, "ada", "x", null, null, null, null, Now));
            Assert.Throws<ArgumentException>(() => PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 7, "ada", " ", null, null, null, null, Now));
            Assert.Throws<ArgumentException>(() => PipelineAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 1, 7, "ada", "x", null, null, null, null, DateTime.Now));
        }

        private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
        }

        private async Task<string> MakeAsync(string name)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Inputs = { ["Text"] = "hello" } } } };
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(
                PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())));
            definition.Name = name;
            await publisher.SaveDraftAsync(definition);
            return definition.DefinitionId;
        }

        private sealed record PlantedCommand(string DefinitionId) : ICommand<bool>;

        // Public: the mediator binds a handler dynamically, and the binder does not see a private type (measured: RuntimeBinderException).
        public sealed class RefusingUpdate : IRequestHandler<UpdatePipelineCommand, PipelineView?>
        {
            public Task<PipelineView?> Handle(UpdatePipelineCommand request, CancellationToken ct) => throw new ArgumentException("A name cannot be empty.");
        }

        private sealed class InMemoryAudit : IPipelineAuditRepository
        {
            public List<PipelineAuditEntryAggregate> Entries { get; } = new();

            public bool Failing { get; set; }

            public Task AddAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default)
            {
                if (Failing)
                {
                    throw new InvalidOperationException("The ledger's store is down.");
                }

                Entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task DeleteAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default)
            {
                Entries.Remove(entry);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) => Where(entry => entry.SiteId == siteId);

            public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default) =>
                Where(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoffUtc);

            public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
                Where(entry => entry.SiteId == siteId && entry.UserId == userId);

            private Task<IReadOnlyList<PipelineAuditEntryAggregate>> Where(Expression<Func<PipelineAuditEntryAggregate, bool>> predicate) =>
                Task.FromResult<IReadOnlyList<PipelineAuditEntryAggregate>>(Entries.AsQueryable().Where(predicate).ToList());
        }

        private sealed class Someone : ICurrentUser
        {
            public int? UserId => 7;
            public string? UserName => "ada";
            public bool IsAuthenticated => true;
            public IReadOnlySet<string> Roles => new HashSet<string>();
            public int SiteId => 1;
        }

        private sealed class OneTenant : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        private sealed class FixedTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(Now);
        }

        private sealed class NoUnitOfWork : IUnitOfWork
        {
            public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);

            public void Dispose()
            {
            }
        }
    }
}
