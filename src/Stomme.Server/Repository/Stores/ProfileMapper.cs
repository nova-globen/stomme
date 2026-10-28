using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="ProfileAggregate"/> (the persisted aggregate) and <see cref="Profile"/>
    /// (the client/server wire contract) at the <see cref="ProfileRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Profile"/> has no <c>[NotMapped]</c> members: every public property is a column and every
    /// column is copied here.
    /// </remarks>
    public static class ProfileMapper
    {
        public static Profile ToModel(ProfileAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Profile
            {
                ProfileId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Name = aggregate.Name,
                Title = aggregate.Title,
                Description = aggregate.Description,
                Category = aggregate.Category,
                ViewOrder = aggregate.ViewOrder,
                MaxLength = aggregate.MaxLength,
                DefaultValue = aggregate.DefaultValue,
                IsRequired = aggregate.IsRequired,
                IsPrivate = aggregate.IsPrivate,
                Options = aggregate.Options,
                Validation = aggregate.Validation,
                Rows = aggregate.Rows,
                Autocomplete = aggregate.Autocomplete,
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
        public static ProfileAggregate ToAggregate(Profile profile, bool assignId)
        {
            var aggregate = new ProfileAggregate
            {
                SiteId = profile.SiteId,
                Name = profile.Name,
                Title = profile.Title,
                Description = profile.Description,
                Category = profile.Category,
                ViewOrder = profile.ViewOrder,
                MaxLength = profile.MaxLength,
                DefaultValue = profile.DefaultValue,
                IsRequired = profile.IsRequired,
                IsPrivate = profile.IsPrivate,
                Options = profile.Options,
                Validation = profile.Validation,
                Rows = profile.Rows,
                Autocomplete = profile.Autocomplete,
                CreatedBy = profile.CreatedBy,
                CreatedOn = profile.CreatedOn,
                ModifiedBy = profile.ModifiedBy,
                ModifiedOn = profile.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(profile.ProfileId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Profile"/> reflects the
        /// persisted row. <c>ProfileController.Post</c> returns the profile straight to the client.
        /// </summary>
        public static void CopyPersistedValues(ProfileAggregate aggregate, Profile profile)
        {
            profile.ProfileId = aggregate.Id;
            profile.CreatedBy = aggregate.CreatedBy;
            profile.CreatedOn = aggregate.CreatedOn;
            profile.ModifiedBy = aggregate.ModifiedBy;
            profile.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
