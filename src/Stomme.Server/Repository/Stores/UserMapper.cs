using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="UserAggregate"/> (the persisted aggregate) and <see cref="User"/> (the
    /// client/server wire contract) at the <see cref="UserRepository"/> boundary. Only the columns the
    /// harvested schema actually has are mapped — <see cref="User"/>'s <c>[NotMapped]</c> members
    /// (SecurityStamp, SiteId, Roles, Password, IsAuthenticated, FolderPath, EmailConfirmed,
    /// SuppressNotification and Settings) are per-request state that other layers populate and were never
    /// persisted, so they stay untouched here, exactly as EF left them before this cluster moved to the seam.
    /// </summary>
    public static class UserMapper
    {
        public static User ToModel(UserAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new User
            {
                UserId = aggregate.Id,
                Username = aggregate.Username,
                DisplayName = aggregate.DisplayName,
                Email = aggregate.Email,
                TimeZoneId = aggregate.TimeZoneId,
                CultureCode = aggregate.CultureCode,
                PhotoFileId = aggregate.PhotoFileId,
                LastLoginOn = aggregate.LastLoginOn,
                LastIPAddress = aggregate.LastIPAddress,
                TwoFactorRequired = aggregate.TwoFactorRequired,
                TwoFactorCode = aggregate.TwoFactorCode,
                TwoFactorExpiry = aggregate.TwoFactorExpiry,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn,
                DeletedBy = aggregate.DeletedBy,
                DeletedOn = aggregate.DeletedOn,
                IsDeleted = aggregate.IsDeleted
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static UserAggregate ToAggregate(User user, bool assignId)
        {
            var aggregate = new UserAggregate
            {
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                TimeZoneId = user.TimeZoneId,
                CultureCode = user.CultureCode,
                PhotoFileId = user.PhotoFileId,
                LastLoginOn = user.LastLoginOn,
                LastIPAddress = user.LastIPAddress,
                TwoFactorRequired = user.TwoFactorRequired,
                TwoFactorCode = user.TwoFactorCode,
                TwoFactorExpiry = user.TwoFactorExpiry,
                CreatedBy = user.CreatedBy,
                CreatedOn = user.CreatedOn,
                ModifiedBy = user.ModifiedBy,
                ModifiedOn = user.ModifiedOn,
                DeletedBy = user.DeletedBy,
                DeletedOn = user.DeletedOn,
                IsDeleted = user.IsDeleted
            };

            if (assignId)
            {
                aggregate.AssignId(user.UserId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="User"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on
        /// it — notably <c>UserManager.AddUser</c>, which reads <c>user.UserId</c> straight after
        /// <c>AddUser</c> to create the user's folder and auto-assigned roles.
        /// </summary>
        public static void CopyPersistedValues(UserAggregate aggregate, User user)
        {
            user.UserId = aggregate.Id;
            user.CreatedBy = aggregate.CreatedBy;
            user.CreatedOn = aggregate.CreatedOn;
            user.ModifiedBy = aggregate.ModifiedBy;
            user.ModifiedOn = aggregate.ModifiedOn;
            user.DeletedBy = aggregate.DeletedBy;
            user.DeletedOn = aggregate.DeletedOn;
            user.IsDeleted = aggregate.IsDeleted;
        }
    }
}
