using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="ModuleDefinitionAggregate"/> (the persisted aggregate) and
    /// <see cref="ModuleDefinition"/> (the client/server contract) at the
    /// <see cref="Repository.ModuleDefinitionRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <b>Like <see cref="ThemeMapper"/>, this mapping is PARTIAL by design.</b> <see cref="ModuleDefinition"/>
    /// carries a large tail of <c>[NotMapped]</c> runtime state — Permissions/Resources/Runtimes/Databases/
    /// Dependencies/PageTemplates/IsEnabled/AssemblyName/Fingerprint and the obsolete properties — that has no
    /// column and is rebuilt from the loaded module assemblies and from Settings/Permissions at read time. This
    /// mapper touches ONLY the six persisted columns (ModuleDefinitionId, ModuleDefinitionName, Name,
    /// Description, Categories, the <c>version</c> string) plus the audit quartet; the repository re-populates
    /// the rest around it. <c>ModuleDefinition.Version</c> maps to
    /// <see cref="ModuleDefinitionAggregate.ModuleDefinitionVersion"/>, the distinct property that carries the
    /// <c>version</c> column past the concurrency-token shadow.
    /// </remarks>
    public static class ModuleDefinitionMapper
    {
        public static ModuleDefinition ToModel(ModuleDefinitionAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new ModuleDefinition
            {
                ModuleDefinitionId = aggregate.Id,
                ModuleDefinitionName = aggregate.ModuleDefinitionName,
                Name = aggregate.Name,
                Description = aggregate.Description,
                Categories = aggregate.Categories,
                Version = aggregate.ModuleDefinitionVersion,
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
        public static ModuleDefinitionAggregate ToAggregate(ModuleDefinition moduleDefinition, bool assignId)
        {
            var aggregate = new ModuleDefinitionAggregate
            {
                ModuleDefinitionName = moduleDefinition.ModuleDefinitionName,
                Name = moduleDefinition.Name,
                Description = moduleDefinition.Description,
                Categories = moduleDefinition.Categories,
                ModuleDefinitionVersion = moduleDefinition.Version,
                CreatedBy = moduleDefinition.CreatedBy,
                CreatedOn = moduleDefinition.CreatedOn,
                ModifiedBy = moduleDefinition.ModifiedBy,
                ModifiedOn = moduleDefinition.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(moduleDefinition.ModuleDefinitionId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <c>DbContextUtils</c>), so the caller's <see cref="ModuleDefinition"/> reflects the
        /// persisted row. The module-sync path relies on this: after inserting a newly-discovered definition it
        /// reads the row's ModuleDefinitionId and audit stamps straight back to fill the assembly-scanned
        /// <see cref="ModuleDefinition"/>.
        /// </summary>
        public static void CopyPersistedValues(ModuleDefinitionAggregate aggregate, ModuleDefinition moduleDefinition)
        {
            moduleDefinition.ModuleDefinitionId = aggregate.Id;
            moduleDefinition.CreatedBy = aggregate.CreatedBy;
            moduleDefinition.CreatedOn = aggregate.CreatedOn;
            moduleDefinition.ModifiedBy = aggregate.ModifiedBy;
            moduleDefinition.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
