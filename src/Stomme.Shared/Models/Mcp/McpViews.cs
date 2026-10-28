#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Models.Mcp
{
    /// <summary>
    /// What the admin UI is told about one EXTERNAL MCP server this site has registered (BL-80 / D101 item 7).
    /// </summary>
    /// <remarks>
    /// <b>It carries no secret and no way to ask for one.</b> <see cref="HasApiKey"/> says only THAT a key is stored; the
    /// key lives in the per-site secret store under a name derived from the row's id, and no read path anywhere returns
    /// it. The aggregate itself never holds it either - this view is not the narrower half of something wider.
    /// </remarks>
    public sealed class McpServerRegistrationView
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>The absolute http(s) endpoint. It cannot be edited: a different address is a different registration.</summary>
        public string Endpoint { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public bool HasApiKey { get; set; }

        /// <summary><c>Unverified</c> until a refresh has reached the server; <c>Verified</c> afterwards.</summary>
        public string TrustState { get; set; } = string.Empty;

        /// <summary>The per-minute cap an operator set, or null for none.</summary>
        public int? MaxRequestsPerMinute { get; set; }

        public DateTime RegisteredOnUtc { get; set; }

        public DateTime? LastRefreshedOnUtc { get; set; }

        /// <summary>What the server said it offers, at the last refresh - already bounded when it was stored.</summary>
        public IReadOnlyList<McpDiscoveredToolView> Tools { get; set; } = Array.Empty<McpDiscoveredToolView>();
    }

    public sealed class McpDiscoveredToolView
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }
    }

    /// <summary>What an operator sends to register a server. The endpoint is checked by the domain, not here.</summary>
    public sealed class McpServerRegistrationRequest
    {
        public string Name { get; set; } = string.Empty;

        public string Endpoint { get; set; } = string.Empty;

        public int? MaxRequestsPerMinute { get; set; }
    }

    /// <summary>The API key an operator is SETTING. A null or blank value clears it.</summary>
    public sealed class McpApiKeyRequest
    {
        public string? ApiKey { get; set; }
    }

    /// <summary>One entry of the site's MCP audit ledger, as the admin page shows it.</summary>
    /// <remarks>
    /// The arguments are NOT here and are not stored: the ledger keeps their SHA-256 and length, which is enough to say
    /// "these two were the same call" without the ledger becoming a copy of everything anyone sent.
    /// </remarks>
    public sealed class McpAuditEntryView
    {
        public Guid Id { get; set; }

        public int UserId { get; set; }

        /// <summary><c>ToolCall</c> or <c>ResourceRead</c>.</summary>
        public string Verb { get; set; } = string.Empty;

        /// <summary>The tool name or resource URI. Cut, never refused - a denied call must still leave a trace.</summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary><c>Succeeded</c>, <c>Failed</c>, <c>Denied</c> or <c>ConfirmDenied</c>.</summary>
        public string Outcome { get; set; } = string.Empty;

        public int ArgumentsLength { get; set; }

        public string? ArgumentsSha256 { get; set; }

        public DateTime OccurredOnUtc { get; set; }
    }

    /// <summary>One descriptor THIS install publishes, as the calling user may see it.</summary>
    public sealed class McpPublishedItemView
    {
        /// <summary><c>tool</c> or <c>resource</c>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The tool name, or the resource URI (or URI template).</summary>
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>For a tool: the approval this SITE applies, after any <c>mcp.tool.{name}.approval</c> override.</summary>
        public string? Approval { get; set; }
    }
}
