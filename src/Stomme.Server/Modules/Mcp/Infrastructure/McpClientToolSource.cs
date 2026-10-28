#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Shared;

namespace Stomme.Modules.Mcp.Infrastructure
{
    /// <summary>
    /// What the EXTERNAL servers this SITE has registered offer, as the site last discovered it (D101 item 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>List-only in Wave 1</b>, and list-only means it: there is no way through this type to CALL an external tool.
    /// Calling one is an egress with a guard of its own (BL-97), and a source that could both list and invoke would be
    /// the place that guard was forgotten.
    /// </para>
    /// <para>
    /// <b>It reads nothing live.</b> Every entry came from a refresh an operator asked for, already bounded by
    /// <c>DiscoveredToolSet</c>. So listing cannot dial anything, and a site whose external server has gone away still
    /// answers - with what it last knew, which is what the admin page should show.
    /// </para>
    /// <para>
    /// <b>The site comes from the request</b>, like everywhere else in this module. No resolved site means an empty
    /// list, never "every site's" - and <c>ListEnabledAsync</c> is filtered on the site id in the store, not in memory
    /// after fetching the tenant.
    /// </para>
    /// </remarks>
    public sealed class McpClientToolSource : IMcpClientToolSource
    {
        private readonly IMcpServerRegistrationRepository _registrations;
        private readonly SiteState _siteState;

        public McpClientToolSource(IMcpServerRegistrationRepository registrations, SiteState siteState)
        {
            _registrations = registrations;
            _siteState = siteState;
        }

        public async Task<IReadOnlyList<McpClientToolDescriptor>> GetAvailableToolsAsync(CancellationToken ct = default)
        {
            var descriptors = new List<McpClientToolDescriptor>();
            var siteId = _siteState.Alias?.SiteId ?? -1;
            if (siteId <= 0)
            {
                return descriptors;
            }

            foreach (var registration in await _registrations.ListEnabledAsync(siteId, ct))
            {
                // ExposedTools(), not DiscoveredTools: a disabled server offers nothing, and the aggregate is where
                // that rule lives.
                //
                // MEASURED: swapping this for DiscoveredTools.Tools() changes NOTHING observable, because
                // ListEnabledAsync already filtered on Enabled in the STORE - an equivalent mutant, recorded rather
                // than papered over. Both layers stay, and neither is redundant in the sense that invites deleting it:
                // the store filter is what stops a disabled server's rows being read at all, and ExposedTools() is
                // where the RULE lives, for every future caller that reaches a registration some other way. The
                // aggregate half is asserted directly in McpSiteIsolationTests.
                foreach (var tool in registration.ExposedTools())
                {
                    descriptors.Add(new McpClientToolDescriptor(
                        registration.Id, registration.Name, tool.Name, tool.Description, tool.ParametersJsonSchema));
                }
            }

            return descriptors;
        }
    }
}
