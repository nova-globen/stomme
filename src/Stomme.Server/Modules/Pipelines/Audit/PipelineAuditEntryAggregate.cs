#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Pipelines.Audit
{
    /// <summary>
    /// One line of a site's pipeline activity (BL-229, n8n Enterprise's audit log): who did what to which pipeline, and when -
    /// written by <see cref="PipelineAuditBehavior{TRequest, TResponse}"/> after a Pipelines command succeeds, so every change
    /// made through the module's surface (the editor, the API, MCP) is recorded by construction, not by each handler
    /// remembering to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A durable row, not a log line</b> - as the MCP ledger: the logger drops entries below its level and thins them under
    /// load, so an audit kept there could be configured away.
    /// </para>
    /// <para>
    /// <b>What a change WAS, never what it held.</b> The detail names a folder, a tag, a variable, a table - never a variable's
    /// value or a row's content, which may be secrets or personal data and are read by more people here than there.
    /// </para>
    /// <para>
    /// Immutable: no mutator. Removed only by the per-site retention purge and by erasure after its retention (D112).
    /// </para>
    /// </remarks>
    public sealed class PipelineAuditEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumUserNameLength = 256;

        public const int MaximumActionLength = 128;

        public const int MaximumIdLength = 128;

        public const int MaximumPipelineNameLength = 256;

        public const int MaximumDetailLength = 1000;

        private DateTime _occurredOnUtc;

        // Materialisation constructor. Not for application use.
        private PipelineAuditEntryAggregate()
        {
            TenantId = null!;
            Action = string.Empty;
            DefinitionId = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The Stomme user who did it; 0 when nobody was signed in (a system act).</summary>
        public int UserId { get; private set; }

        /// <summary>Their user name as it was then - the ledger reads without a join, and a renamed user's past stays theirs.</summary>
        public string? UserName { get; private set; }

        public string Action { get; private set; }

        /// <summary>The pipeline it was done to; empty for what concerns the site as a whole.</summary>
        public string DefinitionId { get; private set; }

        public string? PipelineName { get; private set; }

        public string? RunId { get; private set; }

        public string? Detail { get; private set; }

        public DateTime OccurredOnUtc
        {
            get => _occurredOnUtc.Kind == DateTimeKind.Utc ? _occurredOnUtc : DateTime.SpecifyKind(_occurredOnUtc, DateTimeKind.Utc);
            private set => _occurredOnUtc = value;
        }

        public static PipelineAuditEntryAggregate Record(
            Guid id, TenantId tenantId, int siteId, int userId, string? userName, string action, string? definitionId, string? pipelineName,
            string? runId, string? detail, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("An audit entry needs an id.", nameof(id));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An audit entry belongs to a site.");
            }

            if (userId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "A user id is never negative; 0 is nobody signed in.");
            }

            if (string.IsNullOrWhiteSpace(action))
            {
                throw new ArgumentException("An audit entry says what was done.", nameof(action));
            }

            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {nowUtc.Kind}).", nameof(nowUtc));
            }

            // Cut, never refused: what a caller sent must not be a way to make a change leave no trace.
            return new PipelineAuditEntryAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                UserName = Cut(userName, MaximumUserNameLength),
                Action = Cut(action.Trim(), MaximumActionLength)!,
                DefinitionId = Cut(definitionId?.Trim(), MaximumIdLength) ?? string.Empty,
                PipelineName = Cut(pipelineName, MaximumPipelineNameLength),
                RunId = Cut(runId?.Trim(), MaximumIdLength),
                Detail = Cut(detail, MaximumDetailLength),
                OccurredOnUtc = nowUtc,
            };
        }

        private static string? Cut(string? text, int most) =>
            string.IsNullOrEmpty(text) ? null : text.Length <= most ? text : text[..most];
    }
}
