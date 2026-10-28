using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="AliasAggregate"/> (the persisted aggregate) and <see cref="Alias"/> (the
    /// client/server contract) at the <see cref="Repository.AliasRepository"/> boundary (D52).
    /// </summary>
    /// <remarks>
    /// This mapping is PARTIAL by design. <see cref="Alias"/> carries <c>[NotMapped]</c> runtime state —
    /// <see cref="Alias.Path"/>, <see cref="Alias.SiteKey"/>, <see cref="Alias.Protocol"/> and
    /// <see cref="Alias.BaseUrl"/> — that has no column and is derived at read time. This mapper touches ONLY the five
    /// persisted content columns (AliasId, Name, TenantId, SiteId, IsDefault) plus the audit quartet.
    /// </remarks>
    public static class AliasMapper
    {
        public static Alias ToModel(AliasAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Alias
            {
                AliasId = aggregate.Id,
                Name = aggregate.Name,
                TenantId = aggregate.TenantId,
                SiteId = aggregate.SiteId,
                IsDefault = aggregate.IsDefault,
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
        public static AliasAggregate ToAggregate(Alias alias, bool assignId)
        {
            var aggregate = new AliasAggregate
            {
                Name = alias.Name,
                TenantId = alias.TenantId,
                SiteId = alias.SiteId,
                IsDefault = alias.IsDefault,
                CreatedBy = alias.CreatedBy,
                CreatedOn = alias.CreatedOn,
                ModifiedBy = alias.ModifiedBy,
                ModifiedOn = alias.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(alias.AliasId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <c>DbContextUtils</c>), so the caller's <see cref="Alias"/> reflects the persisted row — the
        /// installer and the AliasController both read the assigned AliasId straight back after an insert.
        /// </summary>
        public static void CopyPersistedValues(AliasAggregate aggregate, Alias alias)
        {
            alias.AliasId = aggregate.Id;
            alias.CreatedBy = aggregate.CreatedBy;
            alias.CreatedOn = aggregate.CreatedOn;
            alias.ModifiedBy = aggregate.ModifiedBy;
            alias.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
