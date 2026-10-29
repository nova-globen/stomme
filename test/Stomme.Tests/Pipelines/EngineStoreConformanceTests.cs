#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Features;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Tenants.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.KeyValues.Stores;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Enums;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Stores;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Stores;
using Stomme.Engine.Pipelines.State;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Backup;
using Stomme.Infrastructure.Backup;
using Stomme.Modules.Pipelines.Persistence;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Modules.Pipelines.Persistence.EfCore;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (a), D111: the engine's store contracts on Stomme persistence, held to the engine's own
    /// MEMORY stores as the oracle. Every scenario runs the same writes and reads against the store under test and
    /// a fresh memory store and compares what comes back - ids, counts, order where an order was asked for, and the
    /// text the engine's serializers produce for the non-scalar members. One test per contract keeps the per-test
    /// scratch database count low; each test pushes its own engine tenant, so the tenant scoping is exercised by
    /// every scenario, not one. The external providers FAIL rather than skip when unreachable.
    /// </summary>
    public abstract class EngineStoreConformanceTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        protected abstract string ExpectedStore { get; }

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            services.AddSingleton<ResultSink>();
            services.AddSingleton<FixtureTenants>();
            var kind = services.AddEnginePersistence();
            services.AddEngine(module =>
            {
                module.UseStommeStores(kind);
                module.AddStep<SinkStep>();
                module.AddStep<Stomme.Modules.Pipelines.Steps.Schedule>();
                // The Tenants feature, as the host composes it: without it a dispatched run's tenant is not restored on
                // the worker (measured 2026-09-24). The tenants here are the ones the tests register, one per test.
                module.UseTenants();
                module.Configure<MultitenancyFeature>(multitenancy => multitenancy.UseTenantsProvider<FixtureTenantsProvider>());
            });
            _services = services.BuildServiceProvider();
            // Every hosted service, as the host would start them: the module store's schema initializer, and the
            // engine's own (tenant activation, the background dispatcher's workers, the recurring tasks that sweep
            // the bookmark queue through THESE stores).
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            foreach (var hosted in _services.GetServices<IHostedService>().Reverse())
            {
                try
                {
                    await hosted.StopAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // a worker that was never scheduled has nothing to stop; the store below is what must be torn down
                }
            }

            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        // ---- the harness

        private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

        private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

        private static string NewId() => Guid.NewGuid().ToString("N");

        // Registers the tenant with the engine's tenant service (so a dispatch can restore it on the worker). The PUSH is a
        // separate, synchronous call made in the test's own frame: an ambient value set inside an awaited method never
        // reaches its caller (measured - an async push left every test under the default tenant).
        private async Task<Tenant> RegisterTenantAsync(string id)
        {
            var tenant = new Tenant { Id = id, Name = id };
            _services.GetRequiredService<FixtureTenants>().Add(tenant);
            await _services.GetRequiredService<ITenantService>().RefreshAsync();
            return tenant;
        }

        private IDisposable PushTenant(Tenant tenant) => _services.GetRequiredService<ITenantAccessor>().PushContext(tenant);

        /// <summary>The engine tenants this fixture knows: whatever the tests registered.</summary>
        public sealed class FixtureTenants
        {
            private readonly List<Tenant> _tenants = [];

            public void Add(Tenant tenant)
            {
                lock (_tenants)
                {
                    if (_tenants.All(existing => existing.Id != tenant.Id))
                    {
                        _tenants.Add(tenant);
                    }
                }
            }

            public IReadOnlyList<Tenant> Snapshot()
            {
                lock (_tenants)
                {
                    return _tenants.ToList();
                }
            }
        }

        public sealed class FixtureTenantsProvider(FixtureTenants tenants) : ITenantsProvider
        {
            public Task<IEnumerable<Tenant>> ListAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IEnumerable<Tenant>>(tenants.Snapshot());

            public Task<Tenant?> FindAsync(TenantFilter filter, CancellationToken cancellationToken = default) =>
                Task.FromResult(filter.Apply(tenants.Snapshot().AsQueryable()).FirstOrDefault());
        }

        private IServiceScope Scope() => _services.CreateScope();

        private IPayloadSerializer Payloads => _services.GetRequiredService<IPayloadSerializer>();

        private static IReadOnlyList<string> Ids<T>(IEnumerable<T> entities) where T : Entity => entities.Select(e => e.Id).ToList();

        private static void SameSet(IEnumerable<string> sut, IEnumerable<string> oracle, string because)
        {
            Assert.True(sut.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(oracle.OrderBy(x => x, StringComparer.Ordinal)),
                $"{because}: the store answered [{string.Join(",", sut)}], the memory oracle [{string.Join(",", oracle)}]");
        }

        private static void SameSequence(IEnumerable<string> sut, IEnumerable<string> oracle, string because)
        {
            Assert.True(sut.SequenceEqual(oracle, StringComparer.Ordinal),
                $"{because}: the store answered [{string.Join(",", sut)}] in that order, the memory oracle [{string.Join(",", oracle)}]");
        }

        private static void SamePage<T>(Page<T> sut, Page<T> oracle, string because, bool ordered) where T : Entity
        {
            Assert.True(sut.TotalCount == oracle.TotalCount, $"{because}: total {sut.TotalCount} vs the oracle's {oracle.TotalCount}");
            if (ordered)
            {
                SameSequence(Ids(sut.Items), Ids(oracle.Items), because);
            }
            else
            {
                SameSet(Ids(sut.Items), Ids(oracle.Items), because);
            }
        }

        private static IReadOnlyList<string> SortedIds<T>(IEnumerable<T> entities) where T : Entity =>
            entities.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---- the data

        public sealed record BookmarkPayload(string Value, int Number);

        private static PipelineDefinition Definition(string definitionId, int version, bool latest, bool published, string name,
            string description = "A pipeline", bool usableAsStep = false, bool system = false, bool readOnly = false, int createdMinute = 0) => new()
        {
            Id = NewId(),
            DefinitionId = definitionId,
            Name = name,
            Description = description,
            Version = version,
            IsLatest = latest,
            IsPublished = published,
            CreatedAt = At(createdMinute),
            ToolVersion = new Version(3, 7, 1),
            MaterializerName = "Json",
            ProviderName = "Test",
            StringData = "{\"root\":{}}",
            BinaryData = [1, 2, 3],
            IsSystem = system,
            IsReadonly = readOnly,
            Options = new PipelineOptions
            {
                UsableAsStep = usableAsStep,
                StepCategory = "Test",
                AutoUpdateConsumingPipelines = true,
                ActivationStrategyType = typeof(PipelineDefinition),
            },
            Variables = [new Variable("greeting", "hello")],
            Inputs = [new InputDefinition { Name = "input1", Type = typeof(string), DisplayName = "Input 1" }],
            Outputs = [new OutputDefinition { Name = "output1", Type = typeof(int) }],
            Outcomes = ["Done", "Failed"],
            CustomProperties = new Dictionary<string, object> { ["owner"] = "tests", ["weight"] = 3 },
        };

        private static PipelineInstance Instance(string definitionId, string versionId, PipelineStatus status, PipelineSubStatus subStatus,
            string? name, string? correlationId, int createdMinute, int updatedMinute, int? finishedMinute = null, int incidents = 0,
            bool executing = false, bool system = false, string? parentId = null, int version = 1)
        {
            var id = NewId();
            return new PipelineInstance
            {
                Id = id,
                DefinitionId = definitionId,
                DefinitionVersionId = versionId,
                Version = version,
                Status = status,
                SubStatus = subStatus,
                Name = name,
                CorrelationId = correlationId,
                CreatedAt = At(createdMinute),
                UpdatedAt = At(updatedMinute),
                FinishedAt = finishedMinute is null ? null : At(finishedMinute.Value),
                IncidentCount = incidents,
                IsExecuting = executing,
                IsSystem = system,
                ParentPipelineInstanceId = parentId,
                PipelineState = new PipelineState
                {
                    Id = id,
                    DefinitionId = definitionId,
                    DefinitionVersionId = versionId,
                    DefinitionVersion = version,
                    Status = status,
                    SubStatus = subStatus,
                    ExecutionLogSequence = 7,
                    Properties = new Dictionary<string, object> { ["k"] = "v" },
                    Input = new Dictionary<string, object> { ["x"] = 42 },
                },
            };
        }

        // ---- the scenarios, one per contract

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            using var scope = Scope();
            var store = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>();
            var name = store.GetType().Name;
            if (store is EfPipelineDefinitionStore)
            {
                await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<EngineDbContext>>().CreateDbContextAsync();
                name = db.Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Definitions_round_trip_version_filter_search_page_delete_and_count_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>();
            var oracle = new MemoryPipelineDefinitionStore(new MemoryStore<PipelineDefinition>());

            var alphaId = "alpha-" + NewId();
            var alpha1 = Definition(alphaId, 1, latest: false, published: true, "Alpha Report", createdMinute: 1);
            var alpha2 = Definition(alphaId, 2, latest: false, published: false, "Alpha Report", createdMinute: 2);
            var alpha3 = Definition(alphaId, 3, latest: true, published: false, "Alpha Report", createdMinute: 3, usableAsStep: true);
            var beta = Definition("beta-" + NewId(), 1, latest: true, published: true, "Beta Nightly", description: "Runs at night", createdMinute: 4, system: true);
            var gamma = Definition("gamma-" + NewId(), 1, latest: true, published: true, "Gamma", createdMinute: 5, readOnly: true);
            var all = new[] { alpha1, alpha2, alpha3, beta, gamma };
            await sut.SaveManyAsync(all);
            await oracle.SaveManyAsync(all);

            // Round trip of every member, including the ones that are text in the store.
            var read = await sut.FindAsync(new PipelineDefinitionFilter { Id = alpha3.Id });
            Assert.NotNull(read);
            Assert.Equal((alpha3.DefinitionId, alpha3.Name, alpha3.Description, 3, true, false, "Json", "Test"),
                (read.DefinitionId, read.Name, read.Description, read.Version, read.IsLatest, read.IsPublished, read.MaterializerName, read.ProviderName));
            Assert.Equal(new Version(3, 7, 1), read.ToolVersion);
            Assert.Equal(alpha3.CreatedAt, read.CreatedAt);
            Assert.Equal(alpha3.StringData, read.StringData);
            Assert.Equal(new byte[] { 1, 2, 3 }, read.BinaryData);
            Assert.Equal((true, "Test", true, typeof(PipelineDefinition)),
                (read.Options.UsableAsStep, read.Options.StepCategory, read.Options.AutoUpdateConsumingPipelines, read.Options.ActivationStrategyType));
            Assert.Equal("greeting", Assert.Single(read.Variables).Name);
            Assert.Equal("hello", Assert.Single(read.Variables).Value?.ToString());
            Assert.Equal(("input1", typeof(string), "Input 1"), (read.Inputs.Single().Name, read.Inputs.Single().Type, read.Inputs.Single().DisplayName));
            Assert.Equal(("output1", typeof(int)), (read.Outputs.Single().Name, read.Outputs.Single().Type));
            Assert.Equal(new[] { "Done", "Failed" }, read.Outcomes);
            Assert.Equal("tests", read.CustomProperties["owner"]?.ToString());
            Assert.Equal("3", read.CustomProperties["weight"]?.ToString());
            Assert.False(string.IsNullOrEmpty(read.TenantId), "a saved definition carries the engine tenant it was saved under");

            // Version options, each against the oracle.
            foreach (var version in new[] { VersionOptions.Latest, VersionOptions.Published, VersionOptions.Draft, VersionOptions.LatestOrPublished, VersionOptions.LatestAndPublished, VersionOptions.SpecificVersion(2), VersionOptions.All })
            {
                var filter = new PipelineDefinitionFilter { DefinitionId = alphaId, VersionOptions = version };
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), $"VersionOptions {version}");
            }

            var last = await sut.FindLastVersionAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }, CancellationToken.None);
            var lastOracle = await oracle.FindLastVersionAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }, CancellationToken.None);
            Assert.Equal(lastOracle!.Id, last!.Id);

            // The scalar filters.
            var filters = new Dictionary<string, PipelineDefinitionFilter>
            {
                ["Ids"] = new() { Ids = [alpha1.Id, gamma.Id] },
                ["DefinitionIds"] = new() { DefinitionIds = [alphaId, beta.DefinitionId] },
                ["Name"] = new() { Name = "Beta Nightly" },
                ["Names"] = new() { Names = ["Alpha Report", "Gamma"] },
                ["MaterializerName"] = new() { MaterializerName = "Json" },
                ["UsableAsStep"] = new() { UsableAsStep = true },
                ["IsSystem"] = new() { IsSystem = true },
                ["IsReadonly"] = new() { IsReadonly = false },
                ["DefinitionHandle by version id"] = new() { DefinitionHandle = PipelineDefinitionHandle.ByDefinitionVersionId(alpha2.Id) },
                ["DefinitionHandle by definition id, latest"] = new() { DefinitionHandle = PipelineDefinitionHandle.ByDefinitionId(alphaId, VersionOptions.Latest) },
                ["SearchTerm on the name, lower case"] = new() { SearchTerm = "alpha" },
                ["SearchTerm inside a word"] = new() { SearchTerm = "lph" },
                ["SearchTerm on the description"] = new() { SearchTerm = "night" },
                ["SearchTerm matching nothing"] = new() { SearchTerm = "zeta" },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
                Assert.Equal(await oracle.AnyAsync(filter), await sut.AnyAsync(filter));
            }

            // Ordered paging, and summaries with the same order. The oracle here is the memory store's ORDERED, UNPAGED
            // answer sliced by hand: its own ordered-and-paged overloads drop the order before slicing (measured:
            // MemoryPipelineDefinitionStore.FindManyAsync(filter, order, pageArgs) paginates the unordered set), which is
            // a quirk this store does not reproduce - a page of an ordered list is that list's slice.
            // Ordered by a UNIQUE key: three versions share a name, and a tie has no defined order on any store.
            var byNewest = new PipelineDefinitionOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending);
            var byCreated = new PipelineDefinitionOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending);
            var page = PageArgs.FromRange(1, 2);
            var pageByCreated = await sut.FindManyAsync(new PipelineDefinitionFilter(), byCreated, page);
            var orderedOracle = (await oracle.FindManyAsync(new PipelineDefinitionFilter(), byCreated)).ToList();
            Assert.Equal(orderedOracle.Count, pageByCreated.TotalCount);
            SameSequence(Ids(pageByCreated.Items), Ids(orderedOracle.Skip(1).Take(2)), "page 2 of 2 by CreatedAt");
            var summaries = await sut.FindSummariesAsync(new PipelineDefinitionFilter(), byNewest, page);
            var summariesOracle = (await oracle.FindSummariesAsync(new PipelineDefinitionFilter(), byNewest)).Skip(1).Take(2).ToList();
            Assert.Equal(orderedOracle.Count, summaries.TotalCount);
            Assert.Equal(summariesOracle.Select(s => s.Id), summaries.Items.Select(s => s.Id));
            Assert.Equal(summariesOracle.Select(s => (s.Name, s.Version, s.IsLatest, s.IsPublished)), summaries.Items.Select(s => (s.Name, s.Version, s.IsLatest, s.IsPublished)));
            SameSequence((await sut.FindManyAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }, byCreated)).Select(x => x.Id), (await oracle.FindManyAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }, byCreated)).Select(x => x.Id), "ordered, unpaged");
            var first = await sut.FindAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }, new PipelineDefinitionOrder<int>(x => x.Version, OrderDirection.Descending));
            Assert.Equal(alpha3.Id, first!.Id);

            // Name uniqueness and the distinct count.
            Assert.False(await sut.GetIsNameUnique("Alpha Report"));
            Assert.True(await sut.GetIsNameUnique("Alpha Report", alphaId));
            Assert.False(await sut.GetIsNameUnique("Gamma", alphaId));
            Assert.True(await sut.GetIsNameUnique("Delta"));
            Assert.Equal(await oracle.CountDistinctAsync(), await sut.CountDistinctAsync());

            // Delete counts DEFINITIONS and removes every version.
            Assert.Equal(await oracle.DeleteAsync(new PipelineDefinitionFilter { Id = alpha2.Id }), await sut.DeleteAsync(new PipelineDefinitionFilter { Id = alpha2.Id }));
            SameSet(Ids(await sut.FindManyAsync(new PipelineDefinitionFilter())), Ids(await oracle.FindManyAsync(new PipelineDefinitionFilter())), "after deleting one version of alpha (every version goes)");
            Assert.False(await sut.AnyAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }));
            Assert.Equal(0, await sut.DeleteAsync(new PipelineDefinitionFilter { DefinitionId = alphaId }));
            Assert.Equal(await oracle.CountDistinctAsync(), await sut.CountDistinctAsync());

            // Save is an upsert: a second save of the same id changes the row, not the count.
            beta.Name = "Beta Renamed";
            await sut.SaveAsync(beta);
            await oracle.SaveAsync(beta);
            Assert.Equal("Beta Renamed", (await sut.FindAsync(new PipelineDefinitionFilter { Id = beta.Id }))!.Name);
            Assert.Equal(await oracle.CountDistinctAsync(), await sut.CountDistinctAsync());
        }

        [Fact]
        public async Task Instances_round_trip_filter_by_status_time_and_text_page_count_and_delete_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>();
            var oracle = new MemoryPipelineInstanceStore(new MemoryStore<PipelineInstance>());

            var definitionId = "def-" + NewId();
            var v1 = "v1-" + NewId();
            var v2 = "v2-" + NewId();
            var running = Instance(definitionId, v1, PipelineStatus.Running, PipelineSubStatus.Executing, "Nightly Report", "corr-1", 1, 2, executing: true);
            var suspended = Instance(definitionId, v1, PipelineStatus.Running, PipelineSubStatus.Suspended, "Weekly Report", "corr-2", 3, 4, incidents: 1);
            var finished = Instance(definitionId, v2, PipelineStatus.Finished, PipelineSubStatus.Finished, "Nightly Cleanup", "corr-3", 5, 6, finishedMinute: 7, version: 2, parentId: running.Id);
            // Every instance carries a name and a correlation id: the memory store's text filters dereference both without a
            // null check (an upstream quirk), and a row it throws on is a row it cannot be the oracle for.
            var faulted = Instance(definitionId, v2, PipelineStatus.Finished, PipelineSubStatus.Faulted, "Faulted Run", "corr-4", 1440, 1441, finishedMinute: 1442, incidents: 2, system: true, version: 2);
            var all = new[] { running, suspended, finished, faulted };
            foreach (var instance in all)
            {
                await sut.AddAsync(instance);
                await oracle.AddAsync(instance);
            }

            // A second Add of the same id is refused, as the memory store refuses it.
            var duplicate = Instance(definitionId, v1, PipelineStatus.Running, PipelineSubStatus.Executing, "dup", null, 1, 1);
            duplicate.Id = running.Id;
            await Assert.ThrowsAnyAsync<Exception>(async () => await sut.AddAsync(duplicate));
            await Assert.ThrowsAnyAsync<Exception>(async () => await oracle.AddAsync(duplicate));

            var read = await sut.FindAsync(new PipelineInstanceFilter { Id = finished.Id });
            Assert.NotNull(read);
            Assert.Equal((definitionId, v2, 2, PipelineStatus.Finished, PipelineSubStatus.Finished, "Nightly Cleanup", "corr-3", running.Id),
                (read.DefinitionId, read.DefinitionVersionId, read.Version, read.Status, read.SubStatus, read.Name, read.CorrelationId, read.ParentPipelineInstanceId));
            Assert.Equal((At(5), At(6), (DateTimeOffset?)At(7)), (read.CreatedAt, read.UpdatedAt, read.FinishedAt));
            Assert.Equal((finished.Id, definitionId, 7L, PipelineStatus.Finished), (read.PipelineState.Id, read.PipelineState.DefinitionId, read.PipelineState.ExecutionLogSequence, read.PipelineState.Status));
            Assert.Equal("v", read.PipelineState.Properties["k"]?.ToString());
            Assert.Equal("42", read.PipelineState.Input["x"]?.ToString());

            var filters = new Dictionary<string, PipelineInstanceFilter>
            {
                ["Ids"] = new() { Ids = [running.Id, faulted.Id] },
                ["DefinitionId"] = new() { DefinitionId = definitionId },
                ["DefinitionVersionId"] = new() { DefinitionVersionId = v2 },
                ["DefinitionVersionIds"] = new() { DefinitionVersionIds = [v1] },
                ["Version"] = new() { Version = 2 },
                ["ParentPipelineInstanceIds"] = new() { ParentPipelineInstanceIds = [running.Id] },
                ["CorrelationId"] = new() { CorrelationId = "corr-2" },
                ["CorrelationIds"] = new() { CorrelationIds = ["corr-1", "corr-3"] },
                ["Names"] = new() { Names = ["Nightly Report", "Nightly Cleanup"] },
                ["PipelineStatus"] = new() { PipelineStatus = PipelineStatus.Finished },
                ["PipelineStatuses"] = new() { PipelineStatuses = [PipelineStatus.Running] },
                ["PipelineSubStatus"] = new() { PipelineSubStatus = PipelineSubStatus.Suspended },
                ["PipelineSubStatuses"] = new() { PipelineSubStatuses = [PipelineSubStatus.Faulted, PipelineSubStatus.Executing] },
                ["IsExecuting"] = new() { IsExecuting = true },
                ["HasIncidents true"] = new() { HasIncidents = true },
                ["HasIncidents false"] = new() { HasIncidents = false },
                ["IsSystem"] = new() { IsSystem = false },
                ["Name contains, any case"] = new() { Name = "nightly" },
                ["SearchTerm on the correlation id"] = new() { SearchTerm = "corr-3" },
                ["BeforeLastUpdated"] = new() { BeforeLastUpdated = At(5) },
                ["CreatedAt >= a day (zero time)"] = new() { TimestampFilters = [new TimestampFilter { Column = "CreatedAt", Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = new DateTimeOffset(T0.Date.AddDays(1)) }] },
                ["CreatedAt < an instant"] = new() { TimestampFilters = [new TimestampFilter { Column = "CreatedAt", Operator = TimestampFilterOperator.LessThan, Timestamp = At(4) }] },
                // The window n8n's Insights reads - every run since an instant.
                ["CreatedAt >= an instant"] = new() { TimestampFilters = [new TimestampFilter { Column = "CreatedAt", Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = At(4) }] },
                ["UpdatedAt is a day"] = new() { TimestampFilters = [new TimestampFilter { Column = "UpdatedAt", Operator = TimestampFilterOperator.Is, Timestamp = new DateTimeOffset(T0.Date) }] },
                ["FinishedAt is not an instant"] = new() { TimestampFilters = [new TimestampFilter { Column = "FinishedAt", Operator = TimestampFilterOperator.IsNot, Timestamp = At(7) }] },
                ["combined"] = new() { DefinitionId = definitionId, PipelineStatus = PipelineStatus.Finished, HasIncidents = false },
                // The step-data pass's windows (PipelineRunSettingsTask) - every run ended, or ended well, since its mark
                // and before the grace.
                ["ended since a mark, before a cutoff"] = new()
                {
                    DefinitionId = definitionId, PipelineStatus = PipelineStatus.Finished, BeforeLastUpdated = At(9),
                    TimestampFilters = [new TimestampFilter { Column = "UpdatedAt", Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = At(3) }],
                },
                ["ended well since a mark"] = new()
                {
                    PipelineSubStatus = PipelineSubStatus.Finished,
                    TimestampFilters = [new TimestampFilter { Column = "UpdatedAt", Operator = TimestampFilterOperator.GreaterThanOrEqual, Timestamp = At(3) }],
                },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
                Assert.True(await sut.CountAsync(filter) == await oracle.CountAsync(filter), $"{label}: count");
                SameSet(await sut.FindManyIdsAsync(filter), await oracle.FindManyIdsAsync(filter), $"{label}: ids");
            }

            var byCreated = new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Descending);
            var page = PageArgs.FromRange(1, 2);
            SamePage(await sut.FindManyAsync(new PipelineInstanceFilter(), page, byCreated), await oracle.FindManyAsync(new PipelineInstanceFilter(), page, byCreated), "page by CreatedAt desc", ordered: true);
            var ids = await sut.FindManyIdsAsync(new PipelineInstanceFilter(), page, byCreated);
            var idsOracle = await oracle.FindManyIdsAsync(new PipelineInstanceFilter(), page, byCreated);
            Assert.Equal(idsOracle.TotalCount, ids.TotalCount);
            Assert.Equal(idsOracle.Items, ids.Items);
            var summaries = await sut.SummarizeManyAsync(new PipelineInstanceFilter(), page, byCreated);
            var summariesOracle = await oracle.SummarizeManyAsync(new PipelineInstanceFilter(), page, byCreated);
            Assert.Equal(summariesOracle.TotalCount, summaries.TotalCount);
            Assert.Equal(summariesOracle.Items.Select(s => (s.Id, s.Status, s.SubStatus, s.Name, s.IncidentCount, s.FinishedAt)), summaries.Items.Select(s => (s.Id, s.Status, s.SubStatus, s.Name, s.IncidentCount, s.FinishedAt)));
            SameSequence((await sut.SummarizeManyAsync(new PipelineInstanceFilter(), byCreated)).Select(s => s.Id), (await oracle.SummarizeManyAsync(new PipelineInstanceFilter(), byCreated)).Select(s => s.Id), "summaries ordered, unpaged");
            // The step-data pass pages the runs that ended by when they were last updated, oldest first.
            var byUpdated = new PipelineInstanceOrder<DateTimeOffset?>(x => x.UpdatedAt, OrderDirection.Ascending);
            var ended = new PipelineInstanceFilter { PipelineStatus = PipelineStatus.Finished };
            var endedPage = await sut.SummarizeManyAsync(ended, PageArgs.FromRange(0, 2), byUpdated);
            var endedPageOracle = await oracle.SummarizeManyAsync(ended, PageArgs.FromRange(0, 2), byUpdated);
            Assert.Equal(endedPageOracle.TotalCount, endedPage.TotalCount);
            Assert.Equal(endedPageOracle.Items.Select(s => (s.Id, s.UpdatedAt)), endedPage.Items.Select(s => (s.Id, s.UpdatedAt)));

            // Update and the timestamp touch.
            suspended.Status = PipelineStatus.Finished;
            suspended.SubStatus = PipelineSubStatus.Cancelled;
            suspended.PipelineState.Status = PipelineStatus.Finished;
            await sut.UpdateAsync(suspended);
            await oracle.UpdateAsync(suspended);
            Assert.Equal(PipelineSubStatus.Cancelled, (await sut.FindAsync(new PipelineInstanceFilter { Id = suspended.Id }))!.SubStatus);
            await sut.UpdateUpdatedTimestampAsync(running.Id, At(99));
            Assert.Equal(At(99), (await sut.FindAsync(new PipelineInstanceFilter { Id = running.Id }))!.UpdatedAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateUpdatedTimestampAsync("missing-" + NewId(), At(1)));

            // Save many is an upsert; delete counts rows.
            await sut.SaveManyAsync([running, finished]);
            await oracle.SaveManyAsync([running, finished]);
            Assert.Equal(await oracle.CountAsync(new PipelineInstanceFilter()), await sut.CountAsync(new PipelineInstanceFilter()));
            var deleteFilter = new PipelineInstanceFilter { PipelineStatuses = [PipelineStatus.Finished] };
            Assert.Equal(await oracle.DeleteAsync(deleteFilter), await sut.DeleteAsync(deleteFilter));
            SameSet(Ids(await sut.FindManyAsync(new PipelineInstanceFilter())), Ids(await oracle.FindManyAsync(new PipelineInstanceFilter())), "after the delete");
        }

        [Fact]
        public async Task Bookmarks_round_trip_their_payload_and_are_found_by_hash_name_and_instance_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IBookmarkStore>();
            var oracle = new MemoryBookmarkStore(new MemoryStore<StoredBookmark>());

            var instanceA = "inst-" + NewId();
            var instanceB = "inst-" + NewId();
            StoredBookmark Bookmark(string name, string hash, string instance, string? correlation, string? stepInstance, int minute) => new()
            {
                Id = NewId(),
                Name = name,
                Hash = hash,
                PipelineInstanceId = instance,
                CorrelationId = correlation,
                StepInstanceId = stepInstance,
                CreatedAt = At(minute),
                Payload = new BookmarkPayload("p-" + name, minute),
                Metadata = new Dictionary<string, string> { ["step"] = name },
            };
            var one = Bookmark("Event", "h1", instanceA, "c1", "s1", 1);
            var two = Bookmark("Timer", "h2", instanceA, "c1", "s2", 2);
            var three = Bookmark("Event", "h3", instanceB, "c2", "s3", 3);
            await sut.SaveAsync(one);
            await oracle.SaveAsync(one);
            await sut.SaveManyAsync([two, three], CancellationToken.None);
            await oracle.SaveManyAsync([two, three], CancellationToken.None);

            var read = await sut.FindAsync(new BookmarkFilter { BookmarkId = two.Id });
            Assert.NotNull(read);
            Assert.Equal(("Timer", "h2", instanceA, "c1", "s2", At(2)), (read.Name, read.Hash, read.PipelineInstanceId, read.CorrelationId, read.StepInstanceId, read.CreatedAt));
            Assert.Equal(Payloads.Serialize(two.Payload!), Payloads.Serialize(read.Payload!));
            Assert.Equal("Timer", read.Metadata!["step"]);

            var filters = new Dictionary<string, BookmarkFilter>
            {
                ["BookmarkIds"] = new() { BookmarkIds = [one.Id, three.Id] },
                ["Hash"] = new() { Hash = "h3" },
                ["Hashes"] = new() { Hashes = ["h1", "h2"] },
                ["PipelineInstanceId"] = new() { PipelineInstanceId = instanceA },
                ["PipelineInstanceIds"] = new() { PipelineInstanceIds = [instanceB] },
                ["CorrelationId"] = new() { CorrelationId = "c1" },
                ["Name"] = new() { Name = "Event" },
                ["Names"] = BookmarkFilter.ByStepTypeNames(["Timer"]),
                ["StepInstanceId"] = new() { StepInstanceId = "s3" },
                ["Name and instance"] = new() { Name = "Event", PipelineInstanceId = instanceA },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
            }

            var deleteFilter = new BookmarkFilter { PipelineInstanceId = instanceA };
            Assert.Equal(await oracle.DeleteAsync(deleteFilter), await sut.DeleteAsync(deleteFilter));
            SameSet(Ids(await sut.FindManyAsync(new BookmarkFilter())), Ids(await oracle.FindManyAsync(new BookmarkFilter())), "after the delete");
        }

        [Fact]
        public async Task The_bookmark_queue_adds_saves_pages_in_order_and_expires_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IBookmarkQueueStore>();
            var oracle = new MemoryBookmarkQueueStore(new MemoryStore<BookmarkQueueItem>());

            BookmarkQueueItem Item(string? bookmarkId, string? hash, string? stepType, int minute) => new()
            {
                Id = NewId(),
                PipelineInstanceId = "inst-" + minute,
                CorrelationId = "c",
                BookmarkId = bookmarkId,
                StimulusHash = hash,
                StepInstanceId = "si-" + minute,
                StepTypeName = stepType,
                CreatedAt = At(minute),
                Options = new ResumeBookmarkOptions { Input = new Dictionary<string, object> { ["i"] = minute } },
            };
            var a = Item("b1", "h1", "Event", 3);
            var b = Item("b2", "h2", "Timer", 1);
            var c = Item(null, "h1", "Event", 2);
            await sut.AddAsync(a);
            await oracle.AddAsync(a);
            await sut.SaveAsync(b);
            await oracle.SaveAsync(b);
            await sut.AddAsync(c);
            await oracle.AddAsync(c);

            var read = await sut.FindAsync(new BookmarkQueueFilter { Id = a.Id });
            Assert.NotNull(read);
            Assert.Equal(("b1", "h1", "Event", "inst-3", "si-3", At(3)), (read.BookmarkId, read.StimulusHash, read.StepTypeName, read.PipelineInstanceId, read.StepInstanceId, read.CreatedAt));
            Assert.Equal("3", read.Options!.Input!["i"].ToString());

            var filters = new Dictionary<string, BookmarkQueueFilter>
            {
                ["Ids"] = new() { Ids = [a.Id, c.Id] },
                ["BookmarkId"] = new() { BookmarkId = "b2" },
                ["BookmarkHash"] = new() { BookmarkHash = "h1" },
                ["StepInstanceId"] = new() { StepInstanceId = "si-2" },
                ["StepTypeName"] = new() { StepTypeName = "Event" },
                ["PipelineInstanceId"] = new() { PipelineInstanceId = "inst-1" },
                ["CreatedAtLessThan"] = new() { CreatedAtLessThan = At(3) },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
            }

            var byCreated = new BookmarkQueueItemOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending);
            SamePage(await sut.PageAsync(PageArgs.FromRange(0, 2), byCreated), await oracle.PageAsync(PageArgs.FromRange(0, 2), byCreated), "first two by CreatedAt", ordered: true);
            SamePage(await sut.PageAsync(PageArgs.FromRange(0, 5), new BookmarkQueueFilter { StepTypeName = "Event" }, byCreated), await oracle.PageAsync(PageArgs.FromRange(0, 5), new BookmarkQueueFilter { StepTypeName = "Event" }, byCreated), "filtered page", ordered: true);

            var expire = new BookmarkQueueFilter { CreatedAtLessThan = At(3) };
            Assert.Equal(await oracle.DeleteAsync(expire), await sut.DeleteAsync(expire));
            SameSet(Ids(await sut.FindManyAsync(new BookmarkQueueFilter())), Ids(await oracle.FindManyAsync(new BookmarkQueueFilter())), "after expiring");
        }

        [Fact]
        public async Task Triggers_are_saved_found_paged_replaced_and_deleted_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<ITriggerStore>();
            var oracle = new MemoryTriggerStore(new MemoryStore<StoredTrigger>());

            var definition = "def-" + NewId();
            StoredTrigger Trigger(string name, string? hash, string version, string step) => new()
            {
                Id = NewId(),
                PipelineDefinitionId = definition,
                PipelineDefinitionVersionId = version,
                Name = name,
                StepId = step,
                Hash = hash,
                Payload = new BookmarkPayload(name, 1),
            };
            var cron = Trigger("Cron", "h-cron", "v1", "step-1");
            var http = Trigger("Http", "h-http", "v1", "step-2");
            var bus = Trigger("Bus", null, "v2", "step-3");
            await sut.SaveManyAsync([cron, http, bus]);
            await oracle.SaveManyAsync([cron, http, bus]);

            var read = await sut.FindAsync(new TriggerFilter { Id = http.Id });
            Assert.NotNull(read);
            Assert.Equal((definition, "v1", "Http", "step-2", "h-http"), (read.PipelineDefinitionId, read.PipelineDefinitionVersionId, read.Name, read.StepId, read.Hash));
            Assert.Equal(Payloads.Serialize(http.Payload!), Payloads.Serialize(read.Payload!));

            var filters = new Dictionary<string, TriggerFilter>
            {
                ["Ids"] = new() { Ids = [cron.Id, bus.Id] },
                ["PipelineDefinitionId"] = new() { PipelineDefinitionId = definition },
                ["PipelineDefinitionIds"] = new() { PipelineDefinitionIds = ["other"] },
                ["PipelineDefinitionVersionId"] = new() { PipelineDefinitionVersionId = "v2" },
                ["PipelineDefinitionVersionIds"] = new() { PipelineDefinitionVersionIds = ["v1"] },
                ["Name"] = new() { Name = "Cron" },
                ["Names"] = TriggerFilter.ByNames(["Http", "Bus"]),
                ["Hash"] = new() { Hash = "h-cron" },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
            }

            SamePage(await sut.FindManyAsync(new TriggerFilter(), PageArgs.FromRange(1, 2)), await oracle.FindManyAsync(new TriggerFilter(), PageArgs.FromRange(1, 2)), "default page order (Id ascending)", ordered: true);
            var byName = new StoredTriggerOrder<string>(x => x.Name!, OrderDirection.Descending);
            SamePage(await sut.FindManyAsync(new TriggerFilter(), PageArgs.FromRange(0, 2), byName), await oracle.FindManyAsync(new TriggerFilter(), PageArgs.FromRange(0, 2), byName), "page by Name desc", ordered: true);

            var replacement = Trigger("Cron", "h-cron-2", "v3", "step-1");
            await sut.ReplaceAsync([cron, http], [replacement]);
            await oracle.ReplaceAsync([cron, http], [replacement]);
            SameSet(Ids(await sut.FindManyAsync(new TriggerFilter())), Ids(await oracle.FindManyAsync(new TriggerFilter())), "after the replace");

            var deleteFilter = new TriggerFilter { PipelineDefinitionVersionIds = ["v2", "v3"] };
            Assert.Equal(await oracle.DeleteManyAsync(deleteFilter), await sut.DeleteManyAsync(deleteFilter));
            SameSet(Ids(await sut.FindManyAsync(new TriggerFilter())), Ids(await oracle.FindManyAsync(new TriggerFilter())), "after the delete");
        }

        // A run kept without its steps' data: a record emptied by the pipeline's setting (PipelineRunSettingsTask.Empty)
        // and saved reads back with no inputs, outputs or payload, marked - its status, times and error kept - on every store.
        [Fact]
        public async Task A_step_record_emptied_of_its_data_saves_and_reads_back_empty_and_marked()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            var record = new StepExecutionRecord
            {
                Id = NewId(),
                PipelineInstanceId = "inst-" + NewId(),
                StepId = "s1",
                StepNodeId = "node-s1",
                StepType = "Stomme.WriteLine",
                StepTypeVersion = 1,
                StepName = "Say",
                StartedAt = At(1),
                CompletedAt = At(2),
                Status = StepStatus.Faulted,
                AggregateFaultCount = 1,
                StepState = new Dictionary<string, object?> { ["Text"] = "card 4111" },
                Payload = new Dictionary<string, object> { ["p"] = "x" },
                Outputs = new Dictionary<string, object?> { ["Result"] = "card 4111" },
                Metadata = new Dictionary<string, object> { ["m"] = "kept" },
                Exception = ExceptionState.FromException(new InvalidOperationException("boom")),
            };
            using (var scope = Scope())
            {
                await scope.ServiceProvider.GetRequiredService<IStepExecutionStore>().SaveAsync(record);
            }

            using (var scope = Scope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IStepExecutionStore>();
                var read = (await store.FindAsync(new StepExecutionRecordFilter { Id = record.Id }))!;
                Assert.True(Stomme.Modules.Pipelines.Triggers.PipelineRunSettingsTask.Empty(read));
                Assert.False(Stomme.Modules.Pipelines.Triggers.PipelineRunSettingsTask.Empty(read)); // nothing left to empty
                await store.SaveAsync(read);
            }

            using (var scope = Scope())
            {
                var read = (await scope.ServiceProvider.GetRequiredService<IStepExecutionStore>().FindAsync(new StepExecutionRecordFilter { Id = record.Id }))!;
                Assert.Null(read.StepState);
                Assert.Null(read.Outputs);
                Assert.Null(read.Payload);
                Assert.True(read.Metadata!.ContainsKey(Stomme.Models.Pipelines.PipelineStepDataKeeping.NotKeptKey));
                Assert.Equal("kept", read.Metadata["m"].ToString());
                Assert.Equal((StepStatus.Faulted, "boom", (DateTimeOffset?)At(2)), (read.Status, read.Exception!.Message, read.CompletedAt));
            }
        }

        // A run emptied of what it was started with and returned saves as read - its UpdatedAt, which the emptying pass
        // pages by, unmoved - and reads back empty and marked on every provider.
        [Fact]
        public async Task A_run_emptied_of_its_input_saves_and_reads_back_empty_and_marked_with_its_times_unmoved()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            var run = Instance("def-" + NewId(), "ver-" + NewId(), PipelineStatus.Finished, PipelineSubStatus.Faulted, "Payments", null, 1, 2, finishedMinute: 2);
            run.PipelineState.Properties[Stomme.Modules.Pipelines.Application.KeepRunInput.PropertyKey] = new Dictionary<string, object> { ["card"] = "4111" };
            run.PipelineState.Output = new Dictionary<string, object> { ["Result"] = "4111" };
            using (var scope = Scope())
            {
                await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().SaveAsync(run);
            }

            using (var scope = Scope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>();
                var read = (await store.FindAsync(new PipelineInstanceFilter { Id = run.Id }))!;
                Assert.True(Stomme.Modules.Pipelines.Application.KeepRunInput.Empty(read.PipelineState));
                Assert.False(Stomme.Modules.Pipelines.Application.KeepRunInput.Empty(read.PipelineState)); // nothing left to empty
                await store.SaveAsync(read);
            }

            using (var scope = Scope())
            {
                var read = (await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = run.Id }))!;
                Assert.Empty(Stomme.Modules.Pipelines.Application.KeepRunInput.Of(read.PipelineState));
                Assert.Empty(read.PipelineState.Output);
                Assert.True(Stomme.Modules.Pipelines.Application.KeepRunInput.WasEmptied(read.PipelineState));
                Assert.Equal("v", read.PipelineState.Properties["k"].ToString());
                Assert.Equal((At(2), (DateTimeOffset?)At(2), PipelineSubStatus.Faulted), (read.UpdatedAt, read.FinishedAt, read.SubStatus));
            }
        }

        // One at a time, the rest in turn: a waiting run's mark and its whole input are kept by the store; the turn keeper
        // finds it, takes the OLDEST - its mark gone on the store, so it is the run going - and takes nothing more while it goes.
        [Fact]
        public async Task A_waiting_runs_mark_and_input_are_kept_and_the_turn_keeper_takes_the_oldest_on_the_store()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            var definitionId = "def-" + NewId();
            var older = Instance(definitionId, "ver-" + NewId(), PipelineStatus.Running, PipelineSubStatus.Pending, "Orders", "a", 1, 1);
            var newer = Instance(definitionId, older.DefinitionVersionId, PipelineStatus.Running, PipelineSubStatus.Pending, "Orders", "b", 2, 2);
            foreach (var run in new[] { older, newer })
            {
                run.PipelineState.Properties[Stomme.Modules.Pipelines.Reliability.PipelineTurns.WaitingKey] = "true";
                run.PipelineState.Properties[Stomme.Modules.Pipelines.Reliability.PipelineTurns.TriggerKey] = "hook";
                run.PipelineState.Input[Stomme.Modules.Pipelines.Steps.Webhook.PayloadInputKey] = "{\"order\":\"" + run.CorrelationId + "\"}";
            }

            using (var scope = Scope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>();
                await store.SaveAsync(newer);
                await store.SaveAsync(older);
            }

            using (var scope = Scope())
            {
                var turns = ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(scope.ServiceProvider);
                Assert.Equal(new[] { definitionId }, await turns.WithWaitingAsync(CancellationToken.None));
                Assert.Equal((older.Id, (string?)"hook"), await turns.TakeNextAsync(definitionId, CancellationToken.None));
                Assert.Null(await turns.TakeNextAsync(definitionId, CancellationToken.None)); // the older is going now
            }

            using (var scope = Scope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>();
                var taken = (await store.FindAsync(new PipelineInstanceFilter { Id = older.Id }))!;
                var waiting = (await store.FindAsync(new PipelineInstanceFilter { Id = newer.Id }))!;
                Assert.False(Stomme.Modules.Pipelines.Reliability.PipelineTurns.IsWaiting(taken.PipelineState));
                Assert.False(taken.PipelineState.Properties.ContainsKey(Stomme.Modules.Pipelines.Reliability.PipelineTurns.TriggerKey));
                Assert.Equal((PipelineStatus.Running, PipelineSubStatus.Pending), (taken.Status, taken.SubStatus));
                Assert.True(Stomme.Modules.Pipelines.Reliability.PipelineTurns.IsWaiting(waiting.PipelineState));
                Assert.Equal("{\"order\":\"b\"}", waiting.PipelineState.Input[Stomme.Modules.Pipelines.Steps.Webhook.PayloadInputKey].ToString());
                Assert.Equal("v", taken.PipelineState.Properties["k"].ToString()); // the rest of its properties untouched
            }
        }

        // The task itself, on the provider - its writes reach the store. The memory store hands back the very object it
        // holds, so a change made without a save looks kept there (measured: the task with its run save removed passed the
        // memory-backed trigger test); a store that reads back a copy is the only one that sees a missing save.
        [Fact]
        public async Task The_run_settings_task_empties_a_runs_step_data_and_input_on_the_store()
        {
            var tenant = await RegisterTenantAsync(NewId());
            using var pushed = PushTenant(tenant);
            var definition = Definition("def-" + NewId(), 1, latest: true, published: true, "Private");
            definition.StringData = "{\"root\":{},\"metadata\":{\"designer\":{\"keepStepData\":\"never\"}}}";
            var run = Instance(definition.DefinitionId, definition.Id, PipelineStatus.Finished, PipelineSubStatus.Finished, "Private", null, 1, 2, finishedMinute: 2);
            run.PipelineState.Properties[Stomme.Modules.Pipelines.Application.KeepRunInput.PropertyKey] = new Dictionary<string, object> { ["card"] = "4111" };
            var record = new StepExecutionRecord
            {
                Id = NewId(), PipelineInstanceId = run.Id, StepId = "s1", StepNodeId = "node-s1", StepType = "Stomme.WriteLine", StepTypeVersion = 1,
                StartedAt = At(1), CompletedAt = At(2), Status = StepStatus.Completed,
                StepState = new Dictionary<string, object?> { ["Text"] = "card 4111" }, Outputs = new Dictionary<string, object?> { ["Result"] = "card 4111" },
            };
            using (var scope = Scope())
            {
                await scope.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>().SaveAsync(definition);
                await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().SaveAsync(run);
                await scope.ServiceProvider.GetRequiredService<IStepExecutionStore>().SaveAsync(record);
            }

            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                var task = new Stomme.Modules.Pipelines.Triggers.PipelineRunSettingsTask(
                    services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineInstanceManager>(),
                    services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<IStepExecutionStore>(), new FixedClock(At(10)), services.GetRequiredService<ITenantAccessor>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Stomme.Modules.Pipelines.Triggers.PipelineRunSettingsTask>.Instance);
                await task.ExecuteAsync(CancellationToken.None);
            }

            using (var scope = Scope())
            {
                var read = (await scope.ServiceProvider.GetRequiredService<IStepExecutionStore>().FindAsync(new StepExecutionRecordFilter { Id = record.Id }))!;
                Assert.Null(read.StepState);
                Assert.Null(read.Outputs);
                Assert.True(read.Metadata!.ContainsKey(Stomme.Models.Pipelines.PipelineStepDataKeeping.NotKeptKey));
                var state = (await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = run.Id }))!.PipelineState;
                Assert.Empty(Stomme.Modules.Pipelines.Application.KeepRunInput.Of(state));
                Assert.True(Stomme.Modules.Pipelines.Application.KeepRunInput.WasEmptied(state));
            }
        }

        [Fact]
        public async Task Step_executions_round_trip_state_outputs_and_exception_and_filter_count_summarize_and_delete_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IStepExecutionStore>();
            var oracle = new MemoryStepExecutionStore(new MemoryStore<StepExecutionRecord>());

            var instance = "inst-" + NewId();
            StepExecutionRecord Record(string stepId, string name, StepStatus status, int startMinute, int? completedMinute, string? schedulingExecution = null, int? depth = null, bool faulted = false) => new()
            {
                Id = NewId(),
                PipelineInstanceId = instance,
                StepId = stepId,
                StepNodeId = "node-" + stepId,
                StepType = "Stomme." + name,
                StepTypeVersion = 1,
                StepName = name,
                StartedAt = At(startMinute),
                HasBookmarks = status == StepStatus.Running,
                Status = status,
                AggregateFaultCount = faulted ? 1 : 0,
                CompletedAt = completedMinute is null ? null : At(completedMinute.Value),
                SchedulingStepExecutionId = schedulingExecution,
                SchedulingStepId = schedulingExecution is null ? null : "sched-step",
                SchedulingPipelineInstanceId = schedulingExecution is null ? null : instance,
                CallStackDepth = depth,
                StepState = new Dictionary<string, object?> { ["counter"] = 1, ["label"] = name },
                Payload = new Dictionary<string, object> { ["p"] = "x" },
                Outputs = new Dictionary<string, object?> { ["Result"] = 2.5 },
                Properties = new Dictionary<string, object> { ["pr"] = "y" },
                Metadata = new Dictionary<string, object> { ["m"] = name },
                Exception = faulted ? ExceptionState.FromException(new InvalidOperationException("boom")) : null,
            };
            var start = Record("s1", "Start", StepStatus.Completed, 1, 2);
            var wait = Record("s2", "Wait", StepStatus.Running, 3, null, schedulingExecution: start.Id, depth: 1);
            var fault = Record("s3", "Fault", StepStatus.Faulted, 4, 5, schedulingExecution: start.Id, depth: 1, faulted: true);
            await sut.AddManyAsync([start, wait]);
            await oracle.AddManyAsync([start, wait]);
            await sut.SaveAsync(fault);
            await oracle.SaveAsync(fault);

            var read = await sut.FindAsync(new StepExecutionRecordFilter { Id = fault.Id });
            Assert.NotNull(read);
            Assert.Equal((instance, "s3", "node-s3", "Stomme.Fault", "Fault", StepStatus.Faulted, 1, At(4), (DateTimeOffset?)At(5), start.Id, 1),
                (read.PipelineInstanceId, read.StepId, read.StepNodeId, read.StepType, read.StepName, read.Status, read.AggregateFaultCount, read.StartedAt, read.CompletedAt, read.SchedulingStepExecutionId, read.CallStackDepth));
            Assert.Equal("1", read.StepState!["counter"]?.ToString());
            Assert.Equal("Fault", read.StepState["label"]?.ToString());
            Assert.Equal("x", read.Payload!["p"].ToString());
            Assert.Equal("2.5", read.Outputs!["Result"]?.ToString());
            Assert.Equal("y", read.Properties!["pr"].ToString());
            Assert.Equal("Fault", read.Metadata!["m"].ToString());
            Assert.Equal(("boom", typeof(InvalidOperationException)), (read.Exception!.Message, read.Exception.Type));

            var filters = new Dictionary<string, StepExecutionRecordFilter>
            {
                ["Ids"] = new() { Ids = [start.Id, fault.Id] },
                ["PipelineInstanceId"] = new() { PipelineInstanceId = instance },
                ["PipelineInstanceIds"] = new() { PipelineInstanceIds = ["none"] },
                ["StepId"] = new() { StepId = "s2" },
                ["StepIds"] = new() { StepIds = ["s1", "s3"] },
                ["StepIds empty (ignored)"] = new() { StepIds = [] },
                ["StepNodeId"] = new() { StepNodeId = "node-s1" },
                ["StepNodeIds"] = new() { StepNodeIds = ["node-s2"] },
                ["Name"] = new() { Name = "Wait" },
                ["Names"] = new() { Names = ["Start", "Fault"] },
                ["Status"] = new() { Status = StepStatus.Running },
                ["Statuses"] = new() { Statuses = [StepStatus.Completed, StepStatus.Faulted] },
                ["Completed true"] = new() { Completed = true },
                ["Completed false"] = new() { Completed = false },
                ["SchedulingStepExecutionId"] = new() { SchedulingStepExecutionId = start.Id },
                ["SchedulingStepExecutionIds"] = new() { SchedulingStepExecutionIds = [start.Id] },
                ["SchedulingStepId"] = new() { SchedulingStepId = "sched-step" },
                ["SchedulingStepIds"] = new() { SchedulingStepIds = ["sched-step", "other"] },
                ["SchedulingPipelineInstanceId"] = new() { SchedulingPipelineInstanceId = instance },
                ["SchedulingPipelineInstanceIds"] = new() { SchedulingPipelineInstanceIds = [instance] },
                ["CallStackDepth"] = new() { CallStackDepth = 1 },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                SameSet(Ids(await sut.FindManyAsync(filter)), Ids(await oracle.FindManyAsync(filter)), label);
                Assert.True(await sut.CountAsync(filter) == await oracle.CountAsync(filter), $"{label}: count");
            }

            var byStarted = new StepExecutionRecordOrder<DateTimeOffset>(x => x.StartedAt, OrderDirection.Descending);
            SameSequence(Ids(await sut.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = instance }, byStarted)), Ids(await oracle.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = instance }, byStarted)), "ordered by StartedAt desc");
            var summaries = (await sut.FindManySummariesAsync(new StepExecutionRecordFilter { PipelineInstanceId = instance }, byStarted)).ToList();
            var summariesOracle = (await oracle.FindManySummariesAsync(new StepExecutionRecordFilter { PipelineInstanceId = instance }, byStarted)).ToList();
            Assert.Equal(summariesOracle.Select(s => (s.Id, s.StepName, s.Status, s.HasBookmarks, s.CompletedAt)), summaries.Select(s => (s.Id, s.StepName, s.Status, s.HasBookmarks, s.CompletedAt)));
            Assert.Equal(summariesOracle.Select(s => s.Metadata!["m"].ToString()), summaries.Select(s => s.Metadata!["m"].ToString()));
            SameSet(Ids(await sut.FindManySummariesAsync(new StepExecutionRecordFilter { Statuses = [StepStatus.Running] })), Ids(await oracle.FindManySummariesAsync(new StepExecutionRecordFilter { Statuses = [StepStatus.Running] })), "summaries, filtered");

            wait.Status = StepStatus.Completed;
            wait.CompletedAt = At(6);
            await sut.SaveManyAsync([wait]);
            await oracle.SaveManyAsync([wait]);
            SameSet(Ids(await sut.FindManyAsync(new StepExecutionRecordFilter { Completed = true })), Ids(await oracle.FindManyAsync(new StepExecutionRecordFilter { Completed = true })), "after completing the waiting step");

            var deleteFilter = new StepExecutionRecordFilter { Statuses = [StepStatus.Completed] };
            Assert.Equal(await oracle.DeleteManyAsync(deleteFilter), await sut.DeleteManyAsync(deleteFilter));
            SameSet(Ids(await sut.FindManyAsync(new StepExecutionRecordFilter())), Ids(await oracle.FindManyAsync(new StepExecutionRecordFilter())), "after the delete");
        }

        [Fact]
        public async Task The_execution_log_adds_finds_by_event_and_type_orders_pages_and_deletes_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IPipelineExecutionLogStore>();
            var oracle = new MemoryPipelineExecutionLogStore(new MemoryStore<PipelineExecutionLogRecord>());

            var instance = "inst-" + NewId();
            PipelineExecutionLogRecord Entry(long sequence, string stepId, string stepType, string eventName, string? parent, int minute) => new()
            {
                Id = NewId(),
                PipelineDefinitionId = "def",
                PipelineDefinitionVersionId = "def-v1",
                PipelineInstanceId = instance,
                PipelineVersion = 1,
                StepInstanceId = "si-" + stepId,
                ParentStepInstanceId = parent,
                StepId = stepId,
                StepType = stepType,
                StepTypeVersion = 1,
                StepName = "Step " + stepId,
                StepNodeId = "node-" + stepId,
                Timestamp = At(minute),
                Sequence = sequence,
                EventName = eventName,
                Message = "m" + sequence,
                Source = "tests",
                Payload = new BookmarkPayload(eventName, (int)sequence),
            };
            var started = Entry(1, "flow", "Stomme.Flowchart", "Started", null, 1);
            var executing = Entry(2, "s1", "Stomme.WriteLine", "Executing", "si-flow", 2);
            var completed = Entry(3, "s1", "Stomme.WriteLine", "Completed", "si-flow", 3);
            var faulted = Entry(4, "s2", "Stomme.Fault", "Faulted", "si-flow", 4);
            await sut.AddAsync(started);
            await oracle.AddAsync(started);
            await sut.AddManyAsync([executing, completed, faulted]);
            await oracle.AddManyAsync([executing, completed, faulted]);

            var read = await sut.FindAsync(new PipelineExecutionLogRecordFilter { Id = faulted.Id });
            Assert.NotNull(read);
            Assert.Equal((instance, "s2", "Stomme.Fault", "Faulted", "si-flow", 4L, At(4), "m4"), (read.PipelineInstanceId, read.StepId, read.StepType, read.EventName, read.ParentStepInstanceId, read.Sequence, read.Timestamp, read.Message));
            Assert.Equal(Payloads.Serialize(faulted.Payload!), Payloads.Serialize(read.Payload!));

            var filters = new Dictionary<string, PipelineExecutionLogRecordFilter>
            {
                ["Ids"] = new() { Ids = [started.Id, faulted.Id] },
                ["PipelineInstanceId"] = new() { PipelineInstanceId = instance },
                ["PipelineInstanceIds"] = new() { PipelineInstanceIds = [instance, "other"] },
                ["ParentStepInstanceId"] = new() { ParentStepInstanceId = "si-flow" },
                ["StepId"] = new() { StepId = "s1" },
                ["StepIds"] = new() { StepIds = ["flow", "s2"] },
                ["StepNodeId"] = new() { StepNodeId = "node-s1" },
                ["StepNodeIds"] = new() { StepNodeIds = ["node-flow"] },
                ["EventName"] = new() { EventName = "Completed" },
                ["EventNames"] = new() { EventNames = ["Started", "Faulted"] },
                ["ExcludeStepType"] = new() { ExcludeStepType = "Stomme.WriteLine" },
                ["ExcludeStepTypes"] = new() { ExcludeStepTypes = ["Stomme.Flowchart", "Stomme.Fault"] },
                ["no filter"] = new(),
            };
            foreach (var (label, filter) in filters)
            {
                var page = PageArgs.FromRange(0, 100);
                SamePage(await sut.FindManyAsync(filter, page), await oracle.FindManyAsync(filter, page), label, ordered: false);
            }

            var bySequence = new PipelineExecutionLogRecordOrder<long>(x => x.Sequence, OrderDirection.Descending);
            Assert.Equal((await oracle.FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = instance }, bySequence))!.Id, (await sut.FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = instance }, bySequence))!.Id);
            var byTime = new PipelineExecutionLogRecordOrder<DateTimeOffset>(x => x.Timestamp, OrderDirection.Ascending);
            SamePage(await sut.FindManyAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = instance }, PageArgs.FromRange(1, 2), byTime), await oracle.FindManyAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = instance }, PageArgs.FromRange(1, 2), byTime), "page by Timestamp", ordered: true);

            completed.Message = "amended";
            await sut.SaveAsync(completed);
            await oracle.SaveAsync(completed);
            await sut.SaveManyAsync([executing]);
            await oracle.SaveManyAsync([executing]);
            Assert.Equal("amended", (await sut.FindAsync(new PipelineExecutionLogRecordFilter { Id = completed.Id }))!.Message);

            var deleteFilter = new PipelineExecutionLogRecordFilter { EventNames = ["Executing", "Completed"] };
            Assert.Equal(await oracle.DeleteManyAsync(deleteFilter), await sut.DeleteManyAsync(deleteFilter));
            SamePage(await sut.FindManyAsync(new PipelineExecutionLogRecordFilter(), PageArgs.All), await oracle.FindManyAsync(new PipelineExecutionLogRecordFilter(), PageArgs.All), "after the delete", ordered: false);
        }

        [Fact]
        public async Task Key_values_save_find_by_key_prefix_and_list_and_delete_as_the_memory_store_does()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var sut = scope.ServiceProvider.GetRequiredService<IKeyValueStore>();
            var oracle = new MemoryKeyValueStore(new MemoryStore<SerializedKeyValuePair>());

            var prefix = "kv:" + NewId() + ":";
            var pairs = new[]
            {
                new SerializedKeyValuePair { Key = prefix + "one", SerializedValue = "1" },
                new SerializedKeyValuePair { Key = prefix + "two", SerializedValue = "2" },
                new SerializedKeyValuePair { Key = "other:" + NewId(), SerializedValue = "3" },
            };
            foreach (var pair in pairs)
            {
                await sut.SaveAsync(pair, CancellationToken.None);
                await oracle.SaveAsync(pair, CancellationToken.None);
            }

            Assert.Equal("2", (await sut.FindAsync(new KeyValueFilter { Key = prefix + "two" }, CancellationToken.None))!.SerializedValue);
            foreach (var (label, filter) in new Dictionary<string, KeyValueFilter>
            {
                ["Key"] = new() { Key = prefix + "one" },
                ["Key, StartsWith"] = new() { Key = prefix, StartsWith = true },
                ["Keys"] = new() { Keys = [prefix + "two", pairs[2].Key] },
                ["no filter"] = new(),
            })
            {
                SameSet(Ids(await sut.FindManyAsync(filter, CancellationToken.None)), Ids(await oracle.FindManyAsync(filter, CancellationToken.None)), label);
            }

            pairs[0].SerializedValue = "one";
            await sut.SaveAsync(pairs[0], CancellationToken.None);
            Assert.Equal("one", (await sut.FindAsync(new KeyValueFilter { Key = prefix + "one" }, CancellationToken.None))!.SerializedValue);
            await sut.DeleteAsync(prefix + "one", CancellationToken.None);
            await oracle.DeleteAsync(prefix + "one", CancellationToken.None);
            SameSet(Ids(await sut.FindManyAsync(new KeyValueFilter(), CancellationToken.None)), Ids(await oracle.FindManyAsync(new KeyValueFilter(), CancellationToken.None)), "after the delete");
        }

        // ---- the engine on top of the stores: a definition published, a run made, the run in the stores

        [Fact]
        public async Task A_published_definition_runs_through_the_runtime_client_and_its_run_lands_in_the_stores()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var services = scope.ServiceProvider;

            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = "ran-" + NewId() } } });
            definition.Name = "Run test";
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));

            var client = await services.GetRequiredService<IPipelineRuntime>().CreateClientAsync();
            var response = await client.CreateAndRunInstanceAsync(new CreateAndRunPipelineInstanceRequest
            {
                PipelineDefinitionHandle = PipelineDefinitionHandle.ByDefinitionId(definition.DefinitionId, VersionOptions.Published),
                CorrelationId = "client-run",
            });
            Assert.Equal((PipelineStatus.Finished, PipelineSubStatus.Finished), (response.Status, response.SubStatus));
            // The step ran from the MATERIALISED definition (text in the store, rebuilt by type name and properties).
            Assert.StartsWith("ran-", _services.GetRequiredService<ResultSink>().Value);

            // The run is in the stores, not in memory: the definition (published), the instance (finished), its step
            // execution records and its execution log, all under this tenant.
            var stored = Assert.Single(await services.GetRequiredService<IPipelineDefinitionStore>().FindManyAsync(new PipelineDefinitionFilter { DefinitionId = definition.DefinitionId }));
            Assert.True(stored.IsPublished && stored.IsLatest);
            var instance = await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = response.PipelineInstanceId });
            Assert.NotNull(instance);
            Assert.Equal((PipelineStatus.Finished, definition.DefinitionId, definition.Id, "client-run"), (instance.Status, instance.DefinitionId, instance.DefinitionVersionId, instance.CorrelationId));
            Assert.Equal(PipelineStatus.Finished, instance.PipelineState.Status);
            var steps = (await services.GetRequiredService<IStepExecutionStore>().FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = response.PipelineInstanceId })).ToList();
            Assert.Contains(steps, s => s.StepType.EndsWith("SinkStep", StringComparison.Ordinal) && s.Status == StepStatus.Completed);
            var log = await services.GetRequiredService<IPipelineExecutionLogStore>().FindManyAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = response.PipelineInstanceId }, PageArgs.All);
            Assert.True(log.TotalCount > 0, "the execution log has no records for the run");
            Assert.All(log.Items, record => Assert.Equal(instance.TenantId, record.TenantId));
        }

        // Inside a REGISTERED engine tenant. MEASURED 2026-09-24: the background dispatcher sends the caller's tenant id as
        // a command header, and restoring it on the worker is the Tenants feature's middleware (UseTenants()), which needs
        // a tenant the engine's ITenantService can FIND - without the feature, a run dispatched from inside a tenant landed
        // under the default tenant and was invisible to a poll from the dispatching one. The fixture composes the feature
        // as the host does; the poll below runs inside the dispatching tenant and finds the run there.
        [Fact]
        public async Task A_dispatched_run_is_not_awaited_and_is_observed_by_polling_the_instance_store()
        {
            using var tenant = PushTenant(await RegisterTenantAsync(NewId()));
            using var scope = Scope();
            var services = scope.ServiceProvider;

            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = "dispatched" } } });
            definition.Name = "Dispatch test";
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);

            var correlation = "dispatch-" + NewId();
            var response = await services.GetRequiredService<IPipelineDispatcher>().DispatchAsync(
                new DispatchPipelineDefinitionRequest(definition.Id) { CorrelationId = correlation }, options: null);
            Assert.Null(response.Fault);

            // Dispatched, never awaited - the observer polls the instance store until the run is finished.
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            PipelineInstance? instance = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                instance = await instances.FindAsync(new PipelineInstanceFilter { CorrelationId = correlation });
                if (instance?.Status == PipelineStatus.Finished)
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert.NotNull(instance);
            Assert.Equal(PipelineStatus.Finished, instance.Status);
            Assert.Equal(_services.GetRequiredService<ITenantAccessor>().TenantId, instance.TenantId);
            Assert.Equal("dispatched", _services.GetRequiredService<ResultSink>().Value);
        }

        [Fact]
        public async Task Every_store_scopes_to_the_engine_tenant_and_only_a_tenant_agnostic_filter_crosses_it()
        {
            var tenantA = "tenant-a-" + NewId();
            var tenantB = "tenant-b-" + NewId();
            var definition = Definition("def-" + NewId(), 1, latest: true, published: true, "Scoped");
            var instance = Instance(definition.DefinitionId, definition.Id, PipelineStatus.Running, PipelineSubStatus.Executing, "Scoped", null, 1, 1);
            var bookmark = new StoredBookmark { Id = NewId(), Name = "Event", Hash = "h", PipelineInstanceId = instance.Id, CreatedAt = At(1) };
            var trigger = new StoredTrigger { Id = NewId(), PipelineDefinitionId = definition.DefinitionId, PipelineDefinitionVersionId = definition.Id, Name = "Cron", StepId = "s", Hash = "h" };
            var queued = new BookmarkQueueItem { Id = NewId(), BookmarkId = bookmark.Id, StimulusHash = "h", CreatedAt = At(1) };
            var step = new StepExecutionRecord { Id = NewId(), PipelineInstanceId = instance.Id, StepId = "s", StepNodeId = "n", StepType = "T", StartedAt = At(1), Status = StepStatus.Running };
            var log = new PipelineExecutionLogRecord { Id = NewId(), PipelineDefinitionId = definition.DefinitionId, PipelineDefinitionVersionId = definition.Id, PipelineInstanceId = instance.Id, StepInstanceId = "si", StepId = "s", StepType = "T", StepNodeId = "n", Timestamp = At(1), Sequence = 1 };
            var pair = new SerializedKeyValuePair { Key = "scoped:" + NewId(), SerializedValue = "v" };

            using (PushTenant(await RegisterTenantAsync(tenantA)))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                await services.GetRequiredService<IPipelineDefinitionStore>().SaveAsync(definition);
                await services.GetRequiredService<IPipelineInstanceStore>().AddAsync(instance);
                await services.GetRequiredService<IBookmarkStore>().SaveAsync(bookmark);
                await services.GetRequiredService<ITriggerStore>().SaveAsync(trigger);
                await services.GetRequiredService<IBookmarkQueueStore>().AddAsync(queued);
                await services.GetRequiredService<IStepExecutionStore>().SaveAsync(step);
                await services.GetRequiredService<IPipelineExecutionLogStore>().AddAsync(log);
                await services.GetRequiredService<IKeyValueStore>().SaveAsync(pair, CancellationToken.None);
            }

            Assert.Equal(tenantA, definition.TenantId);
            Assert.Equal(tenantA, instance.TenantId);

            using (PushTenant(await RegisterTenantAsync(tenantB)))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Null(await services.GetRequiredService<IPipelineDefinitionStore>().FindAsync(new PipelineDefinitionFilter { Id = definition.Id }));
                Assert.Null(await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = instance.Id }));
                Assert.Null(await services.GetRequiredService<IBookmarkStore>().FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id }));
                Assert.Null(await services.GetRequiredService<ITriggerStore>().FindAsync(new TriggerFilter { Id = trigger.Id }));
                Assert.Null(await services.GetRequiredService<IBookmarkQueueStore>().FindAsync(new BookmarkQueueFilter { Id = queued.Id }));
                Assert.Null(await services.GetRequiredService<IStepExecutionStore>().FindAsync(new StepExecutionRecordFilter { Id = step.Id }));
                Assert.Null(await services.GetRequiredService<IPipelineExecutionLogStore>().FindAsync(new PipelineExecutionLogRecordFilter { Id = log.Id }));
                Assert.Null(await services.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = pair.Key }, CancellationToken.None));
                Assert.Equal(0, await services.GetRequiredService<IPipelineInstanceStore>().DeleteAsync(new PipelineInstanceFilter { Id = instance.Id }));

                // The agnostic filters see across tenants and report whose the row is.
                Assert.Equal(tenantA, (await services.GetRequiredService<IPipelineDefinitionStore>().FindAsync(new PipelineDefinitionFilter { Id = definition.Id, TenantAgnostic = true }))!.TenantId);
                Assert.Equal(tenantA, (await services.GetRequiredService<IBookmarkStore>().FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id, TenantAgnostic = true }))!.TenantId);
                Assert.Equal(tenantA, (await services.GetRequiredService<ITriggerStore>().FindAsync(new TriggerFilter { Id = trigger.Id, TenantAgnostic = true }))!.TenantId);
                Assert.Equal(tenantA, (await services.GetRequiredService<IBookmarkQueueStore>().FindAsync(new BookmarkQueueFilter { Id = queued.Id, TenantAgnostic = true }))!.TenantId);
            }

            using (PushTenant(await RegisterTenantAsync(tenantA)))
            using (var scope = Scope())
            {
                Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = instance.Id }));
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IStepExecutionStore>().CountAsync(new StepExecutionRecordFilter { PipelineInstanceId = instance.Id }));
            }
        }

        // Found 2026-10-07 reading the schedules task: a key-value KEY is the row's id, one per installation, while the
        // store's reads are per tenant - so two sites with the same cron kept their last fire under one key, each tick
        // moved the row to the site that saved it last, the other found nothing, re-anchored, and NEITHER ever fired.
        [Fact]
        public async Task Two_sites_with_the_same_schedule_each_fire_it()
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 10, 0, 30, TimeSpan.Zero));
            var options = Microsoft.Extensions.Options.Options.Create(new Stomme.Modules.Pipelines.Triggers.PipelineTriggerOptions());
            var sites = new[] { await RegisterTenantAsync("site-a-" + NewId()), await RegisterTenantAsync("site-b-" + NewId()) };
            var pipelines = new Dictionary<string, string>();
            foreach (var site in sites)
            {
                using (PushTenant(site))
                using (var scope = Scope())
                {
                    var trigger = new Stomme.Modules.Pipelines.Steps.Schedule { Id = "trigger", Cron = new("*/5 * * * *") };
                    trigger.SetCanStartPipeline(true);
                    var sink = new SinkStep { Id = "next", Value = "fired" };
                    var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
                    var definition = await publisher.NewAsync(new Stomme.Engine.Pipelines.Steps.Flowchart.Steps.Flowchart
                    {
                        Steps = { trigger, sink },
                        Connections = { new Stomme.Engine.Pipelines.Steps.Flowchart.Models.Connection(new Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint(trigger), new Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint(sink)) },
                    });
                    definition.Name = "Every five minutes";
                    var published = await publisher.PublishAsync(definition);
                    Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
                    pipelines[site.Id] = definition.DefinitionId;
                }
            }

            async Task TickAsync()
            {
                foreach (var site in sites)
                {
                    using (PushTenant(site))
                    using (var scope = Scope())
                    {
                        await ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Triggers.ScheduledPipelinesTask>(scope.ServiceProvider, clock, options)
                            .ExecuteAsync(CancellationToken.None);
                    }
                }
            }

            // First sight anchors both at 10:00:30; at 10:05:10 both are due.
            await TickAsync();
            clock.UtcNow = clock.UtcNow.AddMinutes(4).AddSeconds(40);
            await TickAsync();

            foreach (var site in sites)
            {
                using (PushTenant(site))
                using (var scope = Scope())
                {
                    Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().CountAsync(new PipelineInstanceFilter { DefinitionId = pipelines[site.Id] }));
                }
            }
        }

        // n8n's workflow tags, on each provider: a pipeline's tags are the site's (the same pipeline id tagged on
        // another site is another record - the schedules' lesson above), listed with the pipelines, cleared by an empty list,
        // refused with the reason when one cannot be a tag, absent for a pipeline the site does not have, and gone with it.
        [Fact]
        public async Task A_pipelines_tags_are_kept_per_site_listed_refused_when_not_tags_and_go_with_it()
        {
            var sites = new[] { await RegisterTenantAsync("site-a-" + NewId()), await RegisterTenantAsync("site-b-" + NewId()) };
            var pipelines = new Dictionary<string, string>();
            foreach (var site in sites)
            {
                using (PushTenant(site))
                using (var scope = Scope())
                {
                    var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
                    var definition = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = "tagged" } } });
                    definition.Name = "Tagged on " + site.Id;
                    await publisher.SaveDraftAsync(definition);
                    pipelines[site.Id] = definition.DefinitionId;
                }
            }

            Stomme.Modules.Pipelines.Application.SetPipelineTagsCommandHandler Tagger(IServiceProvider services) => new(
                services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>());
            async Task<List<string>> ListedAsync(IServiceProvider services, string definitionId) =>
                (await new Stomme.Modules.Pipelines.Application.ListPipelinesQueryHandler(
                        services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(),
                        services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>())
                    .Handle(new Stomme.Modules.Pipelines.Application.ListPipelinesQuery(), CancellationToken.None))
                .Single(view => view.DefinitionId == definitionId).Tags;

            using (PushTenant(sites[0]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Equal(new[] { "billing", "nightly" }, await Tagger(services).Handle(new(pipelines[sites[0].Id], new[] { " billing", "nightly", "Billing" }), CancellationToken.None));
                Assert.Equal(new[] { "billing", "nightly" }, await ListedAsync(services, pipelines[sites[0].Id]));
                var refused = await Assert.ThrowsAsync<ArgumentException>(() => Tagger(services).Handle(new(pipelines[sites[0].Id], new[] { "a/b" }), CancellationToken.None));
                Assert.Equal("'a/b' cannot be a tag: at most 30 letters, digits, spaces, '-', '_' or '.'.", refused.Message);
                Assert.Equal(new[] { "billing", "nightly" }, await ListedAsync(services, pipelines[sites[0].Id]));
                // The other site's pipeline is not this site's to tag.
                Assert.Null(await Tagger(services).Handle(new(pipelines[sites[1].Id], new[] { "stolen" }), CancellationToken.None));
            }

            using (PushTenant(sites[1]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Empty(await ListedAsync(services, pipelines[sites[1].Id]));
                await Tagger(services).Handle(new(pipelines[sites[1].Id], new[] { "ops" }), CancellationToken.None);
                Assert.Equal(new[] { "ops" }, await ListedAsync(services, pipelines[sites[1].Id]));
            }

            // Site B's tags took nothing from site A's.
            using (PushTenant(sites[0]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Equal(new[] { "billing", "nightly" }, await ListedAsync(services, pipelines[sites[0].Id]));
                Assert.Empty((await Tagger(services).Handle(new(pipelines[sites[0].Id], Array.Empty<string>()), CancellationToken.None))!);
                Assert.Empty(await ListedAsync(services, pipelines[sites[0].Id]));
                await Tagger(services).Handle(new(pipelines[sites[0].Id], new[] { "again" }), CancellationToken.None);
                // What its Remove records seen before steps have seen goes with it - and only its: found by the key's start on
                // the store, a pipeline whose id begins with its own is untouched.
                var keyValues = services.GetRequiredService<IKeyValueStore>();
                var seenKeys = new[]
                {
                    Stomme.Modules.Pipelines.Steps.RemoveSeenRecords.Key(sites[0].Id, pipelines[sites[0].Id], "step-a"),
                    Stomme.Modules.Pipelines.Steps.RemoveSeenRecords.Key(sites[0].Id, pipelines[sites[0].Id], "*"),
                };
                var neighbour = Stomme.Modules.Pipelines.Steps.RemoveSeenRecords.Key(sites[0].Id, pipelines[sites[0].Id] + "x", "step-a");
                foreach (var key in seenKeys.Append(neighbour))
                {
                    await keyValues.SaveAsync(new Stomme.Engine.KeyValues.Entities.SerializedKeyValuePair { Key = key, SerializedValue = "{\"Seen\":[\"ab\"]}" }, CancellationToken.None);
                }

                Assert.True(await new Stomme.Modules.Pipelines.Application.DeletePipelineCommandHandler(
                        services.GetRequiredService<IPipelineDefinitionManager>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>(),
                        new NoStepComments(), new NoStepComments(), new Stomme.Shared.SiteState())
                    .Handle(new Stomme.Modules.Pipelines.Application.DeletePipelineCommand(pipelines[sites[0].Id]), CancellationToken.None));
                Assert.Null(await services.GetRequiredService<IKeyValueStore>().FindAsync(
                    new KeyValueFilter { Key = Stomme.Modules.Pipelines.Application.PipelineTagStore.Key(sites[0].Id, pipelines[sites[0].Id]) }, CancellationToken.None));
                foreach (var key in seenKeys)
                {
                    Assert.Null(await keyValues.FindAsync(new KeyValueFilter { Key = key }, CancellationToken.None));
                }

                Assert.NotNull(await keyValues.FindAsync(new KeyValueFilter { Key = neighbour }, CancellationToken.None));
            }
        }

        // n8n's folders: a pipeline's folder is kept as its tags are - per site, listed with the pipelines, made tidy,
        // refused with the reason, cleared by the top, absent for a pipeline the site does not have, and gone with it.
        [Fact]
        public async Task A_pipelines_folder_is_kept_per_site_listed_refused_when_not_one_and_goes_with_it()
        {
            var sites = new[] { await RegisterTenantAsync("site-a-" + NewId()), await RegisterTenantAsync("site-b-" + NewId()) };
            var pipelines = new Dictionary<string, string>();
            foreach (var site in sites)
            {
                using (PushTenant(site))
                using (var scope = Scope())
                {
                    var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
                    var definition = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = "filed" } } });
                    definition.Name = "Filed on " + site.Id;
                    await publisher.SaveDraftAsync(definition);
                    pipelines[site.Id] = definition.DefinitionId;
                }
            }

            Stomme.Modules.Pipelines.Application.SetPipelineFolderCommandHandler Mover(IServiceProvider services) => new(
                services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>());
            async Task<string> ListedAsync(IServiceProvider services, string definitionId) =>
                (await new Stomme.Modules.Pipelines.Application.ListPipelinesQueryHandler(
                        services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(),
                        services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>())
                    .Handle(new Stomme.Modules.Pipelines.Application.ListPipelinesQuery(), CancellationToken.None))
                .Single(view => view.DefinitionId == definitionId).Folder;

            using (PushTenant(sites[0]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Equal("Finance/Invoices", await Mover(services).Handle(new(pipelines[sites[0].Id], " Finance / Invoices "), CancellationToken.None));
                Assert.Equal("Finance/Invoices", await ListedAsync(services, pipelines[sites[0].Id]));
                var refused = await Assert.ThrowsAsync<ArgumentException>(() => Mover(services).Handle(new(pipelines[sites[0].Id], "a/b/c/d/e"), CancellationToken.None));
                Assert.Equal("A folder is at most 4 deep.", refused.Message);
                Assert.Equal("Finance/Invoices", await ListedAsync(services, pipelines[sites[0].Id]));
                // Another pipeline asked into "FINANCE" goes into the folder the site spells "Finance".
                var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
                var second = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = "second" } } });
                second.Name = "Second";
                await publisher.SaveDraftAsync(second);
                Assert.Equal("Finance", await Mover(services).Handle(new(second.DefinitionId, "FINANCE"), CancellationToken.None));
                // The other site's pipeline is not this site's to move.
                Assert.Null(await Mover(services).Handle(new(pipelines[sites[1].Id], "Stolen"), CancellationToken.None));
            }

            using (PushTenant(sites[1]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Equal("", await ListedAsync(services, pipelines[sites[1].Id]));
                await Mover(services).Handle(new(pipelines[sites[1].Id], "Ops"), CancellationToken.None);
                Assert.Equal("Ops", await ListedAsync(services, pipelines[sites[1].Id]));
            }

            // Site B's folder took nothing from site A's; the top clears it, and a deleted pipeline's goes with it.
            using (PushTenant(sites[0]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                Assert.Equal("Finance/Invoices", await ListedAsync(services, pipelines[sites[0].Id]));
                Assert.Equal("", await Mover(services).Handle(new(pipelines[sites[0].Id], ""), CancellationToken.None));
                Assert.Equal("", await ListedAsync(services, pipelines[sites[0].Id]));
                await Mover(services).Handle(new(pipelines[sites[0].Id], "Again"), CancellationToken.None);
                Assert.True(await new Stomme.Modules.Pipelines.Application.DeletePipelineCommandHandler(
                        services.GetRequiredService<IPipelineDefinitionManager>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>(),
                        new NoStepComments(), new NoStepComments(), new Stomme.Shared.SiteState())
                    .Handle(new Stomme.Modules.Pipelines.Application.DeletePipelineCommand(pipelines[sites[0].Id]), CancellationToken.None));
                Assert.Null(await services.GetRequiredService<IKeyValueStore>().FindAsync(
                    new KeyValueFilter { Key = Stomme.Modules.Pipelines.Application.PipelineFolderStore.Key(sites[0].Id, pipelines[sites[0].Id]) }, CancellationToken.None));
            }
        }

        // n8n's folder rename, move and delete: every pipeline in the folder, or in one inside it, goes along - on this
        // site only - or, removed, up to the folder above, joining a folder of the same name there; refused saying why.
        [Fact]
        public async Task A_folder_renamed_moved_or_removed_takes_what_is_in_it_on_its_site_only()
        {
            var sites = new[] { await RegisterTenantAsync("site-a-" + NewId()), await RegisterTenantAsync("site-b-" + NewId()) };
            var made = new Dictionary<(string Site, string Name), string>();
            foreach (var site in sites)
            {
                using (PushTenant(site))
                using (var scope = Scope())
                {
                    var services = scope.ServiceProvider;
                    foreach (var (name, folder) in new[] { ("invoice", "Finance"), ("refund", "Finance/Refunds"), ("report", "Reports"), ("loose", "") })
                    {
                        var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
                        var definition = await publisher.NewAsync(new Sequence { Steps = { new SinkStep { Value = name } } });
                        definition.Name = name;
                        await publisher.SaveDraftAsync(definition);
                        await new Stomme.Modules.Pipelines.Application.SetPipelineFolderCommandHandler(
                                services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>())
                            .Handle(new(definition.DefinitionId, folder), CancellationToken.None);
                        made[(site.Id, name)] = definition.DefinitionId;
                    }
                }
            }

            Stomme.Modules.Pipelines.Application.MovePipelineFolderCommandHandler Mover(IServiceProvider services) => new(services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>());
            async Task<IReadOnlyDictionary<string, string>> FoldersAsync(IServiceProvider services, string siteId) =>
                await Stomme.Modules.Pipelines.Application.PipelineFolderStore.ReadAllAsync(services.GetRequiredService<IKeyValueStore>(), siteId, CancellationToken.None);
            string At(IReadOnlyDictionary<string, string> folders, string siteId, string name) => folders.TryGetValue(made[(siteId, name)], out var folder) ? folder : "";

            var a = sites[0].Id;
            using (PushTenant(sites[0]))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                // Renamed - asked for in another case - and its folder inside goes along.
                var renamed = await Mover(services).Handle(new("finance", "Money", false), CancellationToken.None);
                Assert.Equal(("Money", 2), (renamed!.Folder, renamed.Moved));
                var folders = await FoldersAsync(services, a);
                Assert.Equal(("Money", "Money/Refunds", "Reports", ""), (At(folders, a, "invoice"), At(folders, a, "refund"), At(folders, a, "report"), At(folders, a, "loose")));

                // Moved into another folder, as a path.
                Assert.Equal("Reports/Money", (await Mover(services).Handle(new("Money", "reports/Money", false), CancellationToken.None))!.Folder);
                folders = await FoldersAsync(services, a);
                Assert.Equal(("Reports/Money", "Reports/Money/Refunds"), (At(folders, a, "invoice"), At(folders, a, "refund")));

                // Refused, saying why, and nothing moved.
                Assert.Equal("A folder cannot go inside itself.", (await Assert.ThrowsAsync<ArgumentException>(() => Mover(services).Handle(new("Reports", "Reports/Money/Again", false), CancellationToken.None))).Message);
                Assert.Equal("There is a folder 'Reports/Money' already.", (await Assert.ThrowsAsync<ArgumentException>(() => Mover(services).Handle(new("Reports/Money/Refunds", "Reports/Money", false), CancellationToken.None))).Message);
                Assert.Equal("A folder is at most 4 deep - a folder inside it would be deeper.", (await Assert.ThrowsAsync<ArgumentException>(() => Mover(services).Handle(new("Reports/Money", "A/B/C/D", false), CancellationToken.None))).Message);
                Assert.Null(await Mover(services).Handle(new("Nowhere", "Somewhere", false), CancellationToken.None));
                Assert.Equal(folders.OrderBy(pair => pair.Key), (await FoldersAsync(services, a)).OrderBy(pair => pair.Key));

                // Removed: what was in it goes up - its own pipeline into Reports, its folder inside to Reports/Refunds.
                var removed = await Mover(services).Handle(new("Reports/Money", null, true), CancellationToken.None);
                Assert.Equal(("Reports", 2), (removed!.Folder, removed.Moved));
                folders = await FoldersAsync(services, a);
                Assert.Equal(("Reports", "Reports/Refunds", "Reports"), (At(folders, a, "invoice"), At(folders, a, "refund"), At(folders, a, "report")));

                // A folder at the top removed: its pipelines are at the top of the list - no folder kept for them at all.
                Assert.Equal("", (await Mover(services).Handle(new("Reports", null, true), CancellationToken.None))!.Folder);
                folders = await FoldersAsync(services, a);
                Assert.Equal(("", "Refunds", ""), (At(folders, a, "invoice"), At(folders, a, "refund"), At(folders, a, "report")));
                Assert.False(folders.ContainsKey(made[(a, "invoice")]));
            }

            // The other site's folders took nothing from it.
            var b = sites[1].Id;
            using (PushTenant(sites[1]))
            using (var scope = Scope())
            {
                var folders = await FoldersAsync(scope.ServiceProvider, b);
                Assert.Equal(("Finance", "Finance/Refunds", "Reports", ""), (At(folders, b, "invoice"), At(folders, b, "refund"), At(folders, b, "report"), At(folders, b, "loose")));
            }
        }

        // ---- BL-229: the runs list's days (n8n's start-date filter)

        // Whole days in UTC, both kept, on every store: a run at midnight starts its day and the day's last tick ends it,
        // whatever zone the server runs in - the engine reads a timestamp AT midnight as a day built in the server's zone, so
        // the query never hands it one.
        [Fact]
        public async Task Runs_are_listed_by_the_UTC_days_they_started_on_both_days_kept()
        {
            var site = await RegisterTenantAsync("runs-days-" + NewId());
            var definition = Definition("def-days-" + NewId(), 1, latest: true, published: true, "Days");
            PipelineInstance Run(string name, DateTimeOffset createdAt)
            {
                var run = Instance(definition.DefinitionId, definition.Id, PipelineStatus.Finished, PipelineSubStatus.Finished, name, null, 0, 0);
                run.CreatedAt = createdAt;
                return run;
            }

            var midnight = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
            using (PushTenant(site))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                foreach (var run in new[] { Run("before", midnight.AddTicks(-1)), Run("midnight", midnight), Run("last tick", midnight.AddDays(1).AddTicks(-1)), Run("next day", midnight.AddDays(1)), Run("later", midnight.AddDays(3)) })
                {
                    await services.GetRequiredService<IPipelineInstanceStore>().AddAsync(run);
                }

                var handler = new Stomme.Modules.Pipelines.Application.ListRunsQueryHandler(services.GetRequiredService<IPipelineInstanceStore>(),
                    services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>());
                async Task<string[]> NamesAsync(DateOnly? from, DateOnly? to) =>
                    (await handler.Handle(new Stomme.Modules.Pipelines.Application.ListRunsQuery(definition.DefinitionId, 100, null, null, from, to), CancellationToken.None))
                        .Select(run => run.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var day = new DateOnly(2026, 10, 8);
                Assert.Equal(new[] { "last tick", "midnight" }, await NamesAsync(day, day));
                Assert.Equal(new[] { "last tick", "later", "midnight", "next day" }, await NamesAsync(day, null));
                Assert.Equal(new[] { "before", "last tick", "midnight" }, await NamesAsync(null, day));
                Assert.Equal(5, (await NamesAsync(null, null)).Length);
                await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(
                    new Stomme.Modules.Pipelines.Application.ListRunsQuery(definition.DefinitionId, 100, null, null, day, day.AddDays(-1)), CancellationToken.None));
            }
        }

        // ---- BL-232: the engine in an installation's backup

        // A backup carries the site's pipelines - every row or document of the engine, exactly - and a restore puts back what it
        // held: a definition deleted since comes back, a key saved since goes, and the engine reads what was put back (its
        // polymorphic payloads, its options, a run's state). A second backup after the restore is the first, record for record.
        [Fact]
        public async Task The_pipelines_are_carried_by_a_backup_and_put_back_exactly()
        {
            var siteA = await RegisterTenantAsync(EngineTenantIds.For(1, 1));
            var siteB = await RegisterTenantAsync(EngineTenantIds.For(2, 1));
            var a = await SeedAsync(siteA, "a");
            await SeedAsync(siteB, "b");
            var scope = new BackupScope("10.30.0", new[] { 1, 2 });
            var part = await BackUpAsync(scope);
            var records = await RecordsAsync(part);
            Assert.True(EngineBackupParticipant.Tables.Length == 8 && records.Count > 8 * 2, $"expected every table with rows, got {records.Count} records");

            using (PushTenant(siteA))
            using (var di = Scope())
            {
                await di.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>().DeleteAsync(new PipelineDefinitionFilter { Id = a.Definition.Id });
                await di.ServiceProvider.GetRequiredService<IKeyValueStore>().SaveAsync(new SerializedKeyValuePair { Key = "late:a", SerializedValue = "x" }, CancellationToken.None);
            }

            await RestoreAsync(part, scope);

            Assert.Equal(records, await RecordsAsync(await BackUpAsync(scope)));
            using (PushTenant(siteA))
            using (var di = Scope())
            {
                var services = di.ServiceProvider;
                var definition = await services.GetRequiredService<IPipelineDefinitionStore>().FindAsync(new PipelineDefinitionFilter { Id = a.Definition.Id });
                Assert.Equal(("Pipeline a", (object)"tests", typeof(PipelineDefinition)), (definition!.Name, definition.CustomProperties["owner"], definition.Options.ActivationStrategyType));
                var instance = await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = a.Instance.Id });
                Assert.Equal((PipelineSubStatus.Suspended, a.Definition.DefinitionId), (instance!.SubStatus, instance.PipelineState.DefinitionId));
                var bookmark = await services.GetRequiredService<IBookmarkStore>().FindAsync(new BookmarkFilter { BookmarkId = a.Bookmark.Id });
                Assert.Equal(Payloads.Serialize(new BookmarkPayload("a", 7)), Payloads.Serialize(bookmark!.Payload!));
                var keyValues = services.GetRequiredService<IKeyValueStore>();
                Assert.Null(await keyValues.FindAsync(new KeyValueFilter { Key = "late:a" }, CancellationToken.None));
                Assert.Equal("Finance", (await keyValues.FindAsync(new KeyValueFilter { Key = a.Key }, CancellationToken.None))!.SerializedValue);
            }
        }

        // A partial restore puts back the restored tenant's pipelines and leaves every other tenant's as they are.
        [Fact]
        public async Task A_partial_restore_puts_back_the_restored_tenants_pipelines_and_no_other()
        {
            var siteA = await RegisterTenantAsync(EngineTenantIds.For(1, 1));
            var siteB = await RegisterTenantAsync(EngineTenantIds.For(2, 1));
            await SeedAsync(siteA, "a");
            var b = await SeedAsync(siteB, "b");
            var part = await BackUpAsync(new BackupScope("10.30.0", new[] { 1, 2 }));

            foreach (var (site, key) in new[] { (siteA, "late:a"), (siteB, "late:b") })
            {
                using (PushTenant(site))
                using (var di = Scope())
                {
                    await di.ServiceProvider.GetRequiredService<IKeyValueStore>().SaveAsync(new SerializedKeyValuePair { Key = key, SerializedValue = "x" }, CancellationToken.None);
                }
            }

            using (PushTenant(siteB))
            using (var di = Scope())
            {
                await di.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>().DeleteAsync(new PipelineDefinitionFilter { Id = b.Definition.Id });
            }

            await RestoreAsync(part, new BackupScope("10.30.0", new[] { 1 }, new[] { 1, 2 }));

            using (PushTenant(siteA))
            using (var di = Scope())
            {
                Assert.Null(await di.ServiceProvider.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = "late:a" }, CancellationToken.None));
            }

            using (PushTenant(siteB))
            using (var di = Scope())
            {
                Assert.NotNull(await di.ServiceProvider.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = "late:b" }, CancellationToken.None));
                Assert.Null(await di.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>().FindAsync(new PipelineDefinitionFilter { Id = b.Definition.Id }));
            }
        }

        // What cannot be put back is refused before anything is touched: a part from the other store family, or naming a table
        // this install does not keep.
        [Fact]
        public async Task A_part_from_another_store_or_naming_an_unknown_table_is_refused()
        {
            await SeedAsync(await RegisterTenantAsync(EngineTenantIds.For(1, 1)), "a");
            var scope = new BackupScope("10.30.0", new[] { 1 });
            var records = await RecordsAsync(await BackUpAsync(scope));
            using var di = Scope();
            var participant = Participant(di);

            var otherStore = await EncodeAsync(records.Select(record => record.Replace("\"$store\":\"relational\"", "\"$store\":\"other\"").Replace("\"$store\":\"ravendb\"", "\"$store\":\"other\"")));
            var refused = await participant.ValidateAsync(new PartReader(otherStore, participant.SchemaVersion), scope, CancellationToken.None);
            Assert.Contains(refused.Problems, problem => problem.Contains("engine store", StringComparison.Ordinal));

            var unknownTable = await EncodeAsync(records.Select(record => record.Contains("\"$table\"", StringComparison.Ordinal) ? "{\"$table\":\"Nope\"}" : record));
            refused = await participant.ValidateAsync(new PartReader(unknownTable, participant.SchemaVersion), scope, CancellationToken.None);
            Assert.Contains(refused.Problems, problem => problem.Contains("does not keep", StringComparison.Ordinal));
        }

        private sealed record Seeded(PipelineDefinition Definition, PipelineInstance Instance, StoredBookmark Bookmark, string Key);

        // One of every engine entity under the tenant: a definition with options and custom properties, a suspended run with its
        // state, a bookmark and a trigger with polymorphic payloads, a queued resume (current, and matching no bookmark, so the
        // engine's queue tasks leave it be), a step execution, a log line and a key.
        private async Task<Seeded> SeedAsync(Tenant tenant, string mark)
        {
            var definition = Definition("def-" + mark, 1, latest: true, published: true, "Pipeline " + mark);
            var instance = Instance(definition.DefinitionId, definition.Id, PipelineStatus.Running, PipelineSubStatus.Suspended, "Run " + mark, "corr-" + mark, 1, 2);
            var bookmark = new StoredBookmark { Id = NewId(), Name = "Event", Hash = "h-" + mark, PipelineInstanceId = instance.Id, CreatedAt = At(1), Payload = new BookmarkPayload(mark, 7) };
            var trigger = new StoredTrigger { Id = NewId(), PipelineDefinitionId = definition.DefinitionId, PipelineDefinitionVersionId = definition.Id, Name = "Cron", StepId = "s", Hash = "t-" + mark, Payload = new BookmarkPayload(mark, 8) };
            var queued = new BookmarkQueueItem { Id = NewId(), StimulusHash = "q-" + mark + NewId(), CreatedAt = DateTimeOffset.UtcNow };
            var step = new StepExecutionRecord { Id = NewId(), PipelineInstanceId = instance.Id, StepId = "s", StepNodeId = "n", StepType = "T", StartedAt = At(1), Status = StepStatus.Running };
            var log = new PipelineExecutionLogRecord { Id = NewId(), PipelineDefinitionId = definition.DefinitionId, PipelineDefinitionVersionId = definition.Id, PipelineInstanceId = instance.Id, StepInstanceId = "si", StepId = "s", StepType = "T", StepNodeId = "n", Timestamp = At(1), Sequence = 1 };
            var pair = new SerializedKeyValuePair { Key = "pipelines:folder:" + mark, SerializedValue = "Finance" };
            using (PushTenant(tenant))
            using (var scope = Scope())
            {
                var services = scope.ServiceProvider;
                await services.GetRequiredService<IPipelineDefinitionStore>().SaveAsync(definition);
                await services.GetRequiredService<IPipelineInstanceStore>().AddAsync(instance);
                await services.GetRequiredService<IBookmarkStore>().SaveAsync(bookmark);
                await services.GetRequiredService<ITriggerStore>().SaveAsync(trigger);
                await services.GetRequiredService<IBookmarkQueueStore>().AddAsync(queued);
                await services.GetRequiredService<IStepExecutionStore>().SaveAsync(step);
                await services.GetRequiredService<IPipelineExecutionLogStore>().AddAsync(log);
                await services.GetRequiredService<IKeyValueStore>().SaveAsync(pair, CancellationToken.None);
            }

            return new Seeded(definition, instance, bookmark, pair.Key);
        }

        private static EngineBackupParticipant Participant(IServiceScope scope) =>
            scope.ServiceProvider.GetServices<IBackupParticipant>().OfType<EngineBackupParticipant>().Single();

        private async Task<byte[]> BackUpAsync(BackupScope scope)
        {
            using var di = Scope();
            var writer = new PartWriter();
            await Participant(di).ExportAsync(writer, scope, CancellationToken.None);
            return writer.Bytes.ToArray();
        }

        private async Task RestoreAsync(byte[] part, BackupScope scope)
        {
            using var di = Scope();
            var participant = Participant(di);
            var validation = await participant.ValidateAsync(new PartReader(part, participant.SchemaVersion), scope, CancellationToken.None);
            Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
            await participant.ReplaceAsync(new PartReader(part, participant.SchemaVersion), scope, CancellationToken.None);
        }

        private static async Task<List<string>> RecordsAsync(byte[] part)
        {
            var records = new List<string>();
            await foreach (var (_, json) in BackupRecords.ReadAsync(new MemoryStream(part), CancellationToken.None))
            {
                records.Add(json);
            }

            return records;
        }

        private static async Task<byte[]> EncodeAsync(IEnumerable<string> records)
        {
            var stream = new MemoryStream();
            foreach (var record in records)
            {
                await BackupRecords.WriteAsync(stream, null, record, CancellationToken.None);
            }

            return stream.ToArray();
        }

        private sealed class PartWriter : IBackupPartWriter
        {
            public MemoryStream Bytes { get; } = new();

            public string PartName => EngineBackupParticipant.PartName;

            public Stream Content => Bytes;
        }

        private sealed class PartReader(byte[] bytes, string schemaVersion) : IBackupPartReader
        {
            public string PartName => EngineBackupParticipant.PartName;

            public string SchemaVersion => schemaVersion;

            public long Length => bytes.Length;

            public Stream OpenRead() => new MemoryStream(bytes, writable: false);
        }

        private sealed class FixedClock(DateTimeOffset now) : Stomme.Engine.Common.ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }
    }

    public sealed class SqliteEngineStoreConformanceTests : EngineStoreConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-engine-conformance-{Guid.NewGuid():N}.db")}";

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = _connectionString,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(new SqliteConnectionStringBuilder(_connectionString).DataSource);
            }
            catch (IOException)
            {
            }

            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresEngineStoreConformanceTests : EngineStoreConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_engine_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The engine store conformance suite FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = PostgresTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString(_database)));
            PostgresTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerEngineStoreConformanceTests : EngineStoreConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_engine_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The engine store conformance suite FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = SqlServerTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearPool(new Microsoft.Data.SqlClient.SqlConnection(SqlServerTestEndpoint.ConnectionString(_database)));
            SqlServerTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenEngineStoreConformanceTests : EngineStoreConformanceTests
    {
        protected override string ExpectedStore => "RavenPipelineDefinitionStore";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-engine-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-engine-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
                })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
            }
        }
    }
}
