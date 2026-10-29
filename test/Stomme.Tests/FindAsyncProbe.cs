using System;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Tests
{
    /// <summary>An enum column, so the FindAsync conformance suite can drive an enum-equality predicate.</summary>
    public enum ProbeRating
    {
        Poor = 0,
        Good = 1,
        Excellent = 2,
    }

    /// <summary>
    /// A throwaway aggregate for the <c>IRepository.FindAsync</c> conformance suite. It carries ONE column of
    /// every scalar shape the FindAsync contract calls SAFE (<c>int</c>, <c>Guid</c>, an <c>enum</c>, <c>bool</c>,
    /// <c>DateTime</c>) plus a raw string and a normalized string, so the suite can drive every safe predicate
    /// AND the string boundary (raw-case divergence + normalized-column agreement) against BOTH providers.
    /// <para>
    /// It lives in the <c>Stomme.Tests</c> namespace ON PURPOSE — outside <c>Stomme.Modules.*</c> — so the
    /// <c>ModuleAggregateProviderNeutralityTests</c> reflective gate does not discover it as a module aggregate.
    /// It is persisted through the SAME clean-arch path a real module uses (<c>AddModuleAggregatePersistence</c>
    /// → the closed <c>IRepository&lt;ProbeAggregate, Guid&gt;</c> = <c>EfRepository</c>/<c>RavenRepository</c>).
    /// </para>
    /// </summary>
    public sealed class ProbeAggregate : AggregateRoot<Guid>
    {
        // Materialisation constructor (EF Core / RavenDB). Not for test use.
        private ProbeAggregate()
        {
            Name = string.Empty;
            NameNormalized = string.Empty;
        }

        private ProbeAggregate(
            Guid id, int number, Guid tag, ProbeRating rating, bool active, DateTime whenUtc, string name, long quantity, decimal amount)
        {
            Id = id;
            Number = number;
            Quantity = quantity;
            Amount = amount;
            Tag = tag;
            Rating = rating;
            Active = active;
            WhenUtc = whenUtc;
            Name = name;
            NameNormalized = name.ToUpperInvariant();
        }

        public int Number { get; private set; }

        /// <summary>
        /// A <c>long</c> column. <c>IRepository</c> already NAMES long in the safe set; what was missing was the
        /// EVIDENCE, so the conformance suite drives equality AND a range predicate over it on both providers.
        /// </summary>
        public long Quantity { get; private set; }

        /// <summary>
        /// A <c>decimal</c> column, deliberately ROUND-TRIP ONLY. Decimal sits OUTSIDE the FindAsync contract's
        /// safe set - the providers' numeric handling is not asserted to agree - so the suite proves the value
        /// survives storage and never puts it in a predicate. Money is stored and rendered, never filtered.
        /// </summary>
        public decimal Amount { get; private set; }

        public Guid Tag { get; private set; }

        public ProbeRating Rating { get; private set; }

        public bool Active { get; private set; }

        public DateTime WhenUtc { get; private set; }

        /// <summary>The raw, mixed-case name — comparing it with a bare <c>==</c> DIVERGES across providers.</summary>
        public string Name { get; private set; }

        /// <summary>
        /// The normalized twin of <see cref="Name"/> (upper-invariant). Comparing it against a
        /// SIMILARLY-normalized argument AGREES across providers.
        /// </summary>
        public string NameNormalized { get; private set; }

        public static ProbeAggregate Create(
            int number, Guid tag, ProbeRating rating, bool active, DateTime whenUtc, string name,
            long quantity = 0L, decimal amount = 0m) =>
            new(Guid.NewGuid(), number, tag, rating, active, whenUtc, name, quantity, amount);
    }

    /// <summary>
    /// The probe aggregate's own EF Core context — the relational half the shared helper composes on a relational
    /// install. Every column maps by convention (enum → int, bool, <c>Guid</c>, <c>DateTime</c>, string), so
    /// <c>EnsureCreated</c> builds the schema with no <c>EntityBuilder</c>. The base context ignores the
    /// aggregate's domain events and wires the concurrency token.
    /// </summary>
    public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options, ITenantContext tenantContext)
        : StommeDbContext(options, tenantContext)
    {
        public DbSet<ProbeAggregate> Probes => Set<ProbeAggregate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeAggregate>(probe =>
            {
                probe.ToTable("FindAsyncProbes");
                probe.HasKey(p => p.Id);
                probe.Property(p => p.Name).IsRequired();
                probe.Property(p => p.NameNormalized).IsRequired();
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
