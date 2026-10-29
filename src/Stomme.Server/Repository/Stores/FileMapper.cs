namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="FileAggregate"/> (the persisted aggregate) and <see cref="Models.File"/>
    /// (the client/server wire contract) at the <see cref="FileRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Models.File.Folder"/> and <see cref="Models.File.Url"/> are deliberately absent: both are
    /// <c>[NotMapped]</c> view state the repository populates AFTER this mapper has run — the folder from
    /// <see cref="IFolderRepository"/> (the navigation is severed), the url computed from it. Round-tripping a
    /// File through here LOSES both by design. Pinned by
    /// <c>The_mapper_does_not_round_trip_the_unmapped_view_state</c>.
    /// </para>
    /// <para>
    /// <see cref="FileAggregate.NameNormalized"/> is absent for a different reason: it is a lookup key, not
    /// wire state, and it has no setter to map to — <see cref="FileAggregate.Name"/> derives it. The
    /// <see cref="Models.File"/> contract must never carry it, exactly as Identity never hands you a
    /// NormalizedUserName.
    /// </para>
    /// <para>
    /// The deprecated <see cref="Models.File.IsDeleted"/> is absent for a stronger reason still: the column
    /// does not exist. See <see cref="FileAggregate"/>.
    /// </para>
    /// </remarks>
    public static class FileMapper
    {
        public static Models.File ToModel(FileAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Models.File
            {
                FileId = aggregate.Id,
                FolderId = aggregate.FolderId,
                Name = aggregate.Name,
                Extension = aggregate.Extension,
                Size = aggregate.Size,
                ImageHeight = aggregate.ImageHeight,
                ImageWidth = aggregate.ImageWidth,
                Description = aggregate.Description,
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
        public static FileAggregate ToAggregate(Models.File file, bool assignId)
        {
            var aggregate = new FileAggregate
            {
                FolderId = file.FolderId,
                Name = file.Name,
                Extension = file.Extension,
                Size = file.Size,
                ImageHeight = file.ImageHeight,
                ImageWidth = file.ImageWidth,
                Description = file.Description,
                CreatedBy = file.CreatedBy,
                CreatedOn = file.CreatedOn,
                ModifiedBy = file.ModifiedBy,
                ModifiedOn = file.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(file.FileId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write: the generated identity and the stamps
        /// <see cref="DbContextUtils"/> applied. The harvested repository handed back the tracked entity itself
        /// and callers rely on it — <c>FileController.Post</c> uploads a file and returns the result straight
        /// to the client, and <c>DefaultSiteTemplate</c> reads the new FileId back to point a new site's logo
        /// at it.
        /// </summary>
        public static void CopyPersistedValues(FileAggregate aggregate, Models.File file)
        {
            file.FileId = aggregate.Id;
            file.CreatedBy = aggregate.CreatedBy;
            file.CreatedOn = aggregate.CreatedOn;
            file.ModifiedBy = aggregate.ModifiedBy;
            file.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
