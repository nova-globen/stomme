#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Providers;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A search entry belongs to its site (BL-251, BL-239's E2#3): replacing or deleting one site's entry for an entity leaves
    /// another site's entry for the same entity name and id alone. The table's unique index has always been
    /// (SiteId, EntityName, EntityId); the delete before every save matched (EntityName, EntityId) only, so where two sites
    /// in one database indexed an entity with the same id, saving one site's entry removed the other's.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SearchSiteScopeTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task Saving_and_deleting_one_sites_entry_leaves_another_sites_entry_for_the_same_entity()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                // Each step in a scope of its own, as each save is in production (the index job saves an entity once per run).
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var repository = sp.GetRequiredService<ISearchContentRepository>();
                    // The other site's entry first: the old delete took the FIRST entry of that name and id, whatever its site.
                    await repository.AddSearchContentAsync(Entry(alias.SiteId + 100, "theirs"));
                    await repository.AddSearchContentAsync(Entry(alias.SiteId, "first"));
                    return 0;
                });

                // The site's own save: a delete of its old entry, then the new one.
                var afterSave = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await new DatabaseSearchProvider(sp.GetRequiredService<ISearchContentRepository>()).SaveSearchContent(Entry(alias.SiteId, "second"), new Dictionary<string, string>());
                    return 0;
                });
                var titlesAfterSave = await GateDrive.InTenantAsync(app, Host, (sp, _) => Titles(sp));

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetRequiredService<ISearchContentRepository>().DeleteSearchContentAsync(alias.SiteId, "Note", "42");
                    return 0;
                });
                var titlesAfterDelete = await GateDrive.InTenantAsync(app, Host, (sp, _) => Titles(sp));

                output.WriteLine($"after save: {string.Join(", ", titlesAfterSave)}; after delete: {string.Join(", ", titlesAfterDelete)}");
                Assert.Equal(new[] { "second", "theirs" }, titlesAfterSave);
                Assert.Equal(new[] { "theirs" }, titlesAfterDelete);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static SearchContent Entry(int siteId, string title) =>
            new(siteId, "Note", "42", title, title, title + " body", "/notes/42", "", "host", DateTime.UtcNow, isDeleted: false) { AdditionalContent = "" };

        private static async Task<List<string>> Titles(IServiceProvider sp)
        {
            await using var session = sp.GetRequiredService<IFrameworkStoreSessionFactory>().OpenQuerySession();
            return session.Query<SearchContentAggregate>().Where(item => item.EntityName == "Note" && item.EntityId == "42")
                .AsEnumerable().Select(item => item.Title).OrderBy(title => title, StringComparer.Ordinal).ToList();
        }
    }
}
