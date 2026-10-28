using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Module aggregate root — the persistence-side model of the framework store's <c>Module</c> table,
    /// reachable only through <see cref="IModuleStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Module</c> is a module INSTANCE on a site (a <see cref="ModuleDefinition"/> describes the module
    /// type). The table is tiny — SiteId, ModuleDefinitionName, AllPages and the four audit columns — because
    /// almost everything on the <see cref="Module"/> wire contract is <c>[NotMapped]</c>: the PageModule
    /// properties (Title, Pane, Order, ContainerType, EffectiveDate/ExpiryDate, Header, Footer) belong to the
    /// PageModule row, and the IDeletable trio, PermissionList, Settings, ModuleDefinition, IsShared and the
    /// SiteRouter/IModuleControl render state are populated by other layers. That is exactly why the
    /// aggregate is NOT <see cref="Module"/> — same reason as <see cref="SiteAggregate"/>/<see cref="PageAggregate"/>.
    /// <see cref="ModuleMapper"/> maps between the two at the repository boundary, so the HTTP/client
    /// contract is byte-for-byte unchanged.
    /// </para>
    /// <para>
    /// Note the Module table has no soft-delete columns: <see cref="Module.IsDeleted"/> and friends are
    /// <c>[NotMapped]</c> on the contract and a module is hard-deleted. The soft-delete/recycle-bin state a
    /// user sees for a module is the PageModule row's, not this one's.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Deliberate:
    /// this increment moves the store onto the seam; brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class ModuleAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// A reference to the Site aggregate BY ID. Cross-aggregate references are ids, never navigations
        /// (D7/D19) — this is the column <c>PageModuleRepository</c> filters a site's modules on.
        /// </summary>
        public int SiteId { get; set; }

        /// <summary>
        /// A reference to the <see cref="ModuleDefinition"/> by name. ModuleDefinitions are their own
        /// cluster, still on the <c>IDatabase</c>/<c>TenantDBContext</c> path.
        /// </summary>
        public string ModuleDefinitionName { get; set; }

        public bool AllPages { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Module"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int moduleId) => Id = moduleId;
    }
}
