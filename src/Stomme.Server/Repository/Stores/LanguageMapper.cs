using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="LanguageAggregate"/> (the persisted aggregate) and
    /// <see cref="Language"/> (the client/server wire contract) at the <see cref="LanguageRepository"/>
    /// boundary. Only the columns the harvested schema actually has are mapped.
    /// </summary>
    /// <remarks>
    /// <see cref="Language"/>'s three <c>[NotMapped]</c> members — <c>Name</c>, <c>Version</c> and
    /// <c>AliasName</c> — are deliberately absent. <c>Name</c> is the load-bearing one: there is no
    /// <c>name</c> column at all (see <see cref="LanguageAggregate"/>), so round-tripping a Language through
    /// this mapper LOSES Name by design. Callers that need it get it from
    /// <c>LanguageController</c>/<c>SiteService</c>, which fill it in from the installed satellite assemblies
    /// and <c>CultureInfo</c>, never from the database. Pinned by
    /// <c>The_mapper_does_not_round_trip_the_unmapped_view_state</c>.
    /// </remarks>
    public static class LanguageMapper
    {
        public static Language ToModel(LanguageAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Language
            {
                LanguageId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Code = aggregate.Code,
                IsDefault = aggregate.IsDefault,
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
        public static LanguageAggregate ToAggregate(Language language, bool assignId)
        {
            var aggregate = new LanguageAggregate
            {
                SiteId = language.SiteId,
                Code = language.Code,
                IsDefault = language.IsDefault,
                CreatedBy = language.CreatedBy,
                CreatedOn = language.CreatedOn,
                ModifiedBy = language.ModifiedBy,
                ModifiedOn = language.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(language.LanguageId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Language"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on it
        /// — <c>LanguageController.Post</c> returns the language straight to the client.
        /// </summary>
        public static void CopyPersistedValues(LanguageAggregate aggregate, Language language)
        {
            language.LanguageId = aggregate.Id;
            language.CreatedBy = aggregate.CreatedBy;
            language.CreatedOn = aggregate.CreatedOn;
            language.ModifiedBy = aggregate.ModifiedBy;
            language.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
