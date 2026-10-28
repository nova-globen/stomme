using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="ModuleAggregate"/> (the persisted aggregate) and <see cref="Module"/>
    /// (the client/server wire contract) at the <see cref="ModuleRepository"/> boundary. Only the columns the
    /// harvested schema actually has are mapped — <see cref="Module"/>'s <c>[NotMapped]</c> members (the
    /// PageModule properties, PermissionList, Settings, ModuleDefinition, IsShared, IPortableContext, the
    /// IDeletable trio and the SiteRouter/IModuleControl render state) are view state that other layers
    /// populate and were never persisted, so they stay untouched here, exactly as EF left them before this
    /// cluster moved to the seam.
    /// </summary>
    public static class ModuleMapper
    {
        public static Module ToModel(ModuleAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Module
            {
                ModuleId = aggregate.Id,
                SiteId = aggregate.SiteId,
                ModuleDefinitionName = aggregate.ModuleDefinitionName,
                AllPages = aggregate.AllPages,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static ModuleAggregate ToAggregate(Module module, bool assignId)
        {
            var aggregate = new ModuleAggregate
            {
                SiteId = module.SiteId,
                ModuleDefinitionName = module.ModuleDefinitionName,
                AllPages = module.AllPages,
                CreatedBy = module.CreatedBy,
                CreatedOn = module.CreatedOn,
                ModifiedBy = module.ModifiedBy,
                ModifiedOn = module.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(module.ModuleId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Module"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on
        /// it — notably <c>SiteRepository.CreatePages</c> and <c>SynchronizationJob.SynchronizeModules</c>,
        /// which read <c>module.ModuleId</c> straight after <c>AddModule</c> to attach the page module.
        /// </summary>
        public static void CopyPersistedValues(ModuleAggregate aggregate, Module module)
        {
            module.ModuleId = aggregate.Id;
            module.CreatedBy = aggregate.CreatedBy;
            module.CreatedOn = aggregate.CreatedOn;
            module.ModifiedBy = aggregate.ModifiedBy;
            module.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
