#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Pipelines.Audit
{
    /// <summary>The site's pipeline activity ledger (<see cref="PipelineAuditEntryAggregate"/>), on the storage-blind module seam.</summary>
    public interface IPipelineAuditRepository
    {
        Task AddAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default);

        Task DeleteAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default);

        Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);

        Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default);
    }

    // Filters on integers and an instant only - the provider-neutral part of FindAsync (D78); a pipeline's lines are picked out
    // of the site's in memory, so no string comparison is left to a provider to decide.
    public sealed class PipelineAuditRepository(IRepository<PipelineAuditEntryAggregate, Guid> inner) : IPipelineAuditRepository
    {
        public Task AddAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default) => inner.AddAsync(entry, ct);

        public Task DeleteAsync(PipelineAuditEntryAggregate entry, CancellationToken ct = default) => inner.DeleteAsync(entry, ct);

        public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            inner.FindAsync(entry => entry.SiteId == siteId, ct);

        public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = cutoffUtc.Kind == DateTimeKind.Utc ? cutoffUtc : throw new ArgumentException("The cutoff must be a UTC DateTime.", nameof(cutoffUtc));
            return inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoff, ct);
        }

        public Task<IReadOnlyList<PipelineAuditEntryAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            inner.FindAsync(entry => entry.SiteId == siteId && entry.UserId == userId, ct);
    }
}
