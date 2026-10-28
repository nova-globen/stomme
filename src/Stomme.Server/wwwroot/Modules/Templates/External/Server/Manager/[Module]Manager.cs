using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Interfaces;
using Stomme.Models;
using [Owner].Module.[Module].Persistence;

namespace [Owner].Module.[Module].Manager
{
    /// <summary>
    /// The module's SERVER MANAGER - the type the module definition's <c>ServerManagerType</c> names, which the host creates
    /// wherever it asks a placed module for something: here the site search's index job (<see cref="ISearchable"/>). A
    /// manager can also answer the sitemap (<c>ISitemap</c>) and the module's content export and import (<c>IPortable</c>);
    /// the KnowledgeBase exemplar shows all three.
    /// </summary>
    public sealed class [Module]Manager(I[Module]EntryRepository entries) : ISearchable
    {
        public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn) =>
            (await entries.ListForSiteAsync(pageModule.Module.SiteId))
                .Where(entry => entry.CreatedUtc >= lastIndexedOn)
                .Select(entry => new SearchContent
                {
                    EntityName = "[Module]Entry",
                    EntityId = entry.Id.ToString(),
                    Title = entry.Text.Length <= 80 ? entry.Text : entry.Text.Substring(0, 80),
                    Body = entry.Text,
                    ContentModifiedOn = entry.CreatedUtc,
                })
                .ToList();
    }
}
