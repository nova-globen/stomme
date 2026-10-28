#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Mediator;
using Stomme.Modules.Pipelines.Application;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Audit
{
    /// <summary>
    /// BL-229 - records a Pipelines command in the site's activity ledger once it has SUCCEEDED (a refusal or a failure changed
    /// nothing; the authorization behaviour, composed by the host before any module, never lets a refused one this far). Closed
    /// per command (<see cref="PipelineAuditRegistration"/>) - another module's requests never pass through it.
    /// </summary>
    public sealed class PipelineAuditBehavior<TRequest, TResponse>(PipelineAuditRecorder recorder) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        {
            var before = await recorder.BeforeAsync(request, ct);
            var response = await next();
            await recorder.RecordAsync(request, response, before, ct);
            return response;
        }
    }

    /// <summary>
    /// Who, where and which pipeline, for <see cref="PipelineAudit"/>'s sentence. The change has already happened when it writes,
    /// so a ledger that cannot be written is logged as an error, never thrown: failing the call would say the change did not
    /// happen when it did. Nobody signed in or no site resolved records nothing - every command of the module is sent from a
    /// request or a circuit, both of which have a site, so that is never the editor's path.
    /// </summary>
    public sealed class PipelineAuditRecorder(
        IPipelineAuditRepository audit, ICurrentUser user, ITenantContext tenantContext, SiteState siteState, TimeProvider clock,
        IServiceProvider services, ILogger<PipelineAuditRecorder> logger)
    {
        public async Task<PipelineAuditBefore> BeforeAsync(object request, CancellationToken ct)
        {
            if (!PipelineAudit.Actions.ContainsKey(request.GetType()))
            {
                return PipelineAuditBefore.Nothing;
            }

            try
            {
                var definitionId = Text(request, "DefinitionId") ?? await DefinitionOfRunAsync(RunIdOf(request), ct);
                var name = definitionId is null ? null : await NameAsync(definitionId, ct);
                var variables = request is SetSiteVariablesCommand && services.GetService(typeof(IKeyValueStore)) is IKeyValueStore keyValues
                    ? await PipelineSiteVariableStore.ReadAsync(keyValues, Tenant(), ct)
                    : null;
                return new PipelineAuditBefore(definitionId, name, variables);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "What {Command} concerns could not be read before it ran; it is recorded without it.", request.GetType().Name);
                return PipelineAuditBefore.Nothing;
            }
        }

        public async Task RecordAsync(object request, object? response, PipelineAuditBefore before, CancellationToken ct)
        {
            var fact = PipelineAudit.Describe(request, response, before);
            var siteId = siteState.Alias?.SiteId ?? 0;
            if (fact is null)
            {
                return;
            }

            if (siteId <= 0 || tenantContext.CurrentTenantId is not { } tenantId)
            {
                logger.LogWarning("{Command} succeeded with no site or tenant resolved, so it is not in the pipeline activity ledger.", request.GetType().Name);
                return;
            }

            try
            {
                var definitionId = fact.DefinitionId ?? before.DefinitionId ?? await DefinitionOfRunAsync(fact.RunId, ct);
                var name = fact.PipelineName ?? (definitionId == before.DefinitionId ? before.PipelineName : null)
                    ?? (definitionId is null ? null : await NameAsync(definitionId, ct));
                await audit.AddAsync(PipelineAuditEntryAggregate.Record(
                    Guid.NewGuid(), tenantId, siteId, user.UserId is > 0 ? user.UserId.Value : 0, user.UserName, fact.Action, definitionId, name,
                    fact.RunId, fact.Detail, clock.GetUtcNow().UtcDateTime), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Command} succeeded but could not be written to the pipeline activity ledger.", request.GetType().Name);
            }
        }

        private static string? Text(object request, string property) =>
            request.GetType().GetProperty(property)?.GetValue(request) is string { Length: > 0 } value ? value.Trim() : null;

        // The run a command concerns, when it names one: by its id (Cancel, Delete, Retry...) or as RunId (a review).
        private static string? RunIdOf(object request) => request switch
        {
            RetryRunCommand retry => retry.Id,
            RetryFromFailedStepCommand onward => onward.Id,
            CancelRunCommand cancel => cancel.Id,
            DeleteRunCommand delete => delete.Id,
            SetRunReviewCommand review => review.RunId,
            _ => null,
        };

        private async Task<string?> DefinitionOfRunAsync(string? runId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(runId) || services.GetService(typeof(IPipelineInstanceStore)) is not IPipelineInstanceStore instances)
            {
                return null;
            }

            return (await instances.FindAsync(new PipelineInstanceFilter { Id = runId }, ct))?.DefinitionId;
        }

        private async Task<string?> NameAsync(string definitionId, CancellationToken ct) =>
            services.GetService(typeof(IPipelineDefinitionStore)) is IPipelineDefinitionStore definitions
                ? (await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Latest }, ct))?.Name
                : null;

        private string? Tenant() => services.GetService(typeof(ITenantAccessor)) is ITenantAccessor tenants ? tenants.TenantId : null;
    }
}
