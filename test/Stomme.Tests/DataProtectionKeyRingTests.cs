using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The Data Protection key ring is kept where a redeploy does not lose it. The framework set no location, so a
    /// deployment that mounted only the installation's data lost the ring on every redeploy - every visitor signed out and
    /// every stored secret Unreadable for good. The choice is unit-tested; the drive stores a site secret, restarts the host
    /// and reads it back from a ring kept where the deployment named.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DataProtectionKeyRingTests(ITestOutputHelper output)
    {
        private static IConfiguration Settings(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value)).Build();

        [Fact]
        public void A_named_folder_wins_then_an_existing_default_ring_then_the_installation()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-keyring-" + Guid.NewGuid().ToString("N")[..8]);
            var withKeys = Path.Combine(root, "default-with-keys");
            var empty = Path.Combine(root, "default-empty");
            Directory.CreateDirectory(withKeys);
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(withKeys, "key-00000000-0000-0000-0000-000000000001.xml"), "<key />");
            try
            {
                Assert.Equal("/mnt/keys", DataProtectionKeyRing.Choose(Settings((DataProtectionKeyRing.KeysPathKey, "/mnt/keys")), withKeys, "/app/Data"));
                Assert.Null(DataProtectionKeyRing.Choose(Settings(), withKeys, "/app/Data")); // an existing ring stays where it is
                Assert.Equal(Path.Combine("/app/Data", "DataProtection-Keys"), DataProtectionKeyRing.Choose(Settings(), empty, "/app/Data"));
                Assert.Equal(Path.Combine("/app/Data", "DataProtection-Keys"), DataProtectionKeyRing.Choose(Settings(), Path.Combine(root, "missing"), "/app/Data"));
                Assert.Null(DataProtectionKeyRing.Choose(Settings(), empty, string.Empty));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task A_secret_stored_before_a_restart_reads_after_it_from_the_ring_the_deployment_named()
        {
            var keys = Path.Combine(Path.GetTempPath(), "stomme-keyring-drive-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", output, colocateModuleAggregates: false,
                    extraSettings: new JsonObject { ["DataProtection"] = new JsonObject { ["KeysPath"] = keys } });
                var app = await GateDrive.InstallAsync(scratch, output);
                try
                {
                    await GateDrive.InTenantAsync(app, "localhost", async (sp, alias) =>
                    {
                        await sp.GetRequiredService<ISiteSecretStore>().SetAsync(alias.SiteId, "bl248.secret", "kaffe-och-bulle");
                        return 0;
                    });
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }

                Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml")); // the ring went where the deployment named

                app = await GateDrive.InstallAsync(scratch, output);
                try
                {
                    var read = await GateDrive.InTenantAsync(app, "localhost", (sp, alias) => sp.GetRequiredService<ISiteSecretStore>().GetAsync(alias.SiteId, "bl248.secret"));
                    Assert.Equal((SiteSecretStatus.Found, "kaffe-och-bulle"), (read.Status, read.Secret));
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }
            finally
            {
                if (Directory.Exists(keys))
                {
                    Directory.Delete(keys, true);
                }
            }
        }
    }
}
