using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Databases.Interfaces;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Migrations.Framework;
using Stomme.Models;
using Stomme.Repository.Databases.Interfaces;
using Stomme.Repository.Stores;
using Stomme.Shared;

// ReSharper disable CheckNamespace
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Stomme.Repository
{
    public class TenantDBContext : IdentityUserContext<IdentityUser>, IMultiDatabase
    {
        private readonly ITenantManager _tenantManager;
        private readonly IHttpContextAccessor _accessor;
        private readonly IConfigurationRoot _config;
        private readonly IConfigManager _configManager;
        private string _connectionString = "";
        private string _databaseType = "";

        public TenantDBContext(DbContextOptions<TenantDBContext> options, IDBContextDependencies DBContextDependencies) : base(options)
        {
            _tenantManager = DBContextDependencies.TenantManager;
            _accessor = DBContextDependencies.Accessor;
            _config = DBContextDependencies.Config;
            _configManager = DBContextDependencies.ConfigManager;
        }

        public IDatabase ActiveDatabase { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ReplaceService<IMigrationsAssembly, MultiDatabaseMigrationsAssembly>();

            if (string.IsNullOrEmpty(_connectionString))
            {
                // Sync and reads NO store (D37) — see the note in DBContextBase.OnConfiguring. This context is
                // the one that matters most: ASP.NET Identity's UserStore constructor-injects it on the live
                // authentication path, which is sync DI with nowhere to await.
                Tenant tenant = _tenantManager.GetResolvedTenant();
                if (tenant != null)
                {
                    _connectionString = _config.GetConnectionString(tenant.DBConnectionString);
                    if (_connectionString != null)
                    {
                        // THIS host's data directory (BL-153) - see IConfigManager.GetDataDirectory.
                        _connectionString = _connectionString.Replace($"|{Constants.DataDirectory}|", _configManager.GetDataDirectory());
                        _databaseType = tenant.DBType;
                    }
                    else
                    {
                        // tenant connection string does not exist in appsettings.json
                    }
                }
            }

            if (!string.IsNullOrEmpty(_databaseType))
            {
                var type = Type.GetType(_databaseType);
                ActiveDatabase = Activator.CreateInstance(type) as IDatabase;
            }

            // specify the SchemaVersion for .NET Identity as it is not being persisted when using AddIdentityCore()
            var services = new ServiceCollection();
            services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            });
            optionsBuilder.UseApplicationServiceProvider(services.BuildServiceProvider());

            // Relational-only EF configuration (D53); ActiveDatabase stays IDatabase for IMultiDatabase (D55).
            if (!string.IsNullOrEmpty(_connectionString) && ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                optionsBuilder.UseStommeDatabase(relationalDatabase, _connectionString);
            }

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // EF's own ledger has no key. Said here and not by [Keyless] on the model, which made Stomme.Shared reference
            // EF Core - and every WebAssembly site's browser download it (BL-206's payload, 2026-10-03).
            builder.Entity<MigrationHistory>().HasNoKey();

            // Relational-only (D53): this context IS the ASP.NET Identity EF store and its aggregates map onto
            // relational tables, so both calls need the relational surface. ActiveDatabase stays IDatabase for
            // IMultiDatabase (D55); on the only path that reaches OnModelCreating today it is a relational
            // provider. A non-relational provider (RavenDatabase) skips — this context is not its store.
            if (ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                relationalDatabase.UpdateIdentityStoreTableNames(builder);

                // Framework-store clusters that have moved onto the persistence seam (D17). They share this
                // context with the DbSets below, so a migrated cluster keeps the same per-request connection,
                // audit hooks and migration set as an unmigrated one.
                builder.ConfigureFrameworkStore(relationalDatabase);
            }
        }

        public override int SaveChanges()
        {
            DbContextUtils.SaveChanges(this, _accessor);

            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            DbContextUtils.SaveChanges(this, _accessor);

            return base.SaveChangesAsync(cancellationToken);
        }

        // NOTE: there is deliberately no DbSet<Site>, no DbSet<Page>, no DbSet<Module>, no DbSet<User>, no
        // DbSet<Role>, no DbSet<UserRole>, no DbSet<Setting>, no DbSet<Permission> and — since D44 — no
        // DbSet<Language>, no DbSet<Log>, no DbSet<Profile>, no DbSet<SiteTask> and no DbSet<Notification>.
        // Those clusters are on the seam (D17/D19/D21/D22/D23/D24/D44): they are mapped by
        // ConfigureFrameworkStore as SiteAggregate/PageAggregate/ModuleAggregate/UserAggregate/RoleAggregate/
        // UserRoleAggregate/SettingAggregate/PermissionAggregate/LanguageAggregate/LogAggregate/
        // ProfileAggregate/SiteTaskAggregate/NotificationAggregate and reached only through ISiteStore /
        // IPageStore / IModuleStore / IUserAccountStore / ISecurityRoleStore / IUserRoleAssignmentStore /
        // ISettingStore / IPermissionStore / ILanguageStore / ILogStore / IProfileStore / ISiteTaskStore /
        // INotificationStore / IFrameworkStoreSessionFactory. Removing the DbSet is what makes the compiler
        // prove no caller bypasses the seam back to the context.
        //
        // The D44 five are LEAF clusters — tenant-side only, one table each, nothing navigating in or out — so
        // like Permission they have no master half and no IsMaster routing. Two of them break a pattern the
        // first eight established, and both breaks are load-bearing: LogAggregate is NOT IAuditable (the Log
        // table has no audit columns at all) and NotificationAggregate is IDeletable rather than IAuditable.
        // DbContextUtils decides both by RUNTIME TYPE TEST against the entity this context tracks, so getting
        // either wrong compiles cleanly and silently stops stamping. See the remarks on those aggregates.
        //
        // Permission (D24) is TENANT-ONLY — unlike Setting it has no master half; MasterDBContext never mapped
        // it and the master migration set never created the table. Deleting its DbSet also removed the LAST
        // IDbContextFactory<TenantDBContext> from RoleRepository and UserRepository, which had kept one purely
        // for db.Permission. See src/Stomme.Server/README.md (persistence seam).
        //
        // Setting is mapped in MasterDBContext TOO (ConfigureMasterFrameworkStore) — it is the only framework
        // -store aggregate that lives in both databases, because SettingRepository routes Host/Job/
        // ModuleDefinition/Theme/master:* settings to the master database and everything else here. Two
        // stores, one aggregate. See src/Stomme.Server/README.md (persistence seam).
        //
        // A DbSet is necessary but NOT sufficient for that proof: an EF navigation from any entity below
        // into a migrated aggregate's CLR type resurrects it as a second entity type on the same table and
        // breaks the model at startup. That is why PageModule.Page, PageModule.Module, UserRole.User,
        // UserRole.Role AND Visitor.User are [NotMapped] — see the remarks there, and
        // PageModuleMustNotResurrectPageTests / ModuleMustNotBeResurrectedTests /
        // UserRoleMustNotResurrectUserOrRoleTests / VisitorMustNotResurrectUserTests, which pin all of them.
        //
        // D47 took Visitor, UrlMapping and PageModule — mapped now as VisitorAggregate/UrlMappingAggregate/
        // PageModuleAggregate. That group was chosen precisely because none of them needed a navigation
        // severed FOR it: D22 had already cut Visitor.User, D19 and D21 had already cut PageModule.Page and
        // PageModule.Module while deliberately leaving the PageModule row here, and UrlMapping never had one.
        // So all three were row moves. PageModuleAggregate is the first on the seam to be BOTH IAuditable and
        // IDeletable — the two runtime type tests above apply to it at once.
        //
        // NOTE ALSO: this context IS the ASP.NET Identity store (it derives from
        // IdentityUserContext<IdentityUser>, and AddIdentityCore<IdentityUser>()
        // .AddEntityFrameworkStores<TenantDBContext>() is still the live authentication path). Identity's EF
        // model and the framework store's aggregates coexist here because they are DISJOINT: Identity maps
        // IdentityUser/IdentityUserClaim/IdentityUserLogin/IdentityUserToken onto the AspNetUser* tables,
        // ConfigureFrameworkStore maps Stomme's own tables, and no navigation crosses between them. The
        // Stomme User row and Identity's AspNetUsers row are correlated BY USERNAME in application code.
        // Do not add a navigation between the two models; that is an identity cutover, not a mapping fix.
        // D48 took FOLDER and D49 took FILE — mapped now as FolderAggregate/FileAggregate, reached through
        // IFolderStore/IFileStore and the session factory.
        //
        // The File half was ESCALATED by D48 rather than shipped, and the reason is worth keeping because it
        // is the only blocker on this seam that was resolved by changing the DATA rather than the code:
        // FileRepository's two name lookups filtered with .ToLower() on the COLUMN. That was load-bearing on
        // the relational providers (the live database stores mixed-case names and paths — 'Public/', 'Users/1/'
        // — while the render path lowercases the URL), and RavenDB's LINQ provider THROWS on it rather than
        // mistranslating. The portable spelling was no substitute: plain == is case-SENSITIVE on relational and
        // case-INSENSITIVE on RavenDB. D49 added path_normalized/name_normalized — Identity's
        // NormalizedUserName split, which this very context already runs — so the reads filter a lowercase
        // column with plain ==, which both providers agree on. See src/Stomme.Server/README.md (persistence seam).
        //
        // D50 took the SEARCHCONTENT cluster — Phase 3's LAST — mapped now as SearchContentAggregate /
        // SearchContentPropertyAggregate / SearchContentWordAggregate / SearchWordAggregate, reached through
        // ISearchContentStore / ISearchContentPropertyStore / ISearchContentWordStore / ISearchWordStore, the
        // session factory, and — uniquely on this seam — ISearchQueryStore.
        //
        // ISearchQueryStore is the only closed contract here that is NOT a repository, and the reason is the
        // cluster's whole story: the search read filters SearchWord.Word by PREFIX and sums
        // SearchContentWord.Count per content, which is an aggregation over a subset chosen at RUNTIME by the
        // caller's keywords. RavenDB serves a dynamic GroupBy+Sum only while every filter arm touches the
        // group-by key, and this one does not. So the seam's LINQ read surface cannot express it, and the two
        // providers implement it differently ON PURPOSE: EF keeps GROUP BY + SUM in SQL; RavenDB uses a
        // query-time FACET (AggregateBy/SumOn) over a static LoadDocument index. Same set, same Count, every
        // filter arm and the sum in the store on both.
        //
        // Two mechanisms were MEASURED and rejected, and both look obvious enough to be tried again: a
        // MAP-REDUCE index aggregates under a reduce key fixed at index-definition time, so it cannot sum over
        // a keyword-chosen subset; and a NATIVE FULL-TEXT index is not the same relation as StartsWith in
        // either direction — its analyzer tokenizes hyphens, which FormatContent deliberately does not, so it
        // MISSES 'state-of*' and INVENTS 'art*'. That is why SearchContentWordIndex uses the DEFAULT analyzer
        // on the word field. Do not "improve" it to FieldIndexing.Search.
        //
        // The three navigations that would resurrect these contracts through the tenant model —
        // SearchContent.SearchContentProperties, SearchContentWord.SearchWord and SearchContentWord
        // .SearchContent — are [NotMapped] (D45's mechanism). See src/Stomme.Server/README.md (persistence seam).
        //
        // What is left below is MigrationHistory ALONE, which is EF's own [Keyless] ledger and stays here
        // permanently (D43). That is the end state Phase 3 was aiming at: this context maps no Stomme entity.
        public virtual DbSet<MigrationHistory> MigrationHistory { get; set; }

        // SiteGroup and SiteGroupMember are GONE from this context (D45) — they are on the seam, mapped by
        // ConfigureFrameworkStore as SiteGroupAggregate/SiteGroupMemberAggregate. They had to leave TOGETHER:
        // while SiteGroupMember stayed here, its (then mapped) SiteGroup navigation made EF rediscover the
        // SiteGroup contract and claim the table SiteGroupAggregate owns, and the model failed at startup.
        // That navigation is now [NotMapped] view state SiteGroupMemberRepository populates by hand.
    }
}
