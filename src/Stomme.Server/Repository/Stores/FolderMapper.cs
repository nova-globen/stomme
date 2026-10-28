using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="FolderAggregate"/> (the persisted aggregate) and <see cref="Folder"/>
    /// (the client/server wire contract) at the <see cref="FolderRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Folder.PermissionList"/>, <see cref="Folder.Level"/> and <see cref="Folder.HasChildren"/> are
    /// deliberately absent: all three are <c>[NotMapped]</c> view state the repository populates AFTER this
    /// mapper has run — permissions from <see cref="IPermissionRepository"/>, Level and HasChildren from the
    /// in-memory hierarchy walk. Round-tripping a Folder through here LOSES all three by design. Pinned by
    /// <c>The_mapper_does_not_round_trip_the_unmapped_view_state</c>.
    /// <para>
    /// The deprecated <see cref="Folder.IsDeleted"/> is absent for a stronger reason: the column does not
    /// exist. See <see cref="FolderAggregate"/>.
    /// </para>
    /// </remarks>
    public static class FolderMapper
    {
        public static Folder ToModel(FolderAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Folder
            {
                FolderId = aggregate.Id,
                SiteId = aggregate.SiteId,
                ParentId = aggregate.ParentId,
                Type = aggregate.Type,
                Name = aggregate.Name,
                Path = aggregate.Path,
                Order = aggregate.Order,
                ImageSizes = aggregate.ImageSizes,
                Capacity = aggregate.Capacity,
                IsSystem = aggregate.IsSystem,
                CacheControl = aggregate.CacheControl,
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
        public static FolderAggregate ToAggregate(Folder folder, bool assignId)
        {
            var aggregate = new FolderAggregate
            {
                SiteId = folder.SiteId,
                ParentId = folder.ParentId,
                Type = folder.Type,
                Name = folder.Name,
                Path = folder.Path,
                Order = folder.Order,
                ImageSizes = folder.ImageSizes,
                Capacity = folder.Capacity,
                IsSystem = folder.IsSystem,
                CacheControl = folder.CacheControl,
                CreatedBy = folder.CreatedBy,
                CreatedOn = folder.CreatedOn,
                ModifiedBy = folder.ModifiedBy,
                ModifiedOn = folder.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(folder.FolderId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write: the generated identity and the stamps
        /// <see cref="DbContextUtils"/> applied. The harvested repository handed back the tracked entity itself
        /// and callers rely on it — <c>FolderController.Post</c> creates a folder and returns the result
        /// straight to the client, and <c>UserManager</c> reads the new FolderId back to create the user's
        /// own folder under it.
        /// </summary>
        public static void CopyPersistedValues(FolderAggregate aggregate, Folder folder)
        {
            folder.FolderId = aggregate.Id;
            folder.CreatedBy = aggregate.CreatedBy;
            folder.CreatedOn = aggregate.CreatedOn;
            folder.ModifiedBy = aggregate.ModifiedBy;
            folder.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
