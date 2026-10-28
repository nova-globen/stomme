#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>What a by-id operation did. <see cref="NotFound"/> is the ONE answer for missing and for another site's.</summary>
    public enum McpRegistrationOutcome
    {
        Done = 0,
        NotFound = 1,
        Refused = 2,
        Failed = 3,
    }

    /// <summary>The answer to a by-id operation: an outcome, and the operator-facing reason when it is not <see cref="McpRegistrationOutcome.Done"/>.</summary>
    public sealed record McpRegistrationResult(McpRegistrationOutcome Outcome, string? Reason = null)
    {
        public static readonly McpRegistrationResult NotFound = new(McpRegistrationOutcome.NotFound);

        public static McpRegistrationResult Done() => new(McpRegistrationOutcome.Done);
    }

    /// <summary>
    /// The site's external-server registrations: register, refresh, enable, disable, remove, and set or clear the API
    /// key (D101 items 4, 5 and 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>EVERY by-id operation here reaches its row through <c>GetForSiteAsync</c>, and there is no other way.</b>
    /// the predecessor platform's five by-id handlers took an id and nothing else, so any row in the TENANT answered - and the default
    /// install puts every site in one tenant, which made this a cross-site escalation on a stock deployment. "No such
    /// registration" and "another site's registration" are the same answer, or the id space is an oracle for what
    /// exists on the site next door.
    /// </para>
    /// <para>
    /// <b>Registering does not dial</b> (D101 item 5): a new row is <c>Unverified</c> with no tools, and the only thing
    /// that talks to the server is the explicit refresh.
    /// </para>
    /// <para>
    /// <b>The key is never in the row.</b> <see cref="SetApiKeyAsync"/> writes the per-site secret store under a name
    /// DERIVED from the row's id and then records only that a key exists; nothing here returns a stored key, and
    /// removing a registration removes its secret first - a secret whose row is gone can never be reached again, so
    /// leaving it would be a value nothing can read and nothing will clean up.
    /// </para>
    /// <para>
    /// <b>One write per execution path</b> (D102 / <c>PerWriteStoreInvariantTests</c>): each method loads its row once
    /// and saves it once, and the register path's duplicate-name check is a read that ends in a return.
    /// </para>
    /// </remarks>
    public sealed class McpRegistrationService
    {
        private readonly IMcpServerRegistrationRepository _registrations;
        private readonly IMcpClientConnector _connector;
        private readonly ISiteSecretStore _secrets;
        private readonly ITenantContext _tenantContext;
        private readonly SiteState _siteState;
        private readonly IMcpClock _clock;
        private readonly ILogger<McpRegistrationService> _logger;

        public McpRegistrationService(
            IMcpServerRegistrationRepository registrations,
            IMcpClientConnector connector,
            ISiteSecretStore secrets,
            ITenantContext tenantContext,
            SiteState siteState,
            IMcpClock clock,
            ILogger<McpRegistrationService> logger)
        {
            _registrations = registrations;
            _connector = connector;
            _secrets = secrets;
            _tenantContext = tenantContext;
            _siteState = siteState;
            _clock = clock;
            _logger = logger;
        }

        /// <summary>The site this REQUEST resolved, or -1. Read fresh every time - never captured (BL-160, D37).</summary>
        private int SiteId => _siteState.Alias?.SiteId ?? -1;

        public async Task<IReadOnlyList<McpServerRegistrationAggregate>> ListAsync(CancellationToken ct = default) =>
            SiteId <= 0 ? Array.Empty<McpServerRegistrationAggregate>() : await _registrations.ListBySiteAsync(SiteId, ct);

        public async Task<McpServerRegistrationAggregate?> GetAsync(Guid id, CancellationToken ct = default) =>
            SiteId <= 0 ? null : await _registrations.GetForSiteAsync(id, SiteId, ct);

        /// <summary>
        /// Registers a server for THIS site. It is not dialled: the row is <c>Unverified</c> with no tools until a
        /// refresh. Returns null when the site already has a registration of that name, or there is no resolved site.
        /// </summary>
        public async Task<McpServerRegistrationAggregate?> RegisterAsync(
            string name, string endpoint, int? maxRequestsPerMinute, CancellationToken ct = default)
        {
            var siteId = SiteId;
            var tenantId = _tenantContext.CurrentTenantId;
            if (siteId <= 0 || tenantId is null)
            {
                return null;
            }

            if (await _registrations.GetByNameAsync(siteId, name, ct) is not null)
            {
                return null;
            }

            var registration = McpServerRegistrationAggregate.Register(
                Guid.NewGuid(), tenantId, siteId, name,
                McpServerTransport.Create(endpoint),
                McpGuardrail.Of(maxRequestsPerMinute),
                _clock.UtcNow);

            await _registrations.AddAsync(registration, ct);
            return registration;
        }

        /// <summary>
        /// Dials the server and records what it offers. THE ONLY method here that makes an outbound connection, and it
        /// leaves the row untouched unless the server answered: a refused or failed refresh must not turn a verified
        /// server into an empty one.
        /// </summary>
        public async Task<McpRegistrationResult> RefreshAsync(Guid id, CancellationToken ct = default)
        {
            var registration = await GetAsync(id, ct);
            if (registration is null)
            {
                return McpRegistrationResult.NotFound;
            }

            var apiKey = await ApiKeyAsync(registration, ct);
            var discovery = await _connector.DiscoverToolsAsync(registration, apiKey, ct);
            if (!discovery.Succeeded)
            {
                return new McpRegistrationResult(
                    discovery.Refused ? McpRegistrationOutcome.Refused : McpRegistrationOutcome.Failed, discovery.Failure);
            }

            registration.ReplaceDiscoveredTools(discovery.Tools!, _clock.UtcNow);
            await _registrations.UpdateAsync(registration, ct);
            return McpRegistrationResult.Done();
        }

        public Task<McpRegistrationResult> EnableAsync(Guid id, CancellationToken ct = default) =>
            MutateAsync(id, registration => registration.Enable(), ct);

        public Task<McpRegistrationResult> DisableAsync(Guid id, CancellationToken ct = default) =>
            MutateAsync(id, registration => registration.Disable(), ct);

        /// <summary>Removes the registration AND its secret. The secret goes first - see the type remarks.</summary>
        public async Task<McpRegistrationResult> RemoveAsync(Guid id, CancellationToken ct = default)
        {
            var registration = await GetAsync(id, ct);
            if (registration is null)
            {
                return McpRegistrationResult.NotFound;
            }

            await _secrets.DeleteAsync(registration.SiteId, registration.ApiKeySecretName(), ct);
            await _registrations.DeleteAsync(registration, ct);
            return McpRegistrationResult.Done();
        }

        /// <summary>
        /// Stores (or, for a null/blank value, removes) this server's API key. The key's NAME is derived from the row's
        /// id and is never supplied by the caller - the predecessor platform took the reference from the request, which let one
        /// registration be pointed at another feature's secret. Nothing reads a key back out to a caller.
        /// </summary>
        public async Task<McpRegistrationResult> SetApiKeyAsync(Guid id, string? apiKey, CancellationToken ct = default)
        {
            var registration = await GetAsync(id, ct);
            if (registration is null)
            {
                return McpRegistrationResult.NotFound;
            }

            var present = !string.IsNullOrWhiteSpace(apiKey);
            if (present)
            {
                await _secrets.SetAsync(registration.SiteId, registration.ApiKeySecretName(), apiKey!.Trim(), ct);
            }
            else
            {
                await _secrets.DeleteAsync(registration.SiteId, registration.ApiKeySecretName(), ct);
            }

            registration.MarkApiKey(present);
            await _registrations.UpdateAsync(registration, ct);
            return McpRegistrationResult.Done();
        }

        private async Task<McpRegistrationResult> MutateAsync(Guid id, Action<McpServerRegistrationAggregate> change, CancellationToken ct)
        {
            var registration = await GetAsync(id, ct);
            if (registration is null)
            {
                return McpRegistrationResult.NotFound;
            }

            change(registration);
            await _registrations.UpdateAsync(registration, ct);
            return McpRegistrationResult.Done();
        }

        /// <summary>
        /// The stored key, or null. A value that is PRESENT but cannot be unprotected is a loud log and a refresh
        /// without a key - not an exception, and not a silent success that quietly dials the server anonymously and
        /// records whatever an unauthenticated caller is shown.
        /// </summary>
        private async Task<string?> ApiKeyAsync(McpServerRegistrationAggregate registration, CancellationToken ct)
        {
            if (!registration.HasApiKey)
            {
                return null;
            }

            var result = await _secrets.GetAsync(registration.SiteId, registration.ApiKeySecretName(), ct);
            if (result.Status == SiteSecretStatus.Found)
            {
                return result.Secret;
            }

            _logger.LogError(
                "The API key for MCP server '{Server}' on site {SiteId} could not be read ({Status}). The refresh will be made WITHOUT it, "
                + "so the server may answer as it answers an anonymous caller. Set the key again.",
                registration.Name, registration.SiteId, result.Status);
            return null;
        }
    }
}
