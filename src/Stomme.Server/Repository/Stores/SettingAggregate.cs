using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Setting aggregate root — the persistence-side model of the framework store's <c>Setting</c> table,
    /// reachable only through <see cref="ISettingStore"/>/<see cref="IMasterSettingStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Setting is <c>(EntityName, EntityId)</c>-keyed polymorphic state — a name/value pair hanging off
    /// *some* other entity (a Site, a Page, a Module, a User, the Host…) rather than a child of any one
    /// aggregate. It is therefore its own aggregate root, not a child collection: the harvested code reads,
    /// writes and deletes settings independently of whatever they describe, and the "owner" is a string plus
    /// an int, not a foreign key. Every migrated cluster's <c>DeleteX</c> calls
    /// <c>_settings.DeleteSettings(EntityNames.X, id)</c> — those are callers, and they stay callers.
    /// </para>
    /// <para>
    /// UNIQUE AMONG THE FRAMEWORK-STORE CLUSTERS: this aggregate is mapped in BOTH
    /// <see cref="TenantDBContext"/> and <see cref="MasterDBContext"/>, because the harvested
    /// <c>SettingRepository</c> routes by <c>EntityName</c> — Host/Job/ModuleDefinition/Theme/<c>master:*</c>
    /// settings live in the master database, everything else in the tenant database. Hence two stores over
    /// one aggregate. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Deliberate:
    /// this increment moves the store onto the seam; brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class SettingAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// What kind of entity this setting describes ("Site", "Page", "Module", "Host"…). Half of the
        /// polymorphic reference; it is also what <c>SettingRepository</c> routes on to choose the master or
        /// the tenant store.
        /// </summary>
        public string EntityName { get; set; }

        /// <summary>
        /// The id of the entity this setting describes. A reference to another aggregate BY ID — settings
        /// never navigate to what they describe (D7/D19), and there is no foreign key here: the pair
        /// (<see cref="EntityName"/>, <see cref="EntityId"/>) is a loose reference by construction.
        /// </summary>
        public int EntityId { get; set; }

        public string SettingName { get; set; }

        public string SettingValue { get; set; }

        /// <summary>
        /// Private settings are maintained on the server and never sent to the client —
        /// <c>SettingController</c> filters them out of its responses. Persisted since the tenant migration
        /// <c>03000202_UpdateSettingIsPrivate</c>.
        /// </summary>
        public bool IsPrivate { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Setting"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int settingId) => Id = settingId;
    }
}
