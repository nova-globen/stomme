#nullable enable
using System;

namespace Stomme.Modules.Mcp.Domain
{
    /// <summary>Whether this site has ever successfully talked to the server. Stored as its integer value; never renumber.</summary>
    public enum McpServerTrustState
    {
        /// <summary>Registered, never refreshed. Registration does NOT dial, so every server starts here.</summary>
        Unverified = 0,

        /// <summary>A refresh reached it and it answered with a tool list.</summary>
        Verified = 1,
    }

    /// <summary>Which MCP verb an audit entry is about. Stored as its integer value; never renumber.</summary>
    public enum McpAuditVerb
    {
        /// <summary><c>tools/call</c>.</summary>
        ToolCall = 0,

        /// <summary><c>resources/read</c> - audited like a tool call.</summary>
        ResourceRead = 1,
    }

    /// <summary>How an audited call ended. Stored as its integer value; never renumber.</summary>
    public enum McpAuditOutcome
    {
        Succeeded = 0,

        /// <summary>The handler ran and reported, or threw, an error.</summary>
        Failed = 1,

        /// <summary>A gate refused, or the tool's approval is Deny. The caller was told "unknown".</summary>
        Denied = 2,

        /// <summary>
        /// The tool's approval is Confirm and there is no approval pipeline to ask. Its own outcome, so that the day
        /// one exists the ledger already says which calls were waiting for it.
        /// </summary>
        ConfirmDenied = 3,
    }

    /// <summary>
    /// The MCP domain's UTC rule - the export domain's, for the same measured reason.
    /// </summary>
    /// <remarks>
    /// Inputs are REQUIRED to be <see cref="DateTimeKind.Utc"/>; persisted values are re-stamped UTC on read, because a
    /// relational provider materialises a scalar <see cref="DateTime"/> with no kind while RavenDB and the File store
    /// hand back UTC - and <see cref="DateTime"/> equality ignores the kind, so the divergence passes every naive
    /// round-trip assertion and only shows when something converts the value.
    /// </remarks>
    internal static class McpInstant
    {
        public static DateTime Require(DateTime value, string parameterName) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", parameterName);

        public static DateTime Stamp(DateTime stored) =>
            stored.Kind == DateTimeKind.Utc ? stored : DateTime.SpecifyKind(stored, DateTimeKind.Utc);
    }
}
