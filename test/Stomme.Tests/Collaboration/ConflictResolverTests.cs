#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Modules.Collaboration.Application;
using Stomme.Persistence.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// BL-97 increment 12 - the predecessor platform's <c>ConflictResolverTests</c> by name, over the lifted <see cref="ConflictResolver"/>:
    /// a clean application when nothing changed concurrently, distinct elements and the same element's disjoint fields
    /// merged, a structured same-field conflict rejected with the current value, a text same-field divergence
    /// last-writer-wins with a warning, and the bounded retry around the store's <see cref="ConcurrencyConflictException"/>.
    /// CHANGED: a version is a string token and a user a Stomme user id.
    /// </summary>
    public sealed class ConflictResolverTests
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 3, 10, 0, 0, TimeSpan.Zero);

        private const int AuthorId = 7;
        private const int OtherEditorId = 9;

        private readonly ConflictResolver _resolver = new(new FixedClock(Now));

        // ----- Resolve: no concurrent change -----

        [Fact]
        public void Resolve_WithMatchingVersions_AppliesEveryProposedChange()
        {
            var baseSnapshot = Snapshot("v3", ("steps/1/name", "Extract"), ("steps/2/name", "Load"));
            var current = Snapshot("v3", ("steps/1/name", "Extract"), ("steps/2/name", "Load"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("steps/1/name", "Extract v2") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Applied, resolution.Status);
            Assert.Equal("Extract v2", resolution.MergedFields["steps/1/name"]);
            Assert.Equal("Load", resolution.MergedFields["steps/2/name"]); // untouched fields preserved
            Assert.Empty(resolution.Warnings);
            Assert.Empty(resolution.Conflicts);
            Assert.Equal("v3", resolution.BaseVersion);
            Assert.Equal("v3", resolution.CurrentVersion);
        }

        // ----- Resolve: field-level merge -----

        [Fact]
        public void Resolve_DistinctElements_MergeIndependently()
        {
            // Another editor renamed step 2 (the version moved); this editor renames step 5.
            var baseSnapshot = Snapshot("v3", ("steps/2/name", "Load"), ("steps/5/name", "Notify"));
            var current = Snapshot("v4", ("steps/2/name", "Load to warehouse"), ("steps/5/name", "Notify"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("steps/5/name", "Notify operators") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal("Load to warehouse", resolution.MergedFields["steps/2/name"]); // theirs kept
            Assert.Equal("Notify operators", resolution.MergedFields["steps/5/name"]); // ours applied
            Assert.Empty(resolution.Warnings);
            Assert.Empty(resolution.Conflicts);
        }

        [Fact]
        public void Resolve_SameElementDifferentFields_MergesFieldLevel()
        {
            // Both editors touched step 3 - but disjoint fields: timeout (theirs) and name (ours).
            var baseSnapshot = Snapshot("v7", ("steps/3/name", "Validate"), ("steps/3/timeout", "30"));
            var current = Snapshot("v8", ("steps/3/name", "Validate"), ("steps/3/timeout", "60"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("steps/3/name", "Validate hostnames") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal("60", resolution.MergedFields["steps/3/timeout"]);
            Assert.Equal("Validate hostnames", resolution.MergedFields["steps/3/name"]);
            Assert.Empty(resolution.Warnings);
            Assert.Empty(resolution.Conflicts);
        }

        [Fact]
        public void Resolve_ConvergentSameFieldSameValue_IsNoConflict()
        {
            var baseSnapshot = Snapshot("v1", ("widgets/2/title", "Sales"));
            var current = Snapshot("v2", ("widgets/2/title", "Quarterly sales"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("widgets/2/title", "Quarterly sales") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal("Quarterly sales", resolution.MergedFields["widgets/2/title"]);
            Assert.Empty(resolution.Warnings);
            Assert.Empty(resolution.Conflicts);
        }

        [Fact]
        public void Resolve_FieldAddedConcurrently_IsKeptInTheMerge()
        {
            var baseSnapshot = Snapshot("v1", ("widgets/1/title", "CPU"));
            var current = Snapshot("v2", ("widgets/1/title", "CPU"), ("widgets/2/title", "Memory"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("widgets/1/title", "CPU load") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal("CPU load", resolution.MergedFields["widgets/1/title"]);
            Assert.Equal("Memory", resolution.MergedFields["widgets/2/title"]);
        }

        // ----- Resolve: same-field structured conflict -----

        [Fact]
        public void Resolve_SameStructuredField_RejectsWithTheCurrentValue()
        {
            var baseSnapshot = Snapshot("v5", ("steps/1/connection", "prod-east"));
            var current = Snapshot("v6", ("steps/1/connection", "prod-west"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("steps/1/connection", "staging") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Rejected, resolution.Status);
            var conflict = Assert.Single(resolution.Conflicts);
            Assert.Equal(("steps/1/connection", "staging", "prod-west"), (conflict.Path, conflict.ProposedValue, conflict.CurrentValue));
            Assert.Empty(resolution.MergedFields); // nothing applies on rejection
            Assert.Empty(resolution.Warnings);
        }

        [Fact]
        public void Resolve_WithAnyStructuredConflict_RejectsTheWholeSave()
        {
            var baseSnapshot = Snapshot("v5", ("steps/1/connection", "prod-east"), ("steps/9/name", "Report"));
            var current = Snapshot("v6", ("steps/1/connection", "prod-west"), ("steps/9/name", "Report"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("steps/1/connection", "staging"), FieldChange.Of("steps/9/name", "Report daily") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Rejected, resolution.Status);
            Assert.Single(resolution.Conflicts); // only the contended field is a conflict...
            Assert.Empty(resolution.MergedFields); // ...but the whole save is rejected
        }

        // ----- Resolve: same-field text = last-writer-wins + warning -----

        [Fact]
        public void Resolve_SameTextField_LastWriterWins_AndWarnsAboutTheOverwrite()
        {
            var baseSnapshot = Snapshot("v2", ("widgets/4/query", "select 1"));
            var current = ResourceSnapshot.Of("v3", new Dictionary<string, string?> { ["widgets/4/query"] = "select count(*) from assets" }, OtherEditorId);

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("widgets/4/query", "select top 10 * from assets", FieldKind.Text) }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal("select top 10 * from assets", resolution.MergedFields["widgets/4/query"]);
            var warning = Assert.Single(resolution.Warnings);
            Assert.Equal("widgets/4/query", warning.Path);
            Assert.Equal("select top 10 * from assets", warning.WinningValue);
            Assert.Equal("select count(*) from assets", warning.OverwrittenValue);
            Assert.Equal(AuthorId, warning.WinningAuthorUserId);
            Assert.Equal(OtherEditorId, warning.OverwrittenAuthorUserId);
            Assert.Equal(Now.UtcDateTime, warning.OccurredUtc);
        }

        [Fact]
        public void Resolve_TextLww_DoesNotSoftenAStructuredConflictElsewhere()
        {
            var baseSnapshot = Snapshot("v2", ("widgets/4/query", "select 1"), ("widgets/4/type", "grid"));
            var current = Snapshot("v3", ("widgets/4/query", "select 2"), ("widgets/4/type", "chart"));

            var resolution = _resolver.Resolve(baseSnapshot, current, new[] { FieldChange.Of("widgets/4/query", "select 3", FieldKind.Text), FieldChange.Of("widgets/4/type", "kpi") }, AuthorId);

            Assert.Equal(ConflictResolutionStatus.Rejected, resolution.Status);
            Assert.Equal("widgets/4/type", Assert.Single(resolution.Conflicts).Path);
            Assert.Empty(resolution.MergedFields);
        }

        // ----- Resolve: argument contracts -----

        [Fact]
        public void Resolve_RejectsDuplicateProposedPaths_AndAMissingAuthor()
        {
            var snapshot = Snapshot("v1", ("a", "1"));

            Assert.Throws<ArgumentException>(() => _resolver.Resolve(snapshot, snapshot, new[] { FieldChange.Of("a", "2"), FieldChange.Of("a", "3") }, AuthorId));
            Assert.Throws<ArgumentException>(() => _resolver.Resolve(snapshot, snapshot, new[] { FieldChange.Of("a", "2") }, 0));
        }

        [Fact]
        public void FieldChange_RequiresANonBlankPath()
        {
            Assert.Throws<ArgumentException>(() => FieldChange.Of("  ", "value"));
        }

        // ----- ResolveAndSaveAsync: the retry contract -----

        [Fact]
        public async Task ResolveAndSaveAsync_WithoutContention_SavesTheMergedFieldsOnce()
        {
            var store = new FakeResourceStore(Snapshot("v1", ("steps/1/name", "Extract")));

            var resolution = await _resolver.ResolveAndSaveAsync(Snapshot("v1", ("steps/1/name", "Extract")), new[] { FieldChange.Of("steps/1/name", "Extract v2") }, AuthorId, store.LoadAsync, store.SaveAsync);

            Assert.Equal(ConflictResolutionStatus.Applied, resolution.Status);
            Assert.Equal(1, store.SaveAttempts);
            Assert.Equal("Extract v2", Assert.Single(store.SavedFieldSets)["steps/1/name"]);
        }

        [Fact]
        public async Task ResolveAndSaveAsync_OnAConcurrencyConflict_ReloadsAndReresolves()
        {
            // The first save loses the race: another editor renamed step 2 in between. The retry reloads, re-resolves field
            // by field, and the second save keeps both changes.
            var store = new FakeResourceStore(Snapshot("v1", ("steps/1/name", "Extract"), ("steps/2/name", "Load")))
            {
                FailuresBeforeSuccess = 1,
                OnConflict = self => self.Current = Snapshot("v2", ("steps/1/name", "Extract"), ("steps/2/name", "Load to warehouse")),
            };

            var resolution = await _resolver.ResolveAndSaveAsync(Snapshot("v1", ("steps/1/name", "Extract"), ("steps/2/name", "Load")), new[] { FieldChange.Of("steps/1/name", "Extract v2") }, AuthorId, store.LoadAsync, store.SaveAsync);

            Assert.Equal(ConflictResolutionStatus.Merged, resolution.Status);
            Assert.Equal((2, 2), (store.SaveAttempts, store.Loads));
            var persisted = store.SavedFieldSets[^1];
            Assert.Equal("Extract v2", persisted["steps/1/name"]);
            Assert.Equal("Load to warehouse", persisted["steps/2/name"]);
        }

        [Fact]
        public async Task ResolveAndSaveAsync_WithARejectedResolution_NeverSaves()
        {
            var store = new FakeResourceStore(Snapshot("v2", ("steps/1/connection", "prod-west")));

            var resolution = await _resolver.ResolveAndSaveAsync(Snapshot("v1", ("steps/1/connection", "prod-east")), new[] { FieldChange.Of("steps/1/connection", "staging") }, AuthorId, store.LoadAsync, store.SaveAsync);

            Assert.Equal(ConflictResolutionStatus.Rejected, resolution.Status);
            Assert.Equal(0, store.SaveAttempts);
            Assert.Equal("prod-west", Assert.Single(resolution.Conflicts).CurrentValue);
        }

        [Fact]
        public async Task ResolveAndSaveAsync_UnderPersistentContention_PropagatesAfterBoundedAttempts()
        {
            var store = new FakeResourceStore(Snapshot("v1", ("steps/1/name", "Extract"))) { FailuresBeforeSuccess = int.MaxValue };

            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _resolver.ResolveAndSaveAsync(Snapshot("v1", ("steps/1/name", "Extract")), new[] { FieldChange.Of("steps/1/name", "Extract v2") }, AuthorId, store.LoadAsync, store.SaveAsync));

            Assert.Equal(ConflictResolver.MaxSaveAttempts, store.SaveAttempts); // bounded, not unbounded
            Assert.Equal(ConflictResolver.MaxSaveAttempts, store.Loads); // a reload before every attempt
        }

        // ----- Registration -----

        [Fact]
        public void AddCollaboration_RegistersTheConflictResolver()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = "Data Source=:memory:",
                })
                .Build());
            new Stomme.Modules.Collaboration.Startup.ServerStartup().ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            Assert.IsType<ConflictResolver>(provider.GetRequiredService<IConflictResolver>());
        }

        private static ResourceSnapshot Snapshot(string version, params (string Path, string? Value)[] fields) =>
            ResourceSnapshot.Of(version, fields.ToDictionary(field => field.Path, field => field.Value, StringComparer.Ordinal));

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        /// <summary>
        /// A resource store for the retry contract: hands out the current snapshot, records every saved field set, and throws
        /// <see cref="ConcurrencyConflictException"/> for the first <see cref="FailuresBeforeSuccess"/> saves - calling
        /// <see cref="OnConflict"/> so a test can interleave the concurrent change the retry must pick up.
        /// </summary>
        private sealed class FakeResourceStore(ResourceSnapshot initial)
        {
            public ResourceSnapshot Current { get; set; } = initial;

            public int FailuresBeforeSuccess { get; init; }

            public Action<FakeResourceStore>? OnConflict { get; init; }

            public int Loads { get; private set; }

            public int SaveAttempts { get; private set; }

            public List<IReadOnlyDictionary<string, string?>> SavedFieldSets { get; } = new();

            public Task<ResourceSnapshot> LoadAsync(CancellationToken ct)
            {
                Loads++;
                return Task.FromResult(Current);
            }

            public Task SaveAsync(IReadOnlyDictionary<string, string?> fields, CancellationToken ct)
            {
                SaveAttempts++;
                if (SaveAttempts <= FailuresBeforeSuccess)
                {
                    OnConflict?.Invoke(this);
                    throw new ConcurrencyConflictException("Resource", "test");
                }

                SavedFieldSets.Add(fields);
                return Task.CompletedTask;
            }
        }
    }
}
