using Microsoft.EntityFrameworkCore;
using Stomme.Databases.Interfaces;
using Stomme.Documentation;
using Stomme.Repository;
using Stomme.Repository.Databases.Interfaces;
using Stomme.SharedKernel.Domain;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The HtmlText module's relational context. Since D55 it maps the <see cref="HtmlTextAggregate"/> onto the
    /// existing <c>html_text</c> table (D3 — no schema change) rather than the shared wire model, so a migrated
    /// cluster reads and writes through <see cref="IHtmlTextStore"/> and <see cref="IHtmlTextStoreSessionFactory"/>
    /// exactly as the framework store's aggregates do through <c>TenantDBContext</c>. It still IS the module's EF
    /// migration target: <c>HtmlTextManager.InstallAsync</c> runs the EF migrator over this context on a relational
    /// tenant only.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class HtmlTextContext : DBContextBase, ITransientService, IMultiDatabase
    {
        public HtmlTextContext(IDBContextDependencies DBContextDependencies) : base(DBContextDependencies) { }

        public virtual DbSet<HtmlTextAggregate> HtmlText { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Relational-only (D53/D55): ActiveDatabase stays IDatabase for IMultiDatabase; on the only path that
            // reaches OnModelCreating it is a relational provider. A non-relational provider (RavenDatabase) never
            // resolves this context — its store is RavenHtmlTextStore — so it skips.
            if (ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                builder.Entity<HtmlTextAggregate>(entity =>
                {
                    // The aggregate maps onto the existing html_text table the module migration creates. HtmlTextId
                    // is the AggregateRoot<int>.Id column; ModuleId/Content and the audit quartet are left to
                    // convention (module_id/content/created_*/modified_*), matching HtmlTextEntityBuilder — and an
                    // explicit HasColumnName OUTRANKS the snake_case convention, hence RewriteName on the key.
                    entity.ToTable(relationalDatabase.RewriteName("HtmlText"));
                    entity.HasKey(item => item.Id);
                    entity.Property(item => item.Id)
                        .HasColumnName(relationalDatabase.RewriteName("HtmlTextId"))
                        .ValueGeneratedOnAdd();

                    // No concurrency column in the harvested schema (parity with the framework clusters); domain
                    // events are an in-memory concern, never persisted.
                    entity.Ignore(item => item.Version);
                    entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
                });
            }
        }
    }
}
