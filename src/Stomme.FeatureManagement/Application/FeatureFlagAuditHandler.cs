using Stomme.FeatureManagement.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Application;

/// <summary>
/// Records every flag change to the audit trail. Runs in the CALLER's scope after the flag is persisted,
/// capturing the acting principal (when one is in scope) and the originating tenant, then writing an
/// append-only <see cref="FeatureFlagAudit"/>.
/// </summary>
/// <remarks>
/// Honours the handler contract on <see cref="IDomainEventHandler{TEvent}"/>: it STAGES the audit row via
/// the repository and returns. It does not save — the caller's unit of work commits the audit together with
/// the flag change, which is what makes an audit that cannot be lost. Current-scope dispatch is also what
/// puts a real <see cref="ITenantContext"/> in reach here; a child scope would re-resolve it and the audit
/// could land against the wrong tenant.
/// </remarks>
internal sealed class FeatureFlagAuditHandler(
    IRepository<FeatureFlagAudit, Guid> audits,
    ITenantContext tenantContext,
    IFeatureContextProvider contextProvider) : IDomainEventHandler<FeatureToggledEvent>
{
    public async Task HandleAsync(FeatureToggledEvent domainEvent, CancellationToken ct = default)
    {
        var tenantId = tenantContext.CurrentTenantId ?? TenantId.Default;
        var changedBy = contextProvider.GetContext().UserId?.ToString();

        await audits.AddAsync(
            FeatureFlagAudit.Record(tenantId, domainEvent.FlagId, domainEvent.Key, domainEvent.Enabled, changedBy, DateTimeOffset.UtcNow),
            ct);
    }
}
