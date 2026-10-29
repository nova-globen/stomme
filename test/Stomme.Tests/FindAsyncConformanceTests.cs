using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The observable contract of the provider-neutral <c>IRepository.FindAsync</c>, expressed ONCE and
    /// DRIVEN against BOTH providers it must behave identically on (EF Core / SQLite and a LIVE RavenDB), the
    /// same fail-not-skip shape as the D46 parity suite and the D76 Guestbook conformance suite. A subclass
    /// supplies the store; the tests below know nothing about either provider.
    /// <para>
    /// This is the evidence for the whole modularity effort's crux (src/Stomme.SharedKernel/README.md): a storage-blind
    /// module can filter through <c>FindAsync</c> and get the SAME rows on every provider — but ONLY for the
    /// predicate shapes the contract calls safe. So the suite drives BOTH sides of the boundary:
    /// <list type="bullet">
    ///   <item><b>SAFE</b> predicates (int equality/range, <c>Guid</c> equality, an <c>enum</c>, a <c>bool</c>, a
    ///     <c>DateTime</c> range, and boolean composition) — asserted to return the SAME rows on both providers,
    ///     which the abstract-then-two-fixtures structure proves by running the identical assertions on each.</item>
    ///   <item>the <b>STRING boundary</b> — a raw mixed-case <c>==</c> is DEMONSTRATED to DIVERGE (EF/SQLite
    ///     case-sensitive misses it; RavenDB case-insensitive matches it), and the D49 normalized-column pattern
    ///     is proven to make the two providers AGREE. This is the boundary the contract documents, MEASURED here
    ///     rather than asserted in prose.</item>
    /// </list>
    /// </para>
    /// </summary>
    public abstract class FindAsyncConformanceTests : IAsyncLifetime
    {
        // A fixed, deterministic dataset (five rows) covering every predicate the suite drives.
        private static readonly Guid TagA = new("11111111-1111-1111-1111-111111111111");
        private static readonly Guid TagB = new("22222222-2222-2222-2222-222222222222");
        private static readonly Guid TagC = new("33333333-3333-3333-3333-333333333333");

        private static readonly DateTime D2020 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime D2021 = new(2021, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime D2022 = new(2022, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime D2023 = new(2023, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime D2024 = new(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        private ServiceProvider _services = default!;

        /// <summary>Provider hook: register the store (EF/SQLite or RavenDB) onto <paramref name="services"/>.</summary>
        /// <summary>Longs chosen to sit OUTSIDE int range, so a provider that silently narrowed would be caught.</summary>
        private const long SmallQuantity = 5L;
        private const long BigQuantity = 9_000_000_000L;
        private const long HugeQuantity = 9_223_372_036_854_775_000L;

        protected abstract void AddStore(IServiceCollection services);

        /// <summary>
        /// Provider hook stating this provider's string-equality semantics: RavenDB's bare <c>==</c> is
        /// case-INSENSITIVE, EF/SQLite's is case-SENSITIVE. The divergence test asserts against this, so the
        /// boundary is encoded as a measured provider fact, not hidden.
        /// </summary>
        protected abstract bool StringEqualityIsCaseInsensitive { get; }

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            _services = services.BuildServiceProvider();

            // Run the relational schema initializer (EnsureCreated) as the host does; a no-op on RavenDB.
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }

            // Seed the fixed dataset through the same repository under test, in its own scope per write.
            await SeedAsync(ProbeAggregate.Create(10, TagA, ProbeRating.Good, true, D2020, "Hello", SmallQuantity, 0.01m));
            await SeedAsync(ProbeAggregate.Create(20, TagA, ProbeRating.Excellent, false, D2021, "World", BigQuantity, 12.3456m));
            await SeedAsync(ProbeAggregate.Create(30, TagB, ProbeRating.Excellent, true, D2022, "Hello", BigQuantity, 0m));
            await SeedAsync(ProbeAggregate.Create(42, TagB, ProbeRating.Poor, false, D2023, "Foo", HugeQuantity, -7.5m));
            await SeedAsync(ProbeAggregate.Create(42, TagC, ProbeRating.Good, true, D2024, "Bar", 0L, 999999.999999m));
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        private async Task SeedAsync(ProbeAggregate probe)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRepository<ProbeAggregate, Guid>>().AddAsync(probe);
        }

        private async Task<IReadOnlyList<ProbeAggregate>> FindAsync(
            System.Linq.Expressions.Expression<Func<ProbeAggregate, bool>> predicate)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IRepository<ProbeAggregate, Guid>>()
                .FindAsync(predicate);
        }

        // ---- SAFE predicates: identical on both providers ------------------------------------------------

        [Fact]
        public async Task Int_equality_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Number == 42);
            Assert.Equal(2, matches.Count);
            Assert.All(matches, p => Assert.Equal(42, p.Number));
        }

        /// <summary>
        /// The shape a caller writes for "any of these" - and the one RavenDB's LINQ provider does NOT
        /// implement, so it threw "Could not understand expression" on the module seam while working on the
        /// framework seam, which passes through the rewriting query provider. The repository now rewrites the
        /// predicate itself, and this case holds every provider to the same answer.
        /// </summary>
        [Fact]
        public async Task Collection_membership_filters_in_the_store()
        {
            var wanted = new[] { 20, 42 };
            var matches = await FindAsync(p => wanted.Contains(p.Number));
            Assert.Equal(new[] { 20, 42, 42 }, matches.Select(p => p.Number).OrderBy(n => n).ToArray());
        }
        [Fact]
        public async Task Int_range_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Number >= 20 && p.Number < 42);
            Assert.Equal(new[] { 20, 30 }, matches.Select(p => p.Number).OrderBy(n => n).ToArray());
        }

        [Fact]
        public async Task Guid_equality_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Tag == TagB);
            Assert.Equal(2, matches.Count);
            Assert.All(matches, p => Assert.Equal(TagB, p.Tag));
        }

        /// <summary>
        /// <c>long</c> is already NAMED in the FindAsync safe set; what was missing was the evidence (D102 binding
        /// item 11). These two cases are it - equality and a range, over values well past <c>int</c>, on every
        /// provider the suite runs against. Metering counts in <c>long</c>, so "it is in the list" was not enough.
        /// </summary>
        [Fact]
        public async Task Long_equality_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Quantity == BigQuantity);
            Assert.Equal(2, matches.Count);
            Assert.All(matches, p => Assert.Equal(BigQuantity, p.Quantity));
        }

        [Fact]
        public async Task Long_range_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Quantity > SmallQuantity && p.Quantity <= HugeQuantity);
            Assert.Equal(new[] { BigQuantity, BigQuantity, HugeQuantity }, matches.Select(p => p.Quantity).OrderBy(q => q).ToArray());
        }

        /// <summary>
        /// <c>decimal</c> ROUND-TRIPS and is never filtered - the contract's deliberate edge. It is asserted here
        /// so the boundary is a measured fact rather than an omission someone later reads as an oversight: money is
        /// stored and rendered exactly, and a predicate over it is simply not part of the contract.
        /// </summary>
        [Fact]
        public async Task Decimal_round_trips_exactly_and_is_never_filtered()
        {
            var all = await FindAsync(p => p.Number >= 0);
            var amounts = all.Select(p => p.Amount).OrderBy(a => a).ToArray();

            Assert.Equal(new[] { -7.5m, 0m, 0.01m, 12.3456m, 999999.999999m }, amounts);
        }

        [Fact]
        public async Task Enum_equality_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Rating == ProbeRating.Excellent);
            Assert.Equal(new[] { 20, 30 }, matches.Select(p => p.Number).OrderBy(n => n).ToArray());
        }

        [Fact]
        public async Task Bool_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Active);
            Assert.Equal(new[] { 10, 30, 42 }, matches.Select(p => p.Number).OrderBy(n => n).ToArray());
        }

        [Fact]
        public async Task DateTime_range_filters_in_the_store()
        {
            var from = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var matches = await FindAsync(p => p.WhenUtc >= from && p.WhenUtc < to);
            Assert.Equal(new[] { 20, 30 }, matches.Select(p => p.Number).OrderBy(n => n).ToArray());
        }

        [Fact]
        public async Task Boolean_composition_filters_in_the_store()
        {
            var matches = await FindAsync(p => p.Rating == ProbeRating.Excellent && p.Active);
            var match = Assert.Single(matches);
            Assert.Equal(30, match.Number);
        }

        [Fact]
        public async Task A_predicate_matching_nothing_reads_empty()
        {
            var matches = await FindAsync(p => p.Number == 9999);
            Assert.Empty(matches);
        }

        // ---- The STRING boundary: divergence measured, normalization proven to agree ---------------------

        [Fact]
        public async Task Raw_string_equality_agrees_only_when_the_CASE_matches()
        {
            // Exact-case: both providers match the two "Hello" rows — this is where raw == happens to agree.
            var exact = await FindAsync(p => p.Name == "Hello");
            Assert.Equal(2, exact.Count);
        }

        [Fact]
        public async Task Raw_string_equality_DIVERGES_on_case()
        {
            // The stored value is "Hello"; the argument is lower-case "hello". This is the measured divergence
            // the contract warns about: EF/SQLite's bare == is case-SENSITIVE (0 rows), RavenDB's is
            // case-INSENSITIVE (2 rows). A raw-LINQ passthrough would silently return different data per install.
            var lower = await FindAsync(p => p.Name == "hello");

            if (StringEqualityIsCaseInsensitive)
            {
                Assert.Equal(2, lower.Count); // RavenDB
            }
            else
            {
                Assert.Empty(lower); // EF / SQLite (and PostgreSQL)
            }
        }

        [Fact]
        public async Task Normalized_string_equality_AGREES_across_providers()
        {
            // The D49 pattern: compare the normalized COLUMN against the SAME normalization of the argument
            // (upper-invariant, in code — not translated by the provider). Both providers return the two "Hello"
            // rows regardless of the argument's original case, so the neutral query is safe for text this way.
            var normalized = "hello".ToUpperInvariant();
            var matches = await FindAsync(p => p.NameNormalized == normalized);
            Assert.Equal(2, matches.Count);
            Assert.All(matches, p => Assert.Equal("Hello", p.Name));
        }
    }
}
