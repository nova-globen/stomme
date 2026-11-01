#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Models.Mcp
{
    /// <summary>
    /// What the admin UI is told about one EXTERNAL MCP server this site has registered.
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

    /// <summary>
    /// Which halves of MCP this install has on, and - for an operator (<c>mcp:manage</c> or <c>mcp:view-audit</c>) - the
    /// contributions it REJECTED at startup because two contributors published one name. The admin page reads it to say
    /// what is off and why a name is missing, instead of offering what cannot work.
    /// </summary>
    public sealed class McpStatusView
    {
        /// <summary>The client half - this site's external servers (<c>Mcp:Enabled</c>).</summary>
        public bool ClientEnabled { get; set; }

        /// <summary>The server half - what this install publishes to MCP clients (<c>Mcp:Server:Enabled</c>).</summary>
        public bool ServerEnabled { get; set; }

        /// <summary>The endpoint's path on this site (<c>Mcp:Path</c>), what an MCP client is given with a personal token.</summary>
        public string Path { get; set; } = "/mcp/v1";

        /// <summary>Empty for a caller who is not an operator, and when the server half is off.</summary>
        public IReadOnlyList<McpConflictView> Conflicts { get; set; } = Array.Empty<McpConflictView>();
    }

    /// <summary>A resource or tool name two contributors published: neither is served.</summary>
    public sealed class McpConflictView
    {
        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public IReadOnlyList<string> Contributors { get; set; } = Array.Empty<string>();

        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// What a personal MCP token may do (D101a). A scope only NARROWS what its user may already do on the site - the
    /// permission rows still decide every resource and tool - and a token holds at least one.
    /// </summary>
    public static class McpTokenScopes
    {
        /// <summary>List and read the resources the user may read.</summary>
        public const string Resources = "resources";

        /// <summary>List and call the tools the user may call.</summary>
        public const string Tools = "tools";

        public static readonly IReadOnlyList<string> All = new[] { Resources, Tools };

        /// <summary>The claim a token's principal carries, once per scope. A signed-in browser session carries none.</summary>
        public const string ClaimType = "stomme:mcp_scope";
    }

    /// <summary>One of the caller's own personal MCP tokens. The secret is never in it: it was shown once, when made.</summary>
    public sealed class McpTokenView
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>The token's first characters, to tell tokens apart (<c>smcp_ab12cd34…</c>).</summary>
        public string Prefix { get; set; } = string.Empty;

        public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();

        public DateTime CreatedOnUtc { get; set; }

        public DateTime ExpiresOnUtc { get; set; }

        public DateTime? LastUsedOnUtc { get; set; }

        public DateTime? RevokedOnUtc { get; set; }
    }

    /// <summary>A token to make: a name, how many days it lives (1 to 365), and what it may do.</summary>
    public sealed class McpTokenRequest
    {
        public string Name { get; set; } = string.Empty;

        public int Days { get; set; } = 30;

        public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();
    }

    /// <summary>A token just made: its view, and the secret - shown this once and stored nowhere, only its hash.</summary>
    public sealed class McpTokenCreated
    {
        public McpTokenView Token { get; set; } = new();

        public string Secret { get; set; } = string.Empty;
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
