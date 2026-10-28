using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests
{
    /// <summary>
    /// D76 — the Guestbook clean-arch store's observable contract, expressed ONCE and DRIVEN against BOTH
    /// providers it must run on (the same shape as D73's <c>FeatureFlagStoreConformanceTests</c>). A subclass
    /// supplies the fixture — the relational (EF Core / SQLite) store or the live RavenDB document store — and
    /// the tests below know nothing about either. Both providers must produce the SAME observable results:
    /// signing persists, a re-read in a FRESH scope returns the entry with its <see cref="AuthorName"/> value
    /// object intact, and <c>GetByModuleAsync</c> filters by module in the database, newest first.
    /// <para>
    /// The <see cref="AuthorName"/> round-trip is the point the brief flagged as the trap. It is NOT a trap
    /// today (Newtonsoft reconstructs the value object through its private constructor even without the
    /// <c>IStringValueObject</c> marker — MEASURED), but this suite pins the round-trip on BOTH providers so it
    /// cannot silently regress if the value object grows a second constructor.
    /// </para>
    /// </summary>
    public abstract class GuestbookStoreConformanceTests : IAsyncLifetime
    {
        private const int ModuleA = 4242;
        private const int ModuleB = 9191;

        private ServiceProvider _services = default!;

        /// <summary>The ambient tenant every scope sees - switched by the two-tenant case (BL-111); the default otherwise.</summary>
        private readonly SwitchableTenantContext _tenant = new(TenantId.Default);

        /// <summary>Provider hook: register the store (EF/SQLite or RavenDB) onto <paramref name="services"/>.</summary>
        protected abstract void AddStore(IServiceCollection services);

        /// <summary>
        /// Provider hook (BL-150): whether <c>FindAsync</c> over the <see cref="AuthorName"/> value object matches a
        /// DIFFERENT case. Measured: EF/SQLite no (it compares the converted string), RavenDB yes (case-insensitive), File
        /// yes (the value object's own <c>Equals</c>, which lower-cases). Encoded as a provider fact, like the string
        /// boundary in the D78 suite - so a store that starts to disagree with its recorded answer goes red.
        /// </summary>
        protected abstract bool ValueObjectEqualityIsCaseInsensitive { get; }

        /// <summary>Provider hook: async store setup before composition (e.g. create the RavenDB database).</summary>
        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        /// <summary>Provider hook: async store teardown after disposal (e.g. drop the RavenDB database).</summary>
        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            // AFTER the store: the last registration wins, so every provider's repository - and the File store's
            // partition - reads the tenant this fixture controls, not the module fallback the registration TryAdds.
            services.AddScoped<ITenantContext>(_ => _tenant);
            _services = services.BuildServiceProvider();

            // Run the relational schema initializer (EnsureCreated) exactly as the host does at startup. The
            // RavenDB fixture registers no hosted service (schemaless), so this is a no-op there.
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        private async Task<T> InScopeAsync<T>(Func<IGuestbookRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IGuestbookRepository>());
        }

        private Task SignAsync(int moduleId, string author, string message) =>
            InScopeAsync(async repository =>
            {
                await repository.AddAsync(GuestbookEntryAggregate.Sign(_tenant.CurrentTenantId!, moduleId, author, message));
                return 0;
            });

        /// <summary>
        /// BL-111 (D79's correction), the three-way divergence the survey predicted and the probe measured: a signature
        /// for module 4242 under tenant ONE was returned to tenant TWO on EF and RavenDB (a module id repeats across
        /// tenants; the store is one per installation) and not on File (it partitions by tenant). Tenant-owned now, every
        /// provider isolates it: tenant TWO reads an empty book for the same module id, tenant ONE still reads its row.
        /// </summary>
        [Fact]
        public async Task A_signature_under_one_tenant_is_invisible_to_another_tenant_with_the_same_module_id()
        {
            var one = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            var two = TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

            _tenant.Switch(one);
            await SignAsync(ModuleA, "Ada", "Written by tenant ONE.");
            Assert.Single(await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA)));

            _tenant.Switch(two);
            Assert.Empty(await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA)));
            await SignAsync(ModuleA, "Grace", "Written by tenant TWO.");
            var twos = await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA));
            Assert.Equal("Written by tenant TWO.", Assert.Single(twos).Message);

            _tenant.Switch(one);
            var ones = await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA));
            Assert.Equal("Written by tenant ONE.", Assert.Single(ones).Message);
        }

        private sealed class SwitchableTenantContext(TenantId initial) : ITenantContext
        {
            public TenantId? CurrentTenantId { get; private set; } = initial;

            public bool IsSystemScope => false;

            public void Switch(TenantId tenant) => CurrentTenantId = tenant;
        }

        [Fact]
        public async Task Signing_persists_and_re_reads_in_a_fresh_scope_with_the_author_intact()
        {
            await SignAsync(ModuleA, "Ada Lovelace", "First to compute.");

            // The entry is found by module, in a fresh scope — so the write reached the store, not just the
            // identity map — and its AuthorName value object survived the round-trip.
            var entries = await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA));
            var entry = Assert.Single(entries);

            Assert.Equal(ModuleA, entry.ModuleId);
            Assert.Equal("First to compute.", entry.Message);
            Assert.NotNull(entry.Author);
            Assert.Equal("Ada Lovelace", entry.Author.Value);

            // Re-read by id, again in a fresh scope: the same round-trip through the generic repository's
            // GetById path (a document Load on RavenDB, a keyed query on EF).
            var byId = await InScopeAsync(repository => repository.GetByIdAsync(entry.Id));
            Assert.NotNull(byId);
            Assert.Equal("Ada Lovelace", byId!.Author.Value);
        }

        [Fact]
        public async Task GetByModule_filters_by_module_newest_first_with_authors_preserved()
        {
            await SignAsync(ModuleA, "Grace Hopper", "Signed first.");
            await Task.Delay(10); // ensure a strictly later SignedOnUtc for deterministic ordering
            await SignAsync(ModuleA, "Alan Turing", "Signed second.");
            await SignAsync(ModuleB, "Someone Else", "A different guestbook.");

            var entries = await InScopeAsync(repository => repository.GetByModuleAsync(ModuleA));

            Assert.Equal(2, entries.Count);
            // Newest first.
            Assert.Equal("Alan Turing", entries[0].Author.Value);
            Assert.Equal("Grace Hopper", entries[1].Author.Value);
            // The other module's book is not leaked into this one.
            Assert.DoesNotContain(entries, entry => entry.Author.Value == "Someone Else");
        }

        [Fact]
        public async Task FindAsync_over_the_value_object_TRANSLATES_and_diverges_only_on_case()
        {
            await SignAsync(ModuleA, "Ada Lovelace", "Present.");
            await SignAsync(ModuleA, "Grace Hopper", "Also present.");

            // Exact case: one row on every store - the predicate translates everywhere (EF, the RavenDB query, File in memory).
            var exact = AuthorName.Create("Ada Lovelace");
            Assert.Equal("Ada Lovelace", Assert.Single(await InScopeAsync(repository => repository.FindAsync(e => e.Author == exact))).Author.Value);

            // Another case: the STRING boundary, measured - a text value object is text, not a safe scalar.
            var lower = AuthorName.Create("ada lovelace");
            var found = await InScopeAsync(repository => repository.FindAsync(e => e.Author == lower));
            Assert.Equal(ValueObjectEqualityIsCaseInsensitive ? 1 : 0, found.Count);
        }

        [Fact]
        public async Task An_unknown_module_reads_as_an_empty_book()
        {
            await SignAsync(ModuleA, "Ada Lovelace", "Present.");

            var entries = await InScopeAsync(repository => repository.GetByModuleAsync(123456));

            Assert.Empty(entries);
        }
    }
}
