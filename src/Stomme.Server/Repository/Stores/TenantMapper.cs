using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="TenantAggregate"/> (the persisted aggregate) and <see cref="Tenant"/> (the
    /// client/server contract) at the <see cref="Repository.TenantRepository"/> boundary (D52).
    /// </summary>
    /// <remarks>
    /// This mapping is TOTAL — <see cref="Tenant"/> has no <c>[NotMapped]</c> tail; every one of its properties is a
    /// persisted column. <c>Tenant.Version</c> maps to <see cref="TenantAggregate.TenantVersion"/>, the distinct
    /// property that carries the <c>version</c> column past the <see cref="Stomme.SharedKernel.Domain.AggregateRoot{TId}.Version"/>
    /// concurrency-token shadow. <c>Tenant.DBType</c> — the provider-selector — is carried verbatim.
    /// </remarks>
    public static class TenantMapper
    {
        public static Tenant ToModel(TenantAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Tenant
            {
                TenantId = aggregate.Id,
                Name = aggregate.Name,
                DBConnectionString = aggregate.DBConnectionString,
                DBType = aggregate.DBType,
                Version = aggregate.TenantVersion,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the database
        /// assigns the identity) and true for an update or a delete.
        /// </summary>
        public static TenantAggregate ToAggregate(Tenant tenant, bool assignId)
        {
            var aggregate = new TenantAggregate
            {
                Name = tenant.Name,
                DBConnectionString = tenant.DBConnectionString,
                DBType = tenant.DBType,
                TenantVersion = tenant.Version,
                CreatedBy = tenant.CreatedBy,
                CreatedOn = tenant.CreatedOn,
                ModifiedBy = tenant.ModifiedBy,
                ModifiedOn = tenant.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(tenant.TenantId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <c>DbContextUtils</c>), so the caller's <see cref="Tenant"/> reflects the persisted row — the
        /// installer reads the assigned TenantId straight back after an insert to build the tenant's aliases.
        /// </summary>
        public static void CopyPersistedValues(TenantAggregate aggregate, Tenant tenant)
        {
            tenant.TenantId = aggregate.Id;
            tenant.CreatedBy = aggregate.CreatedBy;
            tenant.CreatedOn = aggregate.CreatedOn;
            tenant.ModifiedBy = aggregate.ModifiedBy;
            tenant.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
