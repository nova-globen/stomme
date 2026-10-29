using Stomme.Documentation;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The per-aggregate store for <see cref="HtmlTextAggregate"/> — the HtmlText module's cluster on the
    /// persistence seam, and the REFERENCE every downstream module copies. Provider-selected at resolve time
    /// (<c>EfHtmlTextStore</c> or <c>RavenHtmlTextStore</c>) keyed on the configured database type, exactly as the
    /// framework store's <c>ISiteStore</c> is.
    /// </summary>
    /// <remarks>
    /// A per-aggregate CLOSED interface rather than the open-generic <see cref="IRepository{TAggregate, TId}"/>, for
    /// the reason documented at length on the framework's <c>ISiteStore</c>:
    /// <c>AddFeatureManagement</c> owns the open-generic <see cref="IRepository{TAggregate, TId}"/> in this host, so
    /// a second open-generic registration would be last-wins and silently re-point every live feature flag. A module
    /// MUST register its own closed store and session factory — never the ambient ones. This is a MODULE-owned
    /// contract in <c>Stomme.Modules.HtmlText.Repository</c>, deliberately NOT the framework's
    /// <c>IFrameworkStoreSessionFactory</c>: reusing that would map a module aggregate into the framework's
    /// <c>TenantDBContext</c>, coupling framework to module.
    /// </remarks>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public interface IHtmlTextStore : IRepository<HtmlTextAggregate, int>
    {
    }

    /// <summary>
    /// Opens sessions over the HtmlText module's store for read queries (reads NEVER go through the store, so
    /// the store's change tracker stays clean for a later keyed write). A MODULE-owned closed session factory, the
    /// twin of the framework's <c>IFrameworkStoreSessionFactory</c>; on the document path it binds to the same
    /// framework (tenant) database that is already ensured, and on the relational path to the module's own
    /// <see cref="HtmlTextContext"/>.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public interface IHtmlTextStoreSessionFactory : IDocumentSessionFactory
    {
    }
}
