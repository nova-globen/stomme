#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Controllers;
using Stomme.Modules.Mcp.Services;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Mcp.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IMcpService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not optional, and leaving it out is a page that cannot render.</b> The framework scans
    /// <c>IClientService</c> only in the WebAssembly client's <c>Program</c>; the server scans <c>IService</c> and
    /// <c>ITransientService</c>. So on a site whose Runtime is Server - the DEFAULT for a fresh install and for every
    /// site the admin UI creates - a control injecting <c>IMcpService</c> with no server registration has nothing to
    /// resolve. Every other in-tree module ships this twin (HtmlText, Guestbook, Notifications); the first version of
    /// this module did not, and no HTTP or API drive could see it, because the controller has its own dependencies.
    /// </para>
    /// <para>
    /// <b>It gates itself, because it is NOT behind the controller.</b> On the HTTP path the keys are decided by
    /// <c>[RequiresPermissionKey]</c> before an action runs; a control resolving this type in-process never passes
    /// through that, so the same keys are asked of the same <see cref="IAccessGuard"/> here. A denial THROWS rather
    /// than answering an empty list: an empty list says "there is nothing", which is a different statement from "this
    /// is not yours", and on a write it would be a silent no-op.
    /// </para>
    /// <para>
    /// <b>With a half switched off the corresponding member throws too.</b> The client half's service and the server
    /// half's catalog are simply absent from the container then - the same absence the controller's route constraints
    /// ask about - so this type takes them as optional and says so, rather than failing to compose and taking the
    /// whole host with it.
    /// </para>
    /// </remarks>
    public sealed class ServerMcpService : IMcpService, ITransientService
    {
        private readonly IMcpAuditRepository _audit;
        private readonly SiteState _siteState;
        private readonly IAccessGuard? _guard;
        private readonly McpRegistrationService? _registrations;
        private readonly McpServerCatalog? _catalog;

        public ServerMcpService(
            IMcpAuditRepository audit,
            SiteState siteState,
            IAccessGuard? guard = null,
            McpRegistrationService? registrations = null,
            McpServerCatalog? catalog = null)
        {
            _audit = audit;
            _siteState = siteState;
            _guard = guard;
            _registrations = registrations;
            _catalog = catalog;
        }

        private int SiteId => _siteState.Alias?.SiteId ?? -1;

        public async Task<List<McpServerRegistrationView>> GetServersAsync()
        {
            var registrations = await ManageAsync();
            return (await registrations.ListAsync()).Select(McpViewMapper.ToView).ToList();
        }

        public async Task<McpServerRegistrationView> RegisterServerAsync(McpServerRegistrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var registrations = await ManageAsync();
            var registered = await registrations.RegisterAsync(request.Name, request.Endpoint, request.MaxRequestsPerMinute)
                ?? throw new InvalidOperationException("This site already has a server registered under that name.");

            return McpViewMapper.ToView(registered);
        }

        public async Task RefreshServerAsync(Guid id) => Done(await (await ManageAsync()).RefreshAsync(id));

        public async Task SetServerEnabledAsync(Guid id, bool enabled)
        {
            var registrations = await ManageAsync();
            Done(enabled ? await registrations.EnableAsync(id) : await registrations.DisableAsync(id));
        }

        public async Task RemoveServerAsync(Guid id) => Done(await (await ManageAsync()).RemoveAsync(id));

        public async Task SetApiKeyAsync(Guid id, string apiKey) => Done(await (await ManageAsync()).SetApiKeyAsync(id, apiKey));

        public async Task<List<McpPublishedItemView>> GetPublishedAsync()
        {
            await RequireAsync(McpPermissions.View);
            if (_catalog is null)
            {
                // The server half is off: this install publishes nothing, which is an empty list and not an error.
                return new List<McpPublishedItemView>();
            }

            var items = (await _catalog.GetResourcesAsync()).Select(McpViewMapper.ToView).ToList();
            foreach (var tool in await _catalog.GetToolsAsync())
            {
                items.Add(McpViewMapper.ToView(tool, await _catalog.EffectiveApprovalAsync(tool, SiteId)));
            }

            return items;
        }

        public async Task<List<McpAuditEntryView>> GetAuditAsync(int days)
        {
            await RequireAsync(McpPermissions.ViewAudit);
            if (SiteId <= 0)
            {
                return new List<McpAuditEntryView>();
            }

            var window = days is > 0 and <= 365 ? days : 7;
            var entries = await _audit.ListSinceAsync(SiteId, DateTime.UtcNow.AddDays(-window));
            return entries.OrderByDescending(entry => entry.OccurredOnUtc).Select(McpViewMapper.ToView).ToList();
        }

        private async Task<McpRegistrationService> ManageAsync()
        {
            await RequireAsync(McpPermissions.Manage);
            return _registrations
                ?? throw new InvalidOperationException("The MCP client half is not enabled on this install (Mcp:Enabled).");
        }

        /// <summary>
        /// The same key the controller's <c>[RequiresPermissionKey]</c> asks for, asked of the same guard. No guard
        /// means DENIED - the fail-closed shape the catalog already uses, not a service that works when the evaluator
        /// is missing.
        /// </summary>
        private async Task RequireAsync(string key)
        {
            if (_guard is null || !await _guard.IsAllowedAsync(null, key))
            {
                throw new AccessDeniedException($"'{key}' is required.");
            }
        }

        private static void Done(McpRegistrationResult result)
        {
            if (result.Outcome == McpRegistrationOutcome.Done)
            {
                return;
            }

            // NotFound stays one answer for "no such registration" and "another site's" here too.
            throw result.Outcome == McpRegistrationOutcome.NotFound
                ? new InvalidOperationException("No such MCP server registration.")
                : new InvalidOperationException(result.Reason ?? "The operation failed.");
        }
    }
}
