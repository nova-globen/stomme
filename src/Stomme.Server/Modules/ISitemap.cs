using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Modules
{
    /// <summary>
    /// A module's entries in the site's sitemap (<c>/sitemap.xml</c>): the addresses of what it shows beyond its page - one
    /// per published article, say. The host asks every placed module a visitor may view whose
    /// <c>ModuleDefinition.ServerManagerType</c> implements this, creating that manager with <c>ActivatorUtilities</c>.
    /// </summary>
    /// <remarks>
    /// <b>Asynchronous since 2026-09-27</b> (BL-86 increment 4, D33's correction): the contract was synchronous, so a module
    /// whose entries live in a store could implement it only by blocking on its own reads, which D33 bans. Nothing in the
    /// tree implemented it; the exemplar module (<c>examples/Modules/KnowledgeBase</c>) is its first implementation.
    /// </remarks>
    public interface ISitemap
    {
        /// <param name="alias">The alias's path - empty for an alias at a host's root.</param>
        /// <param name="path">The path of the page the module is placed on.</param>
        /// <param name="module">The placed module, its settings loaded.</param>
        /// <returns>
        /// Each entry's URL from the site's root - <c>Utilities.NavigateUrl(alias, path, ...)</c> builds it - and when it last
        /// changed (the host stamps "now" on an entry that leaves it unset).
        /// </returns>
        Task<List<Sitemap>> GetUrlsAsync(string alias, string path, Module module);
    }
}
