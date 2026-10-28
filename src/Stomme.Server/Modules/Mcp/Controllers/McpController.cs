#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Domain;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Mcp.Controllers
{
    /// <summary>
    /// The MCP module's Web API (<c>api/Mcp</c>, BL-80 / D101 item 7): this site's EXTERNAL server registrations, what
    /// THIS install publishes, and the audit ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two switches, and each half is gated by its OWN one - at the ROUTER, not in the action body.</b>
    /// <c>[RequiresPermissionKey]</c> is an authorization POLICY: it runs before any action body, so
    /// <c>if (!Enabled) return NotFound()</c> inside an action is never reached by a caller without the key, and a
    /// config-gated route would answer 403 to everyone instead of "there is no such thing here" (measured in BL-85, and
    /// the reason <see cref="McpClientEnabledAttribute"/> exists). An action constraint makes the ROUTER decide.
    /// </para>
    /// <para>
    /// <b>The site comes from the request, never from the body or the route.</b> Every registration operation goes
    /// through <see cref="McpRegistrationService"/>, whose by-id reads all pass <c>GetForSiteAsync</c> - so a
    /// <c>NotFound</c> here is the same answer for "no such registration" and "another site's", and an id is not an
    /// oracle. That is asserted end to end by <c>McpSiteIsolationTests</c> with both sites in ONE tenant.
    /// </para>
    /// <para>
    /// <b>No class-level <c>[AllowAnonymous]</c></b> - it would short-circuit every key policy on this controller
    /// (<c>PermissionKeyDeclarationGateTests</c> fails the build on it). And no stored API key is ever returned: the
    /// only thing this controller will say about one is whether it exists.
    /// </para>
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class McpController : ModuleControllerBase
    {
        private readonly IMcpAuditRepository _audit;
        private readonly SiteState _siteState;

        public McpController(IMcpAuditRepository audit, SiteState siteState, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _audit = audit;
            _siteState = siteState;
        }

        /// <summary>
        /// The client half, or null when <c>Mcp:Enabled</c> is off. Resolved from the REQUEST's services rather than
        /// taken as an optional constructor parameter: a controller is built by <c>ActivatorUtilities</c>, and a
        /// registration that is simply ABSENT is exactly the state these properties describe. The same resolution is
        /// what <see cref="McpClientEnabledAttribute"/> asks, so the route and the action cannot disagree.
        /// </summary>
        private McpRegistrationService? Registrations => HttpContext.RequestServices.GetService<McpRegistrationService>();

        /// <summary>The server half, or null when <c>Mcp:Server:Enabled</c> is off. See <see cref="Registrations"/>.</summary>
        private McpServerCatalog? Catalog => HttpContext.RequestServices.GetService<McpServerCatalog>();

        private int SiteId => _siteState.Alias?.SiteId ?? -1;

        // ---- the client half: this site's external servers (mcp:manage) ------------------------------------------

        // GET api/Mcp/servers
        [HttpGet("servers")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public async Task<IReadOnlyList<McpServerRegistrationView>> Servers(CancellationToken ct) =>
            (await Registrations!.ListAsync(ct)).Select(McpViewMapper.ToView).ToList();

        // POST api/Mcp/servers
        [HttpPost("servers")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public async Task<IActionResult> Register([FromBody] McpServerRegistrationRequest request, CancellationToken ct)
        {
            if (request is null)
            {
                return BadRequest("A registration is required.");
            }

            try
            {
                // Registering does NOT dial (D101 item 5): the row comes back Unverified with no tools, and only an
                // explicit refresh talks to the server. A dial here would make this endpoint an SSRF oracle.
                var registered = await Registrations!.RegisterAsync(request.Name, request.Endpoint, request.MaxRequestsPerMinute, ct);
                return registered is null
                    ? BadRequest("This site already has a server registered under that name.")
                    : Ok(McpViewMapper.ToView(registered));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The domain's own refusal - a name that is too long, an address that is not an absolute http(s) URL,
                // an address carrying a user name or password. Its message is the operator's to read.
                return BadRequest(ex.Message);
            }
        }

        // POST api/Mcp/servers/{id}/refresh
        [HttpPost("servers/{id:guid}/refresh")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public Task<IActionResult> Refresh(Guid id, CancellationToken ct) =>
            ResultAsync(() => Registrations!.RefreshAsync(id, ct));

        // POST api/Mcp/servers/{id}/enable
        [HttpPost("servers/{id:guid}/enable")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public Task<IActionResult> Enable(Guid id, CancellationToken ct) =>
            ResultAsync(() => Registrations!.EnableAsync(id, ct));

        // POST api/Mcp/servers/{id}/disable
        [HttpPost("servers/{id:guid}/disable")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public Task<IActionResult> Disable(Guid id, CancellationToken ct) =>
            ResultAsync(() => Registrations!.DisableAsync(id, ct));

        // DELETE api/Mcp/servers/{id}
        [HttpDelete("servers/{id:guid}")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public Task<IActionResult> Remove(Guid id, CancellationToken ct) =>
            ResultAsync(() => Registrations!.RemoveAsync(id, ct));

        // PUT api/Mcp/servers/{id}/apikey
        [HttpPut("servers/{id:guid}/apikey")]
        [McpClientEnabled]
        [RequiresPermissionKey(McpPermissions.Manage)]
        public Task<IActionResult> SetApiKey(Guid id, [FromBody] McpApiKeyRequest request, CancellationToken ct) =>
            ResultAsync(() => Registrations!.SetApiKeyAsync(id, request?.ApiKey, ct));

        // ---- the server half: what THIS install publishes (mcp:view) ----------------------------------------------

        // GET api/Mcp/published
        [HttpGet("published")]
        [McpServerEnabled]
        [RequiresPermissionKey(McpPermissions.View)]
        public async Task<IReadOnlyList<McpPublishedItemView>> Published(CancellationToken ct)
        {
            // As the CALLING user sees it: every descriptor here has already passed its own gates for this principal,
            // so the page cannot become a list of what somebody else could use.
            var catalog = Catalog!;
            var items = (await catalog.GetResourcesAsync(ct)).Select(McpViewMapper.ToView).ToList();
            foreach (var tool in await catalog.GetToolsAsync(ct))
            {
                items.Add(McpViewMapper.ToView(tool, await catalog.EffectiveApprovalAsync(tool, SiteId)));
            }

            return items;
        }

        // ---- the ledger (mcp:view-audit) ---------------------------------------------------------------------------

        // GET api/Mcp/audit?days=7
        [HttpGet("audit")]
        [RequiresPermissionKey(McpPermissions.ViewAudit)]
        public async Task<IReadOnlyList<McpAuditEntryView>> Audit(int days, CancellationToken ct)
        {
            // A SEPARATE key from mcp:view, and this is why: these rows are every principal's calls on the site, not
            // the reader's own. See McpPermissions.
            if (SiteId <= 0)
            {
                return Array.Empty<McpAuditEntryView>();
            }

            var window = days is > 0 and <= 365 ? days : 7;
            var entries = await _audit.ListSinceAsync(SiteId, DateTime.UtcNow.AddDays(-window), ct);
            return entries.OrderByDescending(entry => entry.OccurredOnUtc).Select(McpViewMapper.ToView).ToList();
        }

        /// <summary>
        /// The one place a by-id outcome becomes a status code. <c>NotFound</c> is deliberately indistinguishable
        /// between "no such registration" and "another site's" - the service gives one answer and so does this.
        /// </summary>
        private async Task<IActionResult> ResultAsync(Func<Task<McpRegistrationResult>> operation)
        {
            var result = await operation();
            return result.Outcome switch
            {
                McpRegistrationOutcome.Done => Ok(),
                McpRegistrationOutcome.NotFound => NotFound(),

                // A refusal and a failure are told apart, because they mean different things to an operator: one is
                // "this install may not dial that address", the other is "that server did not answer".
                McpRegistrationOutcome.Refused => BadRequest(result.Reason ?? "The destination is not permitted."),
                _ => BadRequest(result.Reason ?? "The server could not be reached."),
            };
        }
    }

    /// <summary>
    /// The CLIENT half's routes exist only when <c>Mcp:Enabled</c> is on - decided by the ROUTER, so an install that
    /// has not turned it on answers 404 rather than 403. See the controller remarks.
    /// </summary>
    public sealed class McpClientEnabledAttribute : ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(RouteContext routeContext, ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(McpRegistrationService)) is not null;
    }

    /// <summary>The SERVER half's routes exist only when <c>Mcp:Server:Enabled</c> is on.</summary>
    public sealed class McpServerEnabledAttribute : ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(RouteContext routeContext, ActionDescriptor action) =>
            routeContext.HttpContext.RequestServices.GetService(typeof(McpServerCatalog)) is not null;
    }

    /// <summary>Aggregate to view. Nothing here reads a secret, and there is no path from a view back to one.</summary>
    internal static class McpViewMapper
    {
        public static McpServerRegistrationView ToView(McpServerRegistrationAggregate registration) => new()
        {
            Id = registration.Id,
            Name = registration.Name,
            Endpoint = registration.Transport.Value,
            Enabled = registration.Enabled,
            HasApiKey = registration.HasApiKey,
            TrustState = registration.TrustState.ToString(),
            MaxRequestsPerMinute = registration.Guardrail.MaxRequestsPerMinute,
            RegisteredOnUtc = registration.RegisteredOnUtc,
            LastRefreshedOnUtc = registration.LastRefreshedOnUtc,

            // DiscoveredTools, not ExposedTools: an operator looking at a DISABLED server still needs to see what it
            // offered, or "disable" would look like "forget". The Enabled flag beside it says whether it is offered.
            Tools = registration.DiscoveredTools.Tools()
                .Select(tool => new McpDiscoveredToolView { Name = tool.Name, Description = tool.Description })
                .ToList(),
        };

        public static McpAuditEntryView ToView(McpAuditEntryAggregate entry) => new()
        {
            Id = entry.Id,
            UserId = entry.UserId,
            Verb = entry.Verb.ToString(),
            Subject = entry.Subject,
            Outcome = entry.Outcome.ToString(),
            ArgumentsLength = entry.ArgumentsLength,
            ArgumentsSha256 = entry.ArgumentsSha256,
            OccurredOnUtc = entry.OccurredOnUtc,
        };

        public static McpPublishedItemView ToView(McpResourceDescriptor descriptor) => new()
        {
            Kind = "resource",
            Name = descriptor.Uri,
            Description = descriptor.Description,
        };

        public static McpPublishedItemView ToView(McpToolDescriptor descriptor, McpToolApproval effective) => new()
        {
            Kind = "tool",
            Name = descriptor.Name,
            Description = descriptor.Description,
            Approval = effective.ToString(),
        };
    }
}
