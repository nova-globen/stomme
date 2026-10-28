#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.Mcp.Contributions
{
    /// <summary>
    /// The framework's own MCP contribution (D101 item 8): <c>stomme://site</c>, what the CURRENT SITE is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the proof the seam works from inside the framework</b>, and it is deliberately the smallest useful
    /// thing: the site's name, its render mode and its runtime. No <c>search_site</c> in Wave 1 - a search tool is a
    /// read of everything a site holds, and it wants the egress story (BL-97) before it wants an implementation.
    /// </para>
    /// <para>
    /// <b>It is gated on <see cref="McpPermissions.View"/>, and that gate is why it could not be built earlier.</b>
    /// A permission key that no module DECLARES has no seeded rows, so a resource gated on one denies everybody -
    /// correct, and useless. <c>Stomme.Client</c>'s MCP <c>ModuleInfo</c> declares the three keys; this file depends
    /// on that and on nothing else.
    /// </para>
    /// <para>
    /// <b>SCOPED and registered EXPLICITLY, not discovered by the convention.</b> The convention finds contributors
    /// with a public parameterless constructor and registers them as singletons; this one needs the site THIS REQUEST
    /// resolved, so it takes <see cref="SiteState"/> and is registered by the module's own startup - the path D101
    /// reserved for "a contributor with dependencies". Both kinds land in the same registry, which is where a name
    /// published twice is refused: <b>the framework's own contribution is not privileged</b>, and a module publishing
    /// <c>stomme://site</c> stops the host exactly as any other collision would.
    /// </para>
    /// <para>
    /// <b>The site comes from the request, never from a captured value.</b> With no resolved site the read answers
    /// null, which the catalog renders as the same "no such resource" a denial gets - so it is not an oracle either.
    /// </para>
    /// </remarks>
    public sealed class StommeSiteContributor : IMcpContributor, IMcpResourceReader
    {
        public const string SiteUri = "stomme://site";

        private static readonly IReadOnlyList<McpAccessGate> Gate = new[] { McpAccessGate.ForPermission(McpPermissions.View) };

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private readonly SiteState _siteState;
        private readonly ISiteRepository _sites;

        public StommeSiteContributor(SiteState siteState, ISiteRepository sites)
        {
            _siteState = siteState;
            _sites = sites;
        }

        public string ContributorId => "stomme";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(
                SiteUri,
                "Site",
                "The Nova Globen Stomme site this request resolved to: its name, render mode and runtime.",
                "application/json",
                Gate),
        };

        /// <summary>Wave 1 contributes no tool. A framework tool is an action on the install, and D101 ships none.</summary>
        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = Array.Empty<McpToolDescriptor>();

        public IMcpResourceReader? ReaderFor(string uriTemplate) =>
            string.Equals(uriTemplate, SiteUri, StringComparison.Ordinal) ? this : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => null;

        public async Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
        {
            var alias = _siteState.Alias;
            if (alias is null || alias.SiteId <= 0)
            {
                return null;
            }

            var site = await _sites.GetSiteAsync(alias.SiteId);
            if (site is null)
            {
                return null;
            }

            // Three facts about how the site RENDERS, and nothing about who is on it or what it holds. A resource is a
            // read an MCP client may cache and pass on, so what goes into one is chosen, never collected.
            var payload = JsonSerializer.Serialize(
                new SitePayload(site.SiteId, site.Name ?? string.Empty, site.RenderMode ?? string.Empty, site.Runtime ?? string.Empty), Json);

            return new McpResourceContent(uri, "application/json", payload);
        }

        private sealed record SitePayload(int SiteId, string Name, string RenderMode, string Runtime);
    }
}
