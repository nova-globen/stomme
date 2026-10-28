#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Mcp.Domain
{
    /// <summary>
    /// An EXTERNAL MCP server one SITE has registered, and what it last said it offers (BL-80, D101 items 4 and 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It belongs to a SITE, not only to a tenant.</b> the predecessor platform's registration was tenant-owned and nothing else, its
    /// five by-id handlers reached any row in the tenant, and the default install puts EVERY site in one tenant - so one
    /// site's administrator could enable, refresh or read the secret of another's. <see cref="SiteId"/> sits beside
    /// <see cref="ITenantOwned"/>, and the repository gives ONE answer for "no such registration" and "another site's".
    /// </para>
    /// <para>
    /// <b>Storage-blind</b> (D79): scalars and string value objects only, and every computed member is a METHOD - both
    /// document resolvers serialise read-only properties, so a computed property would be written into the document.
    /// </para>
    /// <para>
    /// <b>The API key is never here.</b> The row records only THAT a key was stored; the key lives in the per-site secret
    /// store under a name derived from this row's id (<see cref="ApiKeySecretName"/>). the predecessor platform took the reference from
    /// the caller, which let one registration point at another feature's secret.
    /// </para>
    /// <para>
    /// <b>There is no way to change the endpoint</b>, on purpose. <see cref="TrustState"/> and the discovered tools are
    /// statements about the server AT THAT ADDRESS; a mutator would carry "Verified" and a tool list over to a server
    /// nobody has talked to. A different address is a different registration.
    /// </para>
    /// <para>
    /// Nothing raises a domain event: the audit trail is written by application code, never by a handler (D40).
    /// </para>
    /// </remarks>
    public sealed class McpServerRegistrationAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumNameLength = 100;

        private DateTime _registeredOnUtc;
        private DateTime? _lastRefreshedOnUtc;

        // Materialisation constructor. Not for application use.
        private McpServerRegistrationAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            Transport = null!;
            Guardrail = McpGuardrail.Default;
            DiscoveredTools = DiscoveredToolSet.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The name as the operator typed it.</summary>
        public string Name { get; private set; }

        /// <summary>
        /// <see cref="Name"/> upper-cased invariantly - what a by-name look-up compares, on BOTH sides (D49): a bare
        /// string <c>==</c> is case-sensitive on the relational providers and case-insensitive on RavenDB.
        /// </summary>
        public string NameNormalized { get; private set; }

        public McpServerTransport Transport { get; private set; }

        public McpGuardrail Guardrail { get; private set; }

        /// <summary>True when an API key is stored for this server. The key itself is in the site secret store.</summary>
        public bool HasApiKey { get; private set; }

        public bool Enabled { get; private set; }

        public McpServerTrustState TrustState { get; private set; }

        public DiscoveredToolSet DiscoveredTools { get; private set; }

        public DateTime RegisteredOnUtc
        {
            get => McpInstant.Stamp(_registeredOnUtc);
            private set => _registeredOnUtc = value;
        }

        /// <summary>When a refresh last reached the server; null until one has.</summary>
        public DateTime? LastRefreshedOnUtc
        {
            get => _lastRefreshedOnUtc is { } value ? McpInstant.Stamp(value) : null;
            private set => _lastRefreshedOnUtc = value;
        }

        /// <summary>The tools this site may be offered from this server: none while it is disabled. A method - see the type remarks.</summary>
        public IReadOnlyList<DiscoveredTool> ExposedTools() => Enabled ? DiscoveredTools.Tools() : Array.Empty<DiscoveredTool>();

        /// <summary>The name of this server's API key in the per-site secret store. Derived, never supplied. A method - see the type remarks.</summary>
        public string ApiKeySecretName() => "mcp.server." + Id.ToString("N") + ".apikey";

        public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>Registers a server. It does NOT dial it: a new registration is Unverified with no tools (D101 item 5).</summary>
        public static McpServerRegistrationAggregate Register(
            Guid id, TenantId tenantId, int siteId, string name, McpServerTransport transport, McpGuardrail? guardrail, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(transport);

            if (id == Guid.Empty)
            {
                throw new ArgumentException("A registration needs an id.", nameof(id));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A registration belongs to a site.");
            }

            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaximumNameLength)
            {
                throw new ArgumentException($"A server name is 1 to {MaximumNameLength} characters.", nameof(name));
            }

            return new McpServerRegistrationAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Name = trimmed,
                NameNormalized = Normalize(trimmed),
                Transport = transport,
                Guardrail = guardrail ?? McpGuardrail.Default,
                HasApiKey = false,
                Enabled = true,
                TrustState = McpServerTrustState.Unverified,
                DiscoveredTools = DiscoveredToolSet.Empty,
                RegisteredOnUtc = McpInstant.Require(nowUtc, nameof(nowUtc)),
            };
        }

        /// <summary>Records what a refresh found. The server answered, so it is Verified from here on.</summary>
        public void ReplaceDiscoveredTools(DiscoveredToolSet tools, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tools);
            DiscoveredTools = tools;
            TrustState = McpServerTrustState.Verified;
            LastRefreshedOnUtc = McpInstant.Require(nowUtc, nameof(nowUtc));
        }

        public void SetGuardrail(McpGuardrail guardrail) => Guardrail = guardrail ?? throw new ArgumentNullException(nameof(guardrail));

        /// <summary>Records that a key was stored, or removed. The caller has ALREADY written the secret store.</summary>
        public void MarkApiKey(bool present) => HasApiKey = present;

        public void Disable() => Enabled = false;

        public void Enable() => Enabled = true;
    }
}
