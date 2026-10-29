#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Mcp.Domain
{
    /// <summary>
    /// One line of the MCP audit ledger: who asked this install's MCP endpoint for what, and how it ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A durable row, not a log line.</b> Auditing through the logger would not do - Stomme's <c>LogManager</c> DROPS
    /// entries below <c>Logging:LogLevel:Default</c> and diverts to the file logger under load, so such an "audit" could be
    /// switched off in configuration and thinned out by traffic. Both verbs (<c>tools/call</c> and <c>resources/read</c>) are written here, by the catalog, on every outcome including a refusal.
    /// </para>
    /// <para>
    /// <b>The arguments are NOT kept - their SHA-256 and their length are.</b> Tool arguments are whatever a caller
    /// chose to send, and an audit table is read by more people than the tool's own data would be. The hash still
    /// answers the questions an audit is asked: were these two calls the same call, and is this the call that was
    /// reported.
    /// </para>
    /// <para>
    /// Immutable: there is no mutator. Written by application code, never by a domain-event handler. Removed only
    /// by the per-site retention purge.
    /// </para>
    /// </remarks>
    public sealed class McpAuditEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumSubjectLength = 512;

        private DateTime _occurredOnUtc;

        // Materialisation constructor. Not for application use.
        private McpAuditEntryAggregate()
        {
            TenantId = null!;
            Subject = string.Empty;
            ArgumentsSha256 = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The Stomme user who called. The endpoint admits nobody else, so there is always one.</summary>
        public int UserId { get; private set; }

        public McpAuditVerb Verb { get; private set; }

        /// <summary>The tool name or the resource URI that was asked for - as ASKED, even when there is no such thing.</summary>
        public string Subject { get; private set; }

        public McpAuditOutcome Outcome { get; private set; }

        /// <summary>Lower-case hex SHA-256 of the UTF-8 arguments; of the empty string when there were none.</summary>
        public string ArgumentsSha256 { get; private set; }

        /// <summary>The length of the arguments in characters.</summary>
        public int ArgumentsLength { get; private set; }

        public DateTime OccurredOnUtc
        {
            get => McpInstant.Stamp(_occurredOnUtc);
            private set => _occurredOnUtc = value;
        }

        public static McpAuditEntryAggregate Record(
            Guid id, TenantId tenantId, int siteId, int userId, McpAuditVerb verb, string subject, McpAuditOutcome outcome, string? arguments, DateTime nowUtc)
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

            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "An audit entry names the user who called.");
            }

            if (!Enum.IsDefined(verb))
            {
                throw new ArgumentOutOfRangeException(nameof(verb));
            }

            if (!Enum.IsDefined(outcome))
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            // Cut, never refused: the subject is caller-supplied, and an over-long one must not be a way to make a
            // DENIED call leave no trace.
            var asked = subject ?? string.Empty;
            if (asked.Length > MaximumSubjectLength)
            {
                asked = asked.Substring(0, MaximumSubjectLength);
            }

            var text = arguments ?? string.Empty;
            return new McpAuditEntryAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                Verb = verb,
                Subject = asked,
                Outcome = outcome,
                ArgumentsSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                ArgumentsLength = text.Length,
                OccurredOnUtc = McpInstant.Require(nowUtc, nameof(nowUtc)),
            };
        }
    }
}
