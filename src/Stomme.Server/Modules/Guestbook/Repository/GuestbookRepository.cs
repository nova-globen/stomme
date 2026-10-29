using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Guestbook.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Guestbook.Persistence
{
    /// <summary>
    /// The Guestbook module's closed repository — now PROVIDER-NEUTRAL by construction. Every member,
    /// including the by-module read, DELEGATES to the CLOSED <see cref="IRepository{TAggregate, TId}"/> the
    /// module registered through <c>AddModuleAggregatePersistence</c> (an <c>EfRepository</c> over the host's
    /// shared <c>ModuleAggregatesDbContext</c> on a relational install, a <c>RavenRepository</c> over the
    /// install's document store on a RavenDB install). It holds NO provider-specific code and NO <c>DbContext</c>,
    /// so a single implementation runs on BOTH providers and the RavenDB twin this module used to ship
    /// (<c>RavenGuestbookRepository</c>) and the provider branch in <c>ServerStartup</c> are DELETED.
    /// <para>
    /// <see cref="GetByModuleAsync"/> filters through <see cref="IRepository{TAggregate, TId}.FindAsync"/> on an
    /// <c>int</c> equality (<c>ModuleId</c>) — a SAFE predicate under FindAsync's contract (equality on a
    /// non-string scalar translates identically on EF and RavenDB; it does not touch the <c>.ToLower()</c>/
    /// <c>==</c> case-sensitivity divergences). The filter runs in the database; the
    /// newest-first ORDER is applied in memory over the already-filtered, single-module result set (small by
    /// construction — one placed guestbook), which keeps the neutral query to its one safe job.
    /// </para>
    /// </summary>
    public sealed class GuestbookRepository(IRepository<GuestbookEntryAggregate, Guid> inner)
        : IGuestbookRepository
    {
        public Task<GuestbookEntryAggregate> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<GuestbookEntryAggregate>> GetAllAsync(CancellationToken ct = default) =>
            inner.GetAllAsync(ct);

        public Task AddAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default) =>
            inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default) =>
            inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default) =>
            inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<GuestbookEntryAggregate>> FindAsync(
            Expression<Func<GuestbookEntryAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        // The ModuleId filter (an int equality — a SAFE FindAsync predicate) runs in the database; the
        // newest-first order is applied in memory over the filtered single-module set.
        public async Task<IReadOnlyList<GuestbookEntryAggregate>> GetByModuleAsync(int moduleId, CancellationToken ct = default)
        {
            var entries = await inner.FindAsync(entry => entry.ModuleId == moduleId, ct);
            return entries
                .OrderByDescending(entry => entry.SignedOnUtc)
                .ToList();
        }
    }
}
