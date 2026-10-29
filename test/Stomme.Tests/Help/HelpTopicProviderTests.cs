#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Models;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Repository;
using Stomme.Tests.Fixtures.HelpTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-84 increment 6 (D103 items 4 and 12): the provider - the port the launcher, the controller and BL-97's assistant
    /// read through - returns PUBLISHED topics of the CURRENT site and tenant only, walks ONE locale chain, and renders
    /// through the allowlist with a cache key that cannot go stale.
    /// </summary>
    /// <remarks>
    /// D103 refutation 12: the predecessor platform's provider returned every topic on the site, drafts included, from outside the
    /// mediator's authorization behavior - so an assistant would have quoted unpublished text to a user. The isolation
    /// case runs on SQLite, PostgreSQL and RavenDB, with TWO TENANTS on ONE site id; PostgreSQL and RavenDB FAIL rather
    /// than skip.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class HelpTopicProviderTests
    {
        private readonly ITestOutputHelper _output;

        public HelpTopicProviderTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task It_returns_PUBLISHED_topics_of_the_CURRENT_site_and_tenant_and_nothing_else(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, HelpDrive.Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                var (first, second) = await HelpDrive.AddSecondTenantAsync(app);
                var report = await HelpDrive.RunReconcilersAsync(app);
                Assert.Empty(report.Failures);

                // Tenant 1 writes a DRAFT whose text contains the search word; tenant 2 edits the SHARED anchor's topic.
                await HelpDrive.InSiteAsync(app, first, async sp =>
                {
                    await sp.GetRequiredService<IHelpTopicRepository>().AddAsync(HelpTopicAggregate.Author(
                        first.TenantId, first.Alias.SiteId, "page:secret", "en", "Secret", "An unpublished widget plan.", DateTime.UtcNow));
                    return true;
                });
                await HelpDrive.InSiteAsync(app, second, async sp =>
                {
                    var repository = sp.GetRequiredService<IHelpTopicRepository>();
                    var widget = (await repository.GetAsync(HelpTestContributor.Anchor, "en"))!;
                    widget.UpdateContent("Tenant two's widget", "Only tenant **two** sees this.", DateTime.UtcNow);
                    await repository.UpdateAsync(widget);
                    return true;
                });

                // ---- tenant 1 --------------------------------------------------------------------------------------------
                await HelpDrive.InSiteAsync(app, first, async sp =>
                {
                    var provider = sp.GetRequiredService<IHelpTopicProvider>();

                    Assert.Null(await provider.GetAsync("page:secret", "en"));                    // a draft is not help
                    var widget = (await provider.GetAsync(HelpTestContributor.Anchor, "en"))!;
                    Assert.Equal("The widget", widget.Title);                                     // not tenant 2's edit
                    Assert.Contains("<strong>widget</strong>", widget.SafeHtml, StringComparison.Ordinal);

                    var hits = await provider.SearchAsync("widget");
                    _output.WriteLine($"[{storeName}] tenant 1 search: {string.Join(", ", hits.Select(hit => $"{hit.Anchor}/{hit.Locale}"))}");
                    Assert.NotEmpty(hits);
                    Assert.All(hits, hit => Assert.True(hit.IsPublished));
                    Assert.DoesNotContain(hits, hit => hit.Anchor == "page:secret");
                    Assert.DoesNotContain(hits, hit => hit.Title.Contains("Tenant two", StringComparison.Ordinal));

                    // By the BODY alone ("does one thing" is not in any title) - the predecessor platform's Search_FindsTopicsByContent.
                    Assert.Contains(await provider.SearchAsync("does one thing"), hit => hit.Anchor == HelpTestContributor.Anchor && hit.Locale == "en");
                    // By the TITLE alone: the Theme Management topic's own body never says "Theme Management", so its hit
                    // is a title match (the dashboard topic's body does say it, and may match too - that is by content).
                    Assert.Contains(await provider.SearchAsync("Theme Management"), hit => hit.Title == "Theme Management");
                    return true;
                });

                // ---- tenant 2, same site id --------------------------------------------------------------------------------
                await HelpDrive.InSiteAsync(app, second, async sp =>
                {
                    var provider = sp.GetRequiredService<IHelpTopicProvider>();
                    Assert.Equal("Tenant two's widget", (await provider.GetAsync(HelpTestContributor.Anchor, "en"))!.Title);
                    Assert.Null(await provider.GetAsync("page:secret", "en"));                    // tenant 1's row, even if it were published
                    Assert.DoesNotContain(await provider.SearchAsync("unpublished"), hit => hit.Anchor == "page:secret");
                    return true;
                });

                // ---- publishing the draft is what makes it help ---------------------------------------------------------
                await HelpDrive.InSiteAsync(app, first, async sp =>
                {
                    var repository = sp.GetRequiredService<IHelpTopicRepository>();
                    var secret = (await repository.GetAsync("page:secret", "en"))!;
                    secret.Publish(DateTime.UtcNow);
                    await repository.UpdateAsync(secret);
                    return true;
                });
                Assert.Equal("Secret", (await HelpDrive.InSiteAsync(app, first, sp => sp.GetRequiredService<IHelpTopicProvider>().GetAsync("page:secret", "en")))!.Title);
                Assert.Null(await HelpDrive.InSiteAsync(app, second, sp => sp.GetRequiredService<IHelpTopicProvider>().GetAsync("page:secret", "en")));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task ONE_locale_chain_exact_then_parent_then_the_sites_default_then_English()
        {
            await WithHostAsync(async (app, site) =>
            {
                // A site whose DEFAULT language is German, so every step of the chain is distinguishable.
                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    await sp.GetRequiredService<ILanguageRepository>().AddLanguageAsync(new Language
                    {
                        SiteId = site.Alias.SiteId, Code = "de", Name = "Deutsch", IsDefault = true, Version = "1.0.0",
                    });

                    var repository = sp.GetRequiredService<IHelpTopicRepository>();
                    foreach (var (locale, title) in new[] { ("sv", "Svenska"), ("de", "Deutsch"), ("en", "English") })
                    {
                        var topic = HelpTopicAggregate.Author(site.TenantId, site.Alias.SiteId, "page:chain", locale, title, title + " text.", DateTime.UtcNow);
                        topic.Publish(DateTime.UtcNow);
                        await repository.AddAsync(topic);
                    }

                    // A DRAFT in the exact locale must not hide the published one further down the chain.
                    await repository.AddAsync(HelpTopicAggregate.Author(site.TenantId, site.Alias.SiteId, "page:chain", "sv-se", "Utkast", "Draft.", DateTime.UtcNow));
                    return true;
                });

                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var provider = (HelpTopicProvider)sp.GetRequiredService<IHelpTopicProvider>();

                    Assert.Equal(new[] { "sv-se", "sv", "de", "en" }, await provider.LocaleChainAsync("sv-SE", site.Alias.SiteId));
                    Assert.Equal(new[] { "de", "en" }, await provider.LocaleChainAsync(null, site.Alias.SiteId));
                    Assert.Equal(new[] { "de", "en" }, await provider.LocaleChainAsync("not a locale", site.Alias.SiteId));

                    var fromParent = (await provider.GetAsync("page:chain", "sv-SE"))!;
                    Assert.Equal("sv", fromParent.Locale);                                        // the sv-se DRAFT did not answer
                    Assert.Equal("Svenska", fromParent.Title);

                    Assert.Equal("de", (await provider.GetAsync("page:chain", "fr-FR"))!.Locale);  // the site's default
                    Assert.Equal("de", (await provider.GetAsync("page:chain"))!.Locale);
                    Assert.Equal("en", (await provider.GetAsync(HelpTestContributor.Anchor, "fr-FR"))!.Locale);   // no de topic: English
                    Assert.Equal("sv-se", (await provider.GetAsync(HelpTestContributor.Anchor, "sv-SE"))!.Locale); // the fixture's exact one
                    return true;
                });
            });
        }

        [Fact]
        public async Task An_edit_is_what_the_next_read_shows_because_the_cache_key_carries_the_version()
        {
            await WithHostAsync(async (app, site) =>
            {
                var before = await HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IHelpTopicProvider>().GetAsync(HelpTestContributor.Anchor, "en"));
                Assert.Contains("<strong>widget</strong>", before!.SafeHtml, StringComparison.Ordinal);

                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var repository = sp.GetRequiredService<IHelpTopicRepository>();
                    var widget = (await repository.GetAsync(HelpTestContributor.Anchor, "en"))!;
                    widget.UpdateContent("The widget", "Now with a [link](https://example.com).", DateTime.UtcNow);
                    await repository.UpdateAsync(widget);
                    return true;
                });

                var after = await HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IHelpTopicProvider>().GetAsync(HelpTestContributor.Anchor, "en"));
                _output.WriteLine(after!.SafeHtml);
                Assert.DoesNotContain("<strong>widget</strong>", after.SafeHtml, StringComparison.Ordinal);
                Assert.Contains("rel=\"" + HelpHtmlSanitizer.ExternalRel + "\"", after.SafeHtml, StringComparison.Ordinal);
            });
        }

        [Fact]
        public async Task Wire_garbage_and_a_missing_site_answer_NO_TOPIC_rather_than_throwing()
        {
            await WithHostAsync(async (app, site) =>
            {
                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var provider = sp.GetRequiredService<IHelpTopicProvider>();
                    Assert.Null(await provider.GetAsync("has space", "en"));
                    Assert.Null(await provider.GetAsync(new string('a', HelpKeys.MaximumAnchorLength + 1), "en"));
                    Assert.Null(await provider.GetAsync(null!, "en"));
                    Assert.Null(await provider.GetAsync("page:never-contributed", "en"));
                    Assert.Empty(await provider.SearchAsync("   "));
                    Assert.Empty(await provider.SearchAsync(null!));
                    return true;
                });

                // A scope with NO resolved alias: nothing, not everything.
                using var bare = app.Services.CreateScope();
                var unrouted = bare.ServiceProvider.GetRequiredService<IHelpTopicProvider>();
                Assert.Null(await unrouted.GetAsync(HelpTestContributor.Anchor, "en"));
                Assert.Empty(await unrouted.SearchAsync("widget"));
            });
        }

        private async Task WithHostAsync(Func<WebApplication, HelpSite, Task> work)
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), HelpDrive.Host, colocateModuleAggregates: false,
                extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                await work(app, await HelpDrive.SiteAsync(app, HelpDrive.Host));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
