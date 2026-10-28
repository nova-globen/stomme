using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="ThemeAggregate"/> (the persisted aggregate) and <see cref="Theme"/> (the
    /// client/server contract) at the <see cref="Repository.ThemeRepository"/> boundary (D52).
    /// </summary>
    /// <remarks>
    /// <b>Unlike <see cref="JobMapper"/>, this mapping is PARTIAL by design.</b> <see cref="Theme"/> carries a
    /// large tail of <c>[NotMapped]</c> runtime state — Themes/Containers/Resources/PermissionList/IsEnabled/
    /// AssemblyName/Fingerprint and the obsolete properties — that has no column and is rebuilt from the loaded
    /// assemblies and from Settings/Permissions at read time. This mapper touches ONLY the four persisted columns
    /// (ThemeId, ThemeName, Name, the <c>version</c> string) plus the audit quartet; the repository re-populates
    /// the rest around it. <c>Theme.Version</c> maps to <see cref="ThemeAggregate.ThemeVersion"/>, the distinct
    /// property that carries the <c>version</c> column past the concurrency-token shadow.
    /// </remarks>
    public static class ThemeMapper
    {
        public static Theme ToModel(ThemeAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Theme
            {
                ThemeId = aggregate.Id,
                ThemeName = aggregate.ThemeName,
                Name = aggregate.Name,
                Version = aggregate.ThemeVersion,
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
        public static ThemeAggregate ToAggregate(Theme theme, bool assignId)
        {
            var aggregate = new ThemeAggregate
            {
                ThemeName = theme.ThemeName,
                Name = theme.Name,
                ThemeVersion = theme.Version,
                CreatedBy = theme.CreatedBy,
                CreatedOn = theme.CreatedOn,
                ModifiedBy = theme.ModifiedBy,
                ModifiedOn = theme.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(theme.ThemeId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <c>DbContextUtils</c>), so the caller's <see cref="Theme"/> reflects the persisted row.
        /// The theme-sync path relies on this: after inserting a newly-discovered theme it reads the row's ThemeId
        /// and audit stamps straight back to fill the assembly-scanned <see cref="Theme"/>.
        /// </summary>
        public static void CopyPersistedValues(ThemeAggregate aggregate, Theme theme)
        {
            theme.ThemeId = aggregate.Id;
            theme.CreatedBy = aggregate.CreatedBy;
            theme.CreatedOn = aggregate.CreatedOn;
            theme.ModifiedBy = aggregate.ModifiedBy;
            theme.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
