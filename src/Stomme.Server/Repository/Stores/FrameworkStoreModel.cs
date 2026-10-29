using Stomme.Databases.Interfaces;
using Stomme.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Maps the framework store's aggregates onto the harvested tenant schema. The aggregates share
    /// <see cref="TenantDBContext"/> with the not-yet-migrated entities, so a migrated cluster and an
    /// unmigrated one keep using the same per-request connection, the same audit hooks
    /// (<see cref="DbContextUtils"/>) and the same migration set — no schema change, no data migration.
    /// </summary>
    public static class FrameworkStoreModel
    {
        /// <summary>
        /// Configures every framework-store aggregate. <paramref name="database"/> supplies
        /// <see cref="IRelationalDatabase.RewriteName"/>, the same name rewriter the migration builders use, so every
        /// explicitly-named table/column matches the column the migration actually created (PostgreSQL
        /// rewrites to snake_case; the other providers rewrite to themselves). Names left to convention are
        /// rewritten by EFCore.NamingConventions exactly as they were before this cluster moved, but an
        /// explicit <c>HasColumnName</c> outranks that convention and would NOT be rewritten — hence
        /// <see cref="IRelationalDatabase.RewriteName"/> on every explicit name here.
        /// </summary>
        public static void ConfigureFrameworkStore(this ModelBuilder builder, IRelationalDatabase database)
        {
            builder.Entity<SiteAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Site"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SiteId"))
                    .ValueGeneratedOnAdd();

                // The Site table's `Version` column is the ISiteMigration configuration version (a string),
                // which AggregateRoot<TId>.Version (a uint concurrency token) shadows by name.
                entity.Property(item => item.SiteVersion).HasColumnName(database.RewriteName("Version"));

                // AggregateRoot<TId>.Version has no column in the harvested schema. Ignoring it keeps this
                // cluster at parity with the IDatabase path, which has no optimistic concurrency on Site
                // either; adding the column is a schema change and belongs with Phase 5, not here. The
                // consequence: writes to Site cannot raise ConcurrencyConflictException on the relational
                // provider (RavenDB would still get change-vector concurrency for free).
                entity.Ignore(item => item.Version);

                // Domain events are an in-memory concern, never persisted (mirrors StommeDbContext).
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<PageAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Page"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("PageId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic
                // concurrency on Page either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<ModuleAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Module"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("ModuleId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic
                // concurrency on Module either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // The Users/Roles cluster. Note these three share this context with ASP.NET Identity's own
            // EF model (TenantDBContext derives from IdentityUserContext<IdentityUser>), which stays the live
            // authentication path. There is no conflict to resolve: Identity maps IdentityUser and friends to
            // the AspNetUser* tables, these map Stomme's own User/Role/UserRole tables, and no navigation
            // crosses between the two models. Do not "unify" them here — that is an identity cutover, a
            // separate, deliberate decision.
            builder.Entity<UserAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("User"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("UserId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on User either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<RoleAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Role"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("RoleId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<UserRoleAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("UserRole"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("UserRoleId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // The Setting cluster — the TENANT half. Site/Page/Module/User/... settings. The master
            // half is mapped by ConfigureMasterFrameworkStore onto MasterDBContext; SettingRepository routes
            // between them by EntityName. Same aggregate, same table shape, two databases.
            builder.ConfigureSettingAggregate(database);

            // The Permission cluster. Mapped HERE ONLY — unlike Setting, Permission is a tenant-side
            // table with no master half: MasterDBContext never had a DbSet<Permission> and the master
            // migration set never created the table. Do not add it to ConfigureMasterFrameworkStore.
            builder.Entity<PermissionAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Permission"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("PermissionId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on Permission either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // The five LEAF clusters: Language, Log, Profile, SiteTask, Notification. Tenant-side only,
            // like Permission and unlike Setting — none of them is mapped by ConfigureMasterFrameworkStore and
            // none should be. Each is a single table with no navigation in or out, which is what made them the
            // lowest-risk of the remaining set.
            //
            // NOTE what is NOT written below: a HasColumnName for anything but the key. Every other column is
            // left to convention, which is what the harvested DbSets did, so the rewriter produces exactly the
            // names it already produced. The keys need the explicit name because AggregateRoot<TId> calls the
            // property Id while the tables call it {Entity}Id — and an explicit name OUTRANKS the snake_case
            // convention, hence RewriteName around every one of them.
            builder.Entity<LanguageAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Language"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("LanguageId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on Language either — parity with the IDatabase path. Phase 5 owns the column.
                //
                // NOTE this is NOT the same hazard SiteAggregate.SiteVersion solves: Language.Version is a
                // [NotMapped] satellite-assembly version with no column, so it is absent from the aggregate
                // entirely and there is nothing for AggregateRoot<TId>.Version to shadow.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<LogAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Log"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("LogId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<ProfileAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Profile"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("ProfileId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<SiteTaskAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SiteTask"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SiteTaskId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<NotificationAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Notification"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("NotificationId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // The SiteGroup PAIR: SiteGroup and SiteGroupMember, which had to land together. Tenant-side
            // only, like Permission and the leaf clusters.
            //
            // These two are NOT leaves and that is the whole point. SiteGroupMember.SiteGroup was a MAPPED
            // navigation into the SiteGroup CONTRACT, so mapping SiteGroupAggregate here while that navigation
            // stayed mapped made EF rediscover SiteGroup through it and claim this very table — the model-rediscovery
            // blocker, OBSERVED for this cluster rather than quoted:
            //
            //   "The entity type 'SiteGroup' cannot be added to the model because a type with the same name
            //    already exists ... 'site_group' cannot be used for entity type 'SiteGroupAggregate' since it
            //    is being used for entity type 'SiteGroupMember.SiteGroup#SiteGroup'."
            //
            // The navigation is severed at the CONTRACT ([NotMapped] on SiteGroupMember.SiteGroup) rather than
            // ignored per-context, because the contract is shared and the member is now view state the
            // repository populates, not a navigation any context should map. See SiteGroupMemberAggregate.
            builder.Entity<SiteGroupAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SiteGroup"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SiteGroupId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on SiteGroup either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<SiteGroupMemberAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SiteGroupMember"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SiteGroupMemberId"))
                    .ValueGeneratedOnAdd();

                // NOTE what is NOT configured: a HasOne(...).WithMany() to SiteGroupAggregate. The database
                // keeps its cascading foreign key on site_group_id — the migration created it and nothing here
                // drops it — but the MODEL references the group by id only. Mapping the relationship
                // would put a navigation back and re-open the blocker above from the other side.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // Visitor, UrlMapping and PageModule. Tenant-side only, like Permission, the leaf
            // clusters and the SiteGroup pair.
            //
            // These three needed NO navigation severed, and that is what made them a group: Visitor.User,
            // PageModule.Page and PageModule.Module had already been cut while deliberately
            // leaving the PageModule ROW behind. UrlMapping never had one. So unlike the SiteGroup pair, mapping these
            // aggregates could not re-open the model-rediscovery blocker from a contract that still carried a navigation —
            // the [NotMapped] attributes already in place are what make this a row move and
            // nothing more. The blockers stay pinned reflectively (see the *MustNotBeResurrected* tests).
            builder.Entity<VisitorAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Visitor"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("VisitorId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on Visitor either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<UrlMappingAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("UrlMapping"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("UrlMappingId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<PageModuleAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("PageModule"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("PageModuleId"))
                    .ValueGeneratedOnAdd();

                // NOTE what is NOT configured: a HasOne(...).WithMany() to PageAggregate or ModuleAggregate.
                // The database keeps both foreign keys — fk_page_module_page (cascade) and fk_page_module_module
                // (no action) — but the MODEL references both by id only. Mapping either
                // relationship would put a navigation back and re-open the blocker that severing those navigations closed.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // Folder. Tenant-side only, like Permission, the leaf clusters, the SiteGroup
            // pair and the Visitor/UrlMapping/PageModule three.
            //
            // Folder is NOT a leaf, and File.Folder was the reason — a MAPPED navigation from the File
            // CONTRACT into the Folder contract. While it stayed mapped, mapping FolderAggregate here made EF
            // rediscover Folder through it and claim this very table, exactly as SiteGroupMember.SiteGroup
            // did. OBSERVED for this cluster rather than inherited from another cluster's write-up:
            //
            //   "The entity type 'Folder' cannot be added to the model because a type with the same name
            //    already exists ... 'folder' cannot be used for entity type 'FolderAggregate' since it is being
            //    used for entity type 'File.Folder#Folder'."
            //
            // The navigation is severed at the CONTRACT ([NotMapped] on File.Folder) rather than ignored
            // per-context, because the contract is shared and the member is now view state FileRepository
            // populates. That severance is the expensive half of this increment: File.Folder is read in ~30
            // production places, including the render path (Pages/Files.cshtml.cs). See
            // src/Stomme.Server/README.md (persistence seam).
            //
            // The FILE row followed later — see ConfigureFileAggregate below. Until then it stayed a DbSet on
            // TenantDBContext because its .ToLower() reads had no portable spelling (src/Stomme.Server/README.md persistence seam).
            builder.Entity<FolderAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Folder"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("FolderId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on Folder either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // File — the last framework cluster onto the seam. Its DbSet is gone from TenantDBContext,
            // which is the compiler's proof that it moved.
            //
            // NOTE there is no navigation to configure in EITHER direction, and that is what made this
            // mapping trivial where Folder's was not: File.Folder had already been severed to [NotMapped] at
            // the CONTRACT, because while it stayed mapped EF rediscovered the Folder contract through it and
            // claimed the table FolderAggregate owns. FileRepository stitches the member on by hand.
            //
            // Name and Path are NOT the columns these two clusters are looked up by any more: name_normalized
            // and path_normalized are. Both are derived in their aggregate's setter, so EF maps them as
            // ordinary properties with private setters — it writes them on materialization and reads them for
            // the filter, and the derivation makes an unnormalized row unrepresentable. See NormalizedText.
            builder.Entity<FileAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("File"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("FileId"))
                    .ValueGeneratedOnAdd();

                // As Folder: no concurrency column in the harvested schema, so none here. Phase 5 owns it.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // The SearchContent cluster — the last to cross. FOUR tables, tenant-side only, so none of them
            // is mapped by ConfigureMasterFrameworkStore and none should be.
            //
            // NONE of the four is IAuditable and none is IDeletable, and unlike the clusters before it that was not a
            // correction to a lying EntityBuilder — all four builders extend BaseEntityBuilder and the live
            // PostgreSQL tables match them exactly (13/4/6/3 columns, zero dropped stubs in pg_attribute).
            // The created_on/modified_on columns below are PLAIN columns the application sets, not stamps:
            // there is no created_by/modified_by anywhere in this cluster to pair them with. DbContextUtils
            // decides by RUNTIME TYPE TEST, so adding either interface would compile and then write columns
            // that do not exist. See the aggregates' remarks.
            //
            // The three contract navigations that would otherwise resurrect these types through the tenant
            // model — SearchContent.SearchContentProperties, SearchContentWord.SearchWord and
            // SearchContentWord.SearchContent — are [NotMapped] on the contracts (the same mechanism as the other severed navigations).
            builder.Entity<SearchContentAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SearchContent"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SearchContentId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<SearchContentPropertyAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SearchContentProperty"));
                entity.HasKey(item => item.Id);

                // NOTE the key column is PropertyId, not SearchContentPropertyId — harvested, not tidied.
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("PropertyId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<SearchContentWordAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SearchContentWord"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SearchContentWordId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<SearchWordAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("SearchWord"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SearchWordId"))
                    .ValueGeneratedOnAdd();

                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });
        }

        /// <summary>
        /// Configures the framework store's aggregates on the MASTER database — <see cref="SettingAggregate"/>
        /// and nothing else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately NOT <see cref="ConfigureFrameworkStore"/>: Site, Page, Module, User, Role and UserRole
        /// are per-tenant tables that do not exist in the master database's own migration set. Mapping them
        /// here would invent entity types over tables the master database is not guaranteed to have.
        /// </para>
        /// <para>
        /// Setting is the ONE framework-store aggregate the master database legitimately holds, because
        /// <c>SettingRepository.IsMaster</c> routes Host/Job/ModuleDefinition/Theme/<c>master:*</c> settings
        /// there. Note the master database has no Setting migration of its own — the table it reads is the one
        /// the TENANT migration set created, which works because the master database is also a tenant
        /// database in every supported install. That is harvested behaviour, unchanged here; see
        /// src/Stomme.Server/README.md (persistence seam).
        /// </para>
        /// </remarks>
        public static void ConfigureMasterFrameworkStore(this ModelBuilder builder, IRelationalDatabase database)
        {
            builder.ConfigureSettingAggregate(database);

            // The Job/JobLog PAIR — the master-bookkeeping cluster. MASTER-side ONLY, unlike Setting
            // (which is both): the harvested MasterDBContext held DbSet<Job> and DbSet<JobLog> and the master
            // migration set creates both tables; no tenant database has them. So they are mapped HERE and NOT in
            // ConfigureFrameworkStore.
            //
            // Job IS IAuditable (JobEntityBuilder extends AuditableBaseEntityBuilder) — the audit quartet is
            // stamped by DbContextUtils. JobLog is NOT (JobLogEntityBuilder extends BaseEntityBuilder). Both
            // verified column-for-column against the live migrated PostgreSQL database (stomme-pg-44357, host
            // 45432): job has 17 columns incl. the quartet and a NULLABLE maximum_duration; job_log has 8 and
            // NONE of the quartet. See the aggregates' remarks.
            builder.Entity<JobAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Job"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("JobId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency on
                // Job either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<JobLogAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("JobLog"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("JobLogId"))
                    .ValueGeneratedOnAdd();

                // NOTE what is NOT configured: a HasOne(...).WithMany() to JobAggregate. The database keeps its
                // cascading foreign key on job_id — the migration created it and nothing here drops it — but the
                // MODEL references the job by id only. Mapping the relationship would put a navigation
                // back and let EF rediscover Job through JobLog.Job (the model-rediscovery blocker), which is why that navigation
                // is [NotMapped] on the contract.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // Theme — the master-bookkeeping cluster's last member. MASTER-side ONLY like the Job/JobLog
            // pair: the harvested MasterDBContext held DbSet<Theme> and the master migration set creates the
            // table; no tenant database has it. So it is mapped HERE and NOT in ConfigureFrameworkStore.
            //
            // Theme IS IAuditable (ThemeEntityBuilder extends AuditableBaseEntityBuilder) — the audit quartet is
            // stamped by DbContextUtils. Verified column-for-column against the live migrated PostgreSQL database
            // (stomme-pg-44357, host 45432): the theme table has EXACTLY eight columns — theme_id, theme_name,
            // name, version, and the four audit columns (all NOT NULL). It is NOT IDeletable.
            builder.Entity<ThemeAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Theme"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("ThemeId"))
                    .ValueGeneratedOnAdd();

                // The Theme table's `version` column (migration 06010001) is a STRING, which
                // AggregateRoot<TId>.Version (a uint concurrency token) shadows by name — the SiteAggregate.
                // SiteVersion situation. ThemeVersion carries the string onto the `version` column; an explicit
                // HasColumnName is required because the property name (ThemeVersion) does not match the column.
                entity.Property(item => item.ThemeVersion).HasColumnName(database.RewriteName("Version"));

                // As every other cluster: AggregateRoot<TId>.Version (the uint token) has no column in the
                // harvested schema, so no optimistic concurrency on Theme either — parity with the IDatabase
                // path. Phase 5 owns the column. Name is left to convention (it matches the `name` column).
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // ModuleDefinition — installation-wide registry of installed module types, read during module
            // discovery at startup. MASTER-side ONLY like the Job/JobLog pair and Theme: the harvested
            // MasterDBContext held DbSet<ModuleDefinition> and the master migration set creates the table; no
            // tenant database has it. So it is mapped HERE and NOT in ConfigureFrameworkStore.
            //
            // ModuleDefinition IS IAuditable (ModuleDefinitionsEntityBuilder extends AuditableBaseEntityBuilder)
            // — the audit quartet is stamped by DbContextUtils. Verified column-for-column against the live
            // migrated PostgreSQL database (stomme-pg-44357, host 45432): the module_definition table has
            // EXACTLY ten columns — module_definition_id, module_definition_name (NOT NULL), the NULLABLE
            // name/description/categories/version, and the four audit columns (all NOT NULL). It is NOT
            // IDeletable. The builder matches the live table exactly here — no drift.
            builder.Entity<ModuleDefinitionAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("ModuleDefinition"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("ModuleDefinitionId"))
                    .ValueGeneratedOnAdd();

                // The ModuleDefinition table's `version` column is a STRING, which AggregateRoot<TId>.Version (a
                // uint concurrency token) shadows by name — the SiteAggregate.SiteVersion / ThemeAggregate.
                // ThemeVersion situation. ModuleDefinitionVersion carries the string onto the `version` column;
                // an explicit HasColumnName is required because the property name does not match the column.
                entity.Property(item => item.ModuleDefinitionVersion).HasColumnName(database.RewriteName("Version"));

                // As every other cluster: AggregateRoot<TId>.Version (the uint token) has no column in the
                // harvested schema, so no optimistic concurrency on ModuleDefinition either — parity with the
                // IDatabase path. Phase 5 owns the column. ModuleDefinitionName/Name/Description/Categories are
                // left to convention (they match their columns).
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            // Tenant + Alias — the routing core, and the FINAL master-bookkeeping cluster. MASTER-
            // side ONLY like Theme, ModuleDefinition and the Job/JobLog pair: the harvested MasterDBContext held
            // DbSet<Tenant> and DbSet<Alias>, the master migration set creates both tables, and no tenant database
            // has them. So they are mapped HERE and NOT in ConfigureFrameworkStore. When these land, the compiler's
            // proof that the move is complete is that MasterDBContext holds ZERO content DbSets.
            //
            // Both verified column-for-column against the live migrated PostgreSQL database (stomme-pg-44357, host
            // 45432), where the two EntityBuilders LIE by omission:
            //   * tenant has EXACTLY 9 columns — tenant_id, name (NOT NULL, UNIQUE ix_tenant), db_connection_string
            //     (NOT NULL), NULLABLE version + db_type, and the audit quartet (all NOT NULL). db_type is NOT in
            //     TenantEntityBuilder (migration 02010001 adds it) yet is present and load-bearing — it selects the
            //     tenant's persistence provider. version is a STRING that AggregateRoot.Version (uint) shadows.
            //   * alias has EXACTLY 9 columns — alias_id, name (NOT NULL, UNIQUE ix_alias), tenant_id + site_id (NOT
            //     NULL), NULLABLE is_default, and the audit quartet (all NOT NULL). is_default is NOT in
            //     AliasEntityBuilder (migration 03000201 adds it) yet is present. Alias has NO version column.
            //
            // Both are IAuditable and NEITHER is IDeletable (no is_deleted/deleted_by/deleted_on in either live table).
            // No navigation is mapped in either direction: the relational FK_Alias_Tenant stays in the DB (the
            // migration created it, nothing here drops it) but the MODEL references the tenant by id only.
            // Alias has no Tenant/Site navigation on its contract, so — unlike SiteGroup or Folder/File
            // pairs — there was no mapped navigation to sever and no model-rediscovery blocker to hit.
            builder.Entity<TenantAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Tenant"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("TenantId"))
                    .ValueGeneratedOnAdd();

                // The tenant table's `version` column is a STRING, which AggregateRoot<TId>.Version (a uint
                // concurrency token) shadows by name — the ThemeAggregate.ThemeVersion situation. TenantVersion
                // carries the string onto the `version` column; an explicit HasColumnName is required because the
                // property name does not match the column.
                entity.Property(item => item.TenantVersion).HasColumnName(database.RewriteName("Version"));

                // AggregateRoot<TId>.Version (the uint token) has no column in the harvested schema, so no optimistic
                // concurrency on Tenant either — parity with the IDatabase path. Name/DBConnectionString/DBType are
                // left to convention (they snake_case to name/db_connection_string/db_type, matching the live table).
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });

            builder.Entity<AliasAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Alias"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("AliasId"))
                    .ValueGeneratedOnAdd();

                // Alias has NO `version` column, so nothing shadows AggregateRoot<TId>.Version — the base uint token
                // is simply ignored (no column) as on every leaf cluster. Name/TenantId/SiteId/IsDefault are left to
                // convention (name/tenant_id/site_id/is_default, matching the live table). NOTE what is NOT
                // configured: a HasOne(...).WithMany() to TenantAggregate — the DB keeps its cascading FK_Alias_Tenant
                // on tenant_id, but the model references the tenant by id only, and mapping the relationship
                // would put a navigation back that has no contract property to hang on anyway.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });
        }

        /// <summary>
        /// The single mapping of <see cref="SettingAggregate"/>, shared by the tenant and master contexts so
        /// the two can never drift apart. Pinned by
        /// <c>TheMasterAndTenantSettingStoresTests.The_master_and_tenant_contexts_map_the_setting_aggregate_identically</c>.
        /// </summary>
        private static void ConfigureSettingAggregate(this ModelBuilder builder, IRelationalDatabase database)
        {
            builder.Entity<SettingAggregate>(entity =>
            {
                entity.ToTable(database.RewriteName("Setting"));
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id)
                    .HasColumnName(database.RewriteName("SettingId"))
                    .ValueGeneratedOnAdd();

                // See the SiteAggregate note: no column in the harvested schema, so no optimistic concurrency
                // on Setting either — parity with the IDatabase path. Phase 5 owns the column.
                entity.Ignore(item => item.Version);
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            });
        }
    }
}
