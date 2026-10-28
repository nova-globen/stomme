using System;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The HtmlText aggregate root — the persistence-side model of the module's <c>html_text</c> table, reachable
    /// only through <see cref="IHtmlTextStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads). The
    /// module's LEAF cluster on the persistence seam (D55), mapped onto the EXISTING table the EF migration creates
    /// (D3 — no schema change), so a relational install and a document install serve the same content with no data
    /// migration between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>Permission</c>/D24 aggregate+mapper pattern, NOT a "move a DbSet" job: HtmlText's wire model
    /// (<see cref="Models.HtmlText"/>) is a SHARED contract (<c>src/Stomme.Shared</c>) the client and controllers
    /// exchange, so the persisted shape is a separate type mapped at the <see cref="HtmlTextRepository"/> boundary
    /// exactly as <c>PermissionAggregate</c> is.
    /// </para>
    /// <para>
    /// <b><see cref="IAuditable"/>, NOT <see cref="IDeletable"/>.</b> The table's builder
    /// (<c>HtmlTextEntityBuilder</c>) extends <c>AuditableBaseEntityBuilder</c> and calls
    /// <c>AddAuditableColumns</c> — the audit quartet only; there is no <c>is_deleted</c>. <c>DbContextUtils</c>
    /// decides which stamps to apply by RUNTIME TYPE TEST, so declaring <see cref="IDeletable"/> here would compile
    /// and then write columns that do not exist (the D44 trap). One module keeps MANY versioned rows
    /// (<c>Versions</c> setting, default 5); the current content is the most recent by <see cref="CreatedOn"/>.
    /// </para>
    /// </remarks>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class HtmlTextAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// The <c>Module</c> instance this content belongs to. A reference to another entity BY ID (D7/D19) — the
        /// harvested schema carries an <c>FK_HtmlText_Module</c> cascade, which is unchanged: this is a mapping
        /// change, not a schema change.
        /// </summary>
        public int ModuleId { get; set; }

        /// <summary>The rendered HTML body.</summary>
        public string Content { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Models.HtmlText"/>
        /// for a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int htmlTextId) => Id = htmlTextId;
    }
}
