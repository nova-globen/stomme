using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="SettingAggregate"/> (the persisted aggregate) and <see cref="Setting"/>
    /// (the client/server wire contract) at the <see cref="SettingRepository"/> boundary. Every column the
    /// harvested schema has is mapped — unlike Site/Page/Module, <see cref="Setting"/> has no
    /// <c>[NotMapped]</c> view state at all, so this mapping is total and the parity test pins that.
    /// </summary>
    public static class SettingMapper
    {
        public static Setting ToModel(SettingAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Setting
            {
                SettingId = aggregate.Id,
                EntityName = aggregate.EntityName,
                EntityId = aggregate.EntityId,
                SettingName = aggregate.SettingName,
                SettingValue = aggregate.SettingValue,
                IsPrivate = aggregate.IsPrivate,
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
        public static SettingAggregate ToAggregate(Setting setting, bool assignId)
        {
            var aggregate = new SettingAggregate
            {
                EntityName = setting.EntityName,
                EntityId = setting.EntityId,
                SettingName = setting.SettingName,
                SettingValue = setting.SettingValue,
                IsPrivate = setting.IsPrivate,
                CreatedBy = setting.CreatedBy,
                CreatedOn = setting.CreatedOn,
                ModifiedBy = setting.ModifiedBy,
                ModifiedOn = setting.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(setting.SettingId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Setting"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on
        /// it — notably <c>SettingController.Post</c>, which returns the setting straight to the client, and
        /// <c>SiteRepository</c>/<c>ModuleRepository</c>'s import paths, which read <c>SettingId</c> back.
        /// </summary>
        public static void CopyPersistedValues(SettingAggregate aggregate, Setting setting)
        {
            setting.SettingId = aggregate.Id;
            setting.CreatedBy = aggregate.CreatedBy;
            setting.CreatedOn = aggregate.CreatedOn;
            setting.ModifiedBy = aggregate.ModifiedBy;
            setting.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
