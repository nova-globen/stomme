#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Domain;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>The instant the catalog stamps an audit entry with. A seam so a test can hold time still.</summary>
    public interface IMcpClock
    {
        DateTime UtcNow { get; }
    }

    internal sealed class SystemMcpClock : IMcpClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>
    /// What this install publishes over MCP, as the CURRENT caller may see and use it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard is OPTIONAL and its absence DENIES.</b> <see cref="IAccessGuard"/> is a constructor parameter with a
    /// default of null: a host composed without an evaluator gets a catalog that lists nothing and runs nothing, not a
    /// container that throws on resolve and answers 500. D101 falsified "the catalog fails closed without a guard" on
    /// the predecessor platform for exactly that reason - a REQUIRED dependency that is missing is an error page, and an error page is
    /// not a refusal.
    /// </para>
    /// <para>
    /// <b>Unknown and denied are ONE answer - null</b> - so the catalog is not an oracle for what exists. The ledger
    /// tells them apart for the operator; the caller cannot.
    /// </para>
    /// <para>
    /// <b>Both verbs are audited on every outcome</b>, refusals included, as durable rows
    /// (<see cref="McpAuditEntryAggregate"/>). A failure to WRITE the entry fails the call: a tool that ran and left no
    /// trace is the one thing the ledger exists to rule out, so the entry is written BEFORE the handler runs for
    /// everything that is going to be refused, and a handler's result is not returned until its entry is stored.
    /// </para>
    /// <para>
    /// <b>Approval.</b> <c>Auto</c> runs. <c>Deny</c> answers like an unknown tool. <c>Confirm</c> ALSO denies - there
    /// is no approval pipeline to ask - with its own outcome and ONE loud log line per call naming what is missing. A
    /// site may override a tool's approval with the Site setting <c>mcp.tool.{name}.approval</c>; the override can
    /// tighten anything, and it can never make a tool whose name says it changes something <c>Auto</c>
    /// (<see cref="McpToolNames.IsMutating"/> - the rule the descriptor's own constructor applies). A value that is not
    /// one of the three names is read as <c>Deny</c>: a typo must not be a way to open a tool.
    /// </para>
    /// </remarks>
    public sealed class McpServerCatalog : IMcpServerCatalog
    {
        public const string ApprovalSettingPrefix = "mcp.tool.";
        public const string ApprovalSettingSuffix = ".approval";

        private readonly McpContributorRegistry _registry;
        private readonly IMcpAuditRepository _audit;
        private readonly ICurrentUser _currentUser;
        private readonly ITenantContext _tenantContext;
        private readonly SiteState _siteState;
        private readonly ISettingRepository _settings;
        private readonly IMcpClock _clock;
        private readonly ILogger<McpServerCatalog> _logger;
        private readonly IAccessGuard? _guard;

        public McpServerCatalog(
            McpContributorRegistry registry,
            IMcpAuditRepository audit,
            ICurrentUser currentUser,
            ITenantContext tenantContext,
            SiteState siteState,
            ISettingRepository settings,
            IMcpClock clock,
            ILogger<McpServerCatalog> logger,
            IAccessGuard? guard = null)
        {
            _registry = registry;
            _audit = audit;
            _currentUser = currentUser;
            _tenantContext = tenantContext;
            _siteState = siteState;
            _settings = settings;
            _clock = clock;
            _logger = logger;
            _guard = guard;
        }

        public static string ApprovalSettingName(string toolName) => ApprovalSettingPrefix + toolName + ApprovalSettingSuffix;

        public async Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(CancellationToken ct = default)
        {
            var visible = new List<McpResourceDescriptor>();
            if (Caller() is null || !await SitePublishesAsync(ct))
            {
                return visible;
            }

            foreach (var resource in _registry.Resources.OrderBy(item => item.Descriptor.Uri, StringComparer.Ordinal))
            {
                if (await PassesAsync(resource.Descriptor.Requires, ct))
                {
                    visible.Add(resource.Descriptor);
                }
            }

            return visible;
        }

        public async Task<IReadOnlyList<McpToolDescriptor>> GetToolsAsync(CancellationToken ct = default)
        {
            var visible = new List<McpToolDescriptor>();
            var caller = Caller();
            if (caller is null || !await SitePublishesAsync(ct))
            {
                return visible;
            }

            foreach (var tool in _registry.Tools.OrderBy(item => item.Descriptor.Name, StringComparer.Ordinal))
            {
                // A tool this site has set to Deny is not offered: listing it would advertise something that always
                // answers "unknown". Confirm IS listed - it says what the tool is, and the ledger says who asked.
                if (await PassesAsync(tool.Descriptor.Requires, ct)
                    && await EffectiveApprovalAsync(tool.Descriptor, caller.SiteId) != McpToolApproval.Deny)
                {
                    visible.Add(tool.Descriptor);
                }
            }

            return visible;
        }

        public async Task<McpResourceContent?> ReadResourceAsync(string uri, CancellationToken ct = default)
        {
            var caller = Caller();
            if (caller is null)
            {
                return null;
            }

            if (!await SitePublishesAsync(ct)
                || !_registry.TryGetResource(uri, out var resource, out var reader, out var arguments)
                || !await PassesAsync(resource.Descriptor.Requires, ct))
            {
                await RecordAsync(caller, McpAuditVerb.ResourceRead, uri, McpAuditOutcome.Denied, null, ct);
                return null;
            }

            McpResourceContent? content;
            try
            {
                content = await reader.ReadAsync(uri, arguments, ct);
            }
            catch (OperationCanceledException)
            {
                await RecordAsync(caller, McpAuditVerb.ResourceRead, uri, McpAuditOutcome.Failed, null, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MCP resource {Uri} ({Contributor}) threw while being read.", resource.Descriptor.Uri, resource.Contributor.ContributorId);
                await RecordAsync(caller, McpAuditVerb.ResourceRead, uri, McpAuditOutcome.Failed, null, ct);
                return null;
            }

            await RecordAsync(caller, McpAuditVerb.ResourceRead, uri, content is null ? McpAuditOutcome.Failed : McpAuditOutcome.Succeeded, null, ct);
            return content;
        }

        public async Task<McpToolResult?> InvokeToolAsync(string name, string argumentsJson, CancellationToken ct = default)
        {
            var caller = Caller();
            if (caller is null)
            {
                return null;
            }

            if (!await SitePublishesAsync(ct)
                || !_registry.TryGetTool(name, out var tool, out var invoker)
                || !await PassesAsync(tool.Descriptor.Requires, ct))
            {
                await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.Denied, argumentsJson, ct);
                return null;
            }

            switch (await EffectiveApprovalAsync(tool.Descriptor, caller.SiteId))
            {
                case McpToolApproval.Auto:
                    break;

                case McpToolApproval.Confirm:
                    _logger.LogError(
                        "MCP tool {Tool} requires confirmation and this install has NO approval pipeline, so the call by user {UserId} on site {SiteId} was DENIED. " +
                        "Nothing will ask anyone: a Confirm tool does not run until an approval pipeline exists.",
                        tool.Descriptor.Name, caller.UserId, caller.SiteId);
                    await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.ConfirmDenied, argumentsJson, ct);
                    return null;

                default:
                    await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.Denied, argumentsJson, ct);
                    return null;
            }

            McpToolResult result;
            try
            {
                result = await invoker.InvokeAsync(argumentsJson ?? string.Empty, ct);
            }
            catch (OperationCanceledException)
            {
                // The caller went away - but the handler STARTED, and may have done part of its work. That is an entry,
                // written without the token that has just been cancelled.
                await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.Failed, argumentsJson, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception's text is not returned: it is the handler's, and may carry what the caller must not see.
                _logger.LogError(ex, "MCP tool {Tool} ({Contributor}) threw.", tool.Descriptor.Name, tool.Contributor.ContributorId);
                await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.Failed, argumentsJson, ct);
                return new McpToolResult("{\"error\":\"The tool failed.\"}", isError: true);
            }

            if (result is null)
            {
                await RecordAsync(caller, McpAuditVerb.ToolCall, name, McpAuditOutcome.Failed, argumentsJson, ct);
                return new McpToolResult("{\"error\":\"The tool failed.\"}", isError: true);
            }

            await RecordAsync(caller, McpAuditVerb.ToolCall, name, result.IsError ? McpAuditOutcome.Failed : McpAuditOutcome.Succeeded, argumentsJson, ct);
            return result;
        }

        // ---- the caller -------------------------------------------------------------------------------------------

        private sealed record CatalogCaller(Tenancy.Domain.TenantId TenantId, int SiteId, int UserId);

        /// <summary>
        /// Who is asking and where - from the REQUEST. Null means there is nobody the ledger could name, and then
        /// nothing is listed and nothing runs: a call that cannot be audited is not made.
        /// </summary>
        private CatalogCaller? Caller()
        {
            var siteId = _siteState.Alias?.SiteId ?? -1;
            var tenantId = _tenantContext.CurrentTenantId;
            if (siteId <= 0 || tenantId is null || !_currentUser.IsAuthenticated || _currentUser.UserId is not > 0)
            {
                return null;
            }

            return new CatalogCaller(tenantId, siteId, _currentUser.UserId.Value);
        }

        /// <summary>
        /// Whether this install publishes over MCP at all right now - the <c>mcp.server</c> feature.
        /// </summary>
        /// <remarks>
        /// <b>It is the RUNTIME half of the switch, not a second copy of it.</b> <c>Mcp:Server:Enabled</c> is read
        /// while the container is composed and decides whether the endpoint EXISTS - turning it off needs a restart,
        /// and a route cannot be un-mapped per request. This one is decided per call, so publishing can be stopped
        /// immediately. Its scope is the TENANT (that is what a flag row is keyed on), so on a one-tenant install it
        /// is install-wide; the per-SITE question is the permission keys'. Asked through the same
        /// <see cref="IAccessGuard"/> as every gate, so with no evaluator it denies like everything else here.
        /// </remarks>
        private async Task<bool> SitePublishesAsync(CancellationToken ct) =>
            _guard is not null && await _guard.IsAllowedAsync(FeatureCatalog.McpServer, null, ct);

        private async Task<bool> PassesAsync(IReadOnlyList<McpAccessGate> gates, CancellationToken ct)
        {
            // No evaluator, no access - and an empty gate list cannot be constructed, so "vacuously true" is not reachable.
            if (_guard is null || gates.Count == 0)
            {
                return false;
            }

            foreach (var gate in gates)
            {
                if (!await _guard.IsAllowedAsync(gate.Feature, gate.Permission, ct))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The approval THIS SITE applies to <paramref name="tool"/>, after any <c>mcp.tool.{name}.approval</c> override.
        /// </summary>
        /// <remarks>
        /// Public because the admin page must show an operator what their override actually did - and the precedence
        /// rule (Deny is the contributor's word; a site may tighten but never open; an unrecognised value reads as Deny)
        /// belongs here and nowhere else. A page that re-read the setting itself would be a second copy of that rule.
        /// </remarks>
        public async Task<McpToolApproval> EffectiveApprovalAsync(McpToolDescriptor tool, int siteId)
        {
            // Deny is the contributor saying "this never runs". A site can tighten a tool; it cannot open that one.
            if (tool.Approval == McpToolApproval.Deny)
            {
                return McpToolApproval.Deny;
            }

            var setting = await _settings.GetSettingAsync(EntityNames.Site, siteId, ApprovalSettingName(tool.Name));
            var value = setting?.SettingValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                return tool.Approval;
            }

            if (!Enum.TryParse<McpToolApproval>(value.Trim(), ignoreCase: true, out var overridden)
                || !Enum.IsDefined(overridden)
                || int.TryParse(value.Trim(), out _))
            {
                _logger.LogError("The site setting {Setting} on site {SiteId} is '{Value}', which is not Auto, Confirm or Deny. It is read as Deny.",
                    ApprovalSettingName(tool.Name), siteId, value);
                return McpToolApproval.Deny;
            }

            if (overridden == McpToolApproval.Auto && McpToolNames.IsMutating(tool.Name))
            {
                _logger.LogError("The site setting {Setting} on site {SiteId} asks for Auto on a tool whose name says it changes something. It is ignored.",
                    ApprovalSettingName(tool.Name), siteId);
                return tool.Approval;
            }

            return overridden;
        }

        private Task RecordAsync(CatalogCaller caller, McpAuditVerb verb, string? subject, McpAuditOutcome outcome, string? arguments, CancellationToken ct) =>
            _audit.AddAsync(
                McpAuditEntryAggregate.Record(Guid.NewGuid(), caller.TenantId, caller.SiteId, caller.UserId, verb, subject ?? string.Empty, outcome, arguments, _clock.UtcNow), ct);
    }
}
