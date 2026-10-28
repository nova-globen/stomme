#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Mcp.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Mcp.Persistence
{
    /// <summary>
    /// The external-server registrations' closed read surface (D101 items 4 and 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The namespace is deliberate</b> (the Metering, Notifications and DataExport convention):
    /// <c>ModuleStoreRegistrationTests</c> holds every closed store contract under <c>Stomme.Modules.*</c> to a
    /// hand-written EF and RavenDB twin. These have no twin - they delegate to the storage-blind closed
    /// <c>IRepository</c> - and are gated by <c>ModuleAggregateProviderNeutralityTests</c> instead.
    /// </para>
    /// <para>
    /// <b>There is no by-id read that does not name a site.</b> the predecessor platform's five by-id handlers (refresh, enable, disable,
    /// remove, secret) took an id and nothing else, so any row in the tenant answered - and the default install puts
    /// every site in ONE tenant. <see cref="GetForSiteAsync"/> is the only way this module fetches one, and it gives the
    /// same answer - null - for "no such registration" and "another site's registration", or the id space is an oracle.
    /// </para>
    /// <para>
    /// Filters stay inside the <c>FindAsync</c> safe set - an <c>int</c> site id and a <c>bool</c> - with ONE string:
    /// the by-name look-up compares <c>NameNormalized</c>, normalised on BOTH sides with the same function (D49), because
    /// a bare string <c>==</c> is case-sensitive on the relational providers and case-insensitive on RavenDB.
    /// </para>
    /// </remarks>
    public interface IMcpServerRegistrationRepository : IRepository<McpServerRegistrationAggregate, Guid>
    {
        Task<McpServerRegistrationAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<McpServerRegistrationAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>The ENABLED registrations of a site - what the client tool source offers from.</summary>
        Task<IReadOnlyList<McpServerRegistrationAggregate>> ListEnabledAsync(int siteId, CancellationToken ct = default);

        /// <summary>The site's registration with this name, ignoring case and surrounding white space; null when there is none.</summary>
        Task<McpServerRegistrationAggregate?> GetByNameAsync(int siteId, string name, CancellationToken ct = default);
    }

    /// <summary>The audit ledger's closed read surface (D101 item 6).</summary>
    public interface IMcpAuditRepository : IRepository<McpAuditEntryAggregate, Guid>
    {
        Task<IReadOnlyList<McpAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>Entries at or after <paramref name="fromUtc"/> - what the admin page shows.</summary>
        Task<IReadOnlyList<McpAuditEntryAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default);

        /// <summary>Entries older than <paramref name="cutoffUtc"/> - what the retention purge removes.</summary>
        Task<IReadOnlyList<McpAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    /// <summary>The shared delegation to the closed <see cref="IRepository{TAggregate, TId}"/>: no provider code, no <c>DbContext</c>, no RavenDB twin.</summary>
    public abstract class McpRepositoryBase<TAggregate>(IRepository<TAggregate, Guid> inner) : IRepository<TAggregate, Guid>
        where TAggregate : AggregateRoot<Guid>
    {
        protected IRepository<TAggregate, Guid> Inner { get; } = inner;

        public Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default) => Inner.GetAllAsync(ct);

        public Task AddAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<TAggregate>> FindAsync(Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default) =>
            Inner.FindAsync(predicate, ct);

        /// <summary>Refuses a non-UTC instant rather than filtering against a local time the store stored as UTC.</summary>
        protected static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", nameof(value));
    }

    public sealed class McpServerRegistrationRepository(IRepository<McpServerRegistrationAggregate, Guid> inner)
        : McpRepositoryBase<McpServerRegistrationAggregate>(inner), IMcpServerRegistrationRepository
    {
        public async Task<McpServerRegistrationAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default)
        {
            var registration = await Inner.GetByIdAsync(id, ct);
            return registration is null || registration.SiteId != siteId ? null : registration;
        }

        public Task<IReadOnlyList<McpServerRegistrationAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(registration => registration.SiteId == siteId, ct);

        public Task<IReadOnlyList<McpServerRegistrationAggregate>> ListEnabledAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(registration => registration.SiteId == siteId && registration.Enabled == true, ct);

        public async Task<McpServerRegistrationAggregate?> GetByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            var normalized = McpServerRegistrationAggregate.Normalize(name);
            if (normalized.Length == 0)
            {
                return null;
            }

            var matches = await Inner.FindAsync(registration => registration.SiteId == siteId && registration.NameNormalized == normalized, ct);
            return matches.FirstOrDefault();
        }
    }

    public sealed class McpAuditRepository(IRepository<McpAuditEntryAggregate, Guid> inner)
        : McpRepositoryBase<McpAuditEntryAggregate>(inner), IMcpAuditRepository
    {
        public Task<IReadOnlyList<McpAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(entry => entry.SiteId == siteId, ct);

        public Task<IReadOnlyList<McpAuditEntryAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default)
        {
            var from = UtcOnly(fromUtc);
            return Inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc >= from, ct);
        }

        public Task<IReadOnlyList<McpAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoff, ct);
        }
    }
}
