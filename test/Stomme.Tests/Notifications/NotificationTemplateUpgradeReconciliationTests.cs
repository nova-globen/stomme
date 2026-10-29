using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// A built-in notification template a NEWER release declares
    /// reaches an ALREADY-INSTALLED site on the boot path, and an operator's edit to an existing template survives - driven
    /// the only way that class of defect shows, TWO hosts over ONE database (the <c>AdminPageUpgradeReconciliationTests</c>
    /// / <c>PermissionKeyUpgradeReconciliationTests</c> shape), never a fresh install.
    /// <para>
    /// <b>The boot path is observed BEFORE the hosted services start</b> (<c>start: false</c>): that is where
    /// <c>Program.Main</c> stands after <c>InstallAsync</c> and before <c>RunAsync</c>, and where the module store's
    /// tables may not exist yet. SQLite runs the production default (the module-store SQLite sidecar); PostgreSQL
    /// co-locates the module store in the install's own database so the leg is PostgreSQL end to end; RavenDB is
    /// schemaless and runs too. PostgreSQL and RavenDB FAIL rather than skip.
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class NotificationTemplateUpgradeReconciliationTests
    {
        private const string Host = "localhost";
        internal const string SecondKey = "upgrade.second-builtin";
        private const string OperatorSubject = "Operator edited {{ subject }}";

        private readonly ITestOutputHelper _output;

        public NotificationTemplateUpgradeReconciliationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_later_builtin_reaches_a_preinstalled_site_and_an_operator_edit_survives(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName);

            // ---- Phase 1: the older release (built-ins only) installs; the boot path creates generic.message ------
            var older = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                var keys = await KeysAsync(older);
                _output.WriteLine($"[{storeName}] phase 1 (boot path, hosted services NOT started): {string.Join(", ", keys)}; host errors: {string.Join(" || ", GateDrive.DrainCapturedErrors())}");
                Assert.Contains(NotificationTemplateCatalogue.GenericMessageKey, keys);
                Assert.DoesNotContain(SecondKey, keys);

                // The operator edits generic.message - what the upgrade must not revert.
                await GateDrive.InTenantAsync(older, Host, async (sp, alias) =>
                {
                    var templates = sp.GetRequiredService<INotificationTemplateRepository>();
                    var generic = await templates.GetByKeyAsync(alias.SiteId, NotificationTemplateCatalogue.GenericMessageKey);
                    generic.Update(
                        [ChannelKind.Email, ChannelKind.InApp],
                        [new LocalizedContent(new Locale("en"), new Content(OperatorSubject, "{{ body }}"))],
                        new PlaceholderSchema(["subject", "body"]));
                    await templates.UpdateAsync(generic);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(older);
            }

            // ---- Phase 2: same database, a newer release that ALSO declares a second built-in ---------------------
            var newer = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.AddSingleton<INotificationTemplateSource, SecondBuiltInSource>(), start: false);
            try
            {
                var templates = await TemplatesAsync(newer);
                _output.WriteLine($"[{storeName}] phase 2 (boot path): {string.Join(", ", templates.Select(t => t.Key))}; host errors: {string.Join(" || ", GateDrive.DrainCapturedErrors())}");
                var second = Assert.Single(templates, t => t.Key == SecondKey);
                Assert.Equal("Second {{ subject }}", second.Contents.Single().Content.Subject);

                var generic = Assert.Single(templates, t => t.Key == NotificationTemplateCatalogue.GenericMessageKey);
                Assert.Equal(OperatorSubject, generic.Contents.Single().Content.Subject);
                Assert.Equal([ChannelKind.Email, ChannelKind.InApp], generic.SupportedChannels);

                // Idempotent: a second run of the runner adds nothing and duplicates nothing.
                var report = await new SiteContentReconciliationRunner(
                    newer.Services.GetRequiredService<IServiceScopeFactory>(),
                    newer.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test")).RunAsync();
                Assert.Empty(report.Failures);
                Assert.Equal(1, report.SitesVisited);
                var again = await TemplatesAsync(newer);
                Assert.Equal(templates.Count, again.Count);
                Assert.Equal(templates.Select(t => t.Id).OrderBy(id => id), again.Select(t => t.Id).OrderBy(id => id));
            }
            finally
            {
                await GateDrive.ShutdownAsync(newer);
            }
        }

        private static async Task<IReadOnlyList<string>> KeysAsync(WebApplication app) =>
            (await TemplatesAsync(app)).Select(template => template.Key).OrderBy(key => key).ToList();

        private static Task<IReadOnlyList<NotificationTemplateAggregate>> TemplatesAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<INotificationTemplateRepository>().ListBySiteAsync(alias.SiteId));

        /// <summary>The "newer release": one more built-in.</summary>
        private sealed class SecondBuiltInSource : INotificationTemplateSource
        {
            public string Name => "upgrade test";

            public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            [
                NotificationTemplateAggregate.Create(Guid.NewGuid(), tenantId, siteId, SecondKey,
                    supportedChannels: [ChannelKind.InApp],
                    contents: [new LocalizedContent(new Locale("en"), new Content("Second {{ subject }}", "{{ body }}"))],
                    placeholders: new PlaceholderSchema(["subject", "body"])),
            ];
        }
    }
}
