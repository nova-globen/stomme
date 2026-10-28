using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-164's harness change, held to the bar of any other change: <see cref="GateInstallTemplates"/> must install
    /// ONCE, hand out copies that do NOT see each other's writes, and go RED - not slow - when the copy is empty.
    /// </summary>
    /// <remarks>
    /// Every case uses an alias of its own, and the alias is part of a template's key: each case therefore owns its
    /// template and can count the installs it caused. FAIL-NOT-SKIP on PostgreSQL and RavenDB (D46).
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class GateInstallTemplateTests
    {
        private readonly ITestOutputHelper _output;

        public GateInstallTemplateTests(ITestOutputHelper output) => _output = output;

        private static IGateStore StoreFor(string storeName) => storeName switch
        {
            SqliteGateStore.StoreName => new SqliteGateStore(),
            PostgresGateStore.StoreName => new PostgresGateStore(),
            RavenGateStore.StoreName => new RavenGateStore(),
            _ => throw new ArgumentOutOfRangeException(nameof(storeName), storeName, "unknown store"),
        };

        [Theory]
        [InlineData(SqliteGateStore.StoreName)]
        [InlineData(PostgresGateStore.StoreName)]
        [InlineData(RavenGateStore.StoreName)]
        public async Task Two_clones_cost_one_install_serve_a_signed_in_host_and_do_not_see_each_others_writes(string storeName)
        {
            if (GateInstallTemplates.FreshInstallsForced)
            {
                _output.WriteLine("STOMME_TEST_FRESH_INSTALLS=1: there are no templates in this run, so there is nothing here to hold to account.");
                return;
            }

            var first = StoreFor(storeName);
            Assert.True(first.IsReachable(), first.UnreachableMessage);

            var host = $"template-{storeName.ToLowerInvariant()}.gate.test";
            const string roleName = "WrittenInTheFirstCloneOnly";
            var installsBefore = GateInstallTemplates.TemplateInstalls;

            using (var scratch = await GateInstallTemplates.CloneAsync(first, host, _output))
            {
                var app = await GateDrive.InstallAsync(scratch, _output);
                try
                {
                    await GateDrive.EnsureRoleAsync(app, host, roleName);
                    Assert.Contains(await RoleNamesAsync(app, host), name => name == roleName);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }

            using (var scratch = await GateInstallTemplates.CloneAsync(StoreFor(storeName), host, _output))
            {
                var app = await GateDrive.InstallAsync(scratch, _output);
                try
                {
                    // Had the first clone written through to the TEMPLATE, this copy of it would carry the role.
                    Assert.DoesNotContain(await RoleNamesAsync(app, host), name => name == roleName);

                    // And it is a working site, not merely a readable store: the installer's host account signs in
                    // and the home page renders.
                    using var client = GateDrive.NewClient(app, host);
                    var cookies = await GateDrive.LoginAsync(app, client, host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                    var (status, body, _) = await GateDrive.GetAsync(client, cookies, host, "/", _output, storeName);
                    Assert.Equal(HttpStatusCode.OK, status);
                    Assert.Contains("</html>", body, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }

            Assert.Equal(1, GateInstallTemplates.TemplateInstalls - installsBefore);
        }

        [Fact]
        public async Task A_clone_whose_store_was_not_copied_FAILS_before_the_installer_can_quietly_install_it()
        {
            if (GateInstallTemplates.FreshInstallsForced)
            {
                _output.WriteLine("STOMME_TEST_FRESH_INSTALLS=1: there are no templates in this run, so there is nothing here to hold to account.");
                return;
            }

            const string host = "template-empty.gate.test";
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), host, _output);

            // What a copy step that silently did nothing leaves behind: settings that say "installed", and no store.
            ScratchSqlite.ReleaseInstall(scratch.ContentRoot);
            foreach (var file in Directory.EnumerateFiles(scratch.ContentRoot, "stomme-gate.db*"))
            {
                File.Delete(file);
            }

            var failure = await Assert.ThrowsAnyAsync<XunitException>(() => GateDrive.InstallAsync(scratch, _output));
            Assert.Contains("CLONE", failure.Message, StringComparison.Ordinal);
        }

        private static Task<string[]> RoleNamesAsync(Microsoft.AspNetCore.Builder.WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
                (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId)).Select(role => role.Name).ToArray());
    }
}
