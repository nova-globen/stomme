#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// The framework-upgrade endpoint, over real HTTP on an installed host: a POST (antiforgery-checked; it was a GET
    /// a cross-site link could trigger), Host-only, that AWAITS the hand-off and answers with why an upgrade did not start -
    /// the pre-upgrade refusal included - instead of leaving it to the log.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class UpgradeEndpointTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_refused_pre_upgrade_snapshot_reaches_the_caller_and_the_package_stays()
        {
            // Backup on, no scheduled passphrase: every upgrade is refused.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var none = await UpgradeAsync(app, client, cookies, hostId, "no package yet");
                Assert.False(none.Success);
                Assert.Contains("No framework package", none.Message, StringComparison.Ordinal);

                // Planted after the boot: a boot installs whatever sits in Packages/.
                var package = BackupPreUpgradeSnapshotTests.WriteFrameworkPackage(scratch.ContentRoot);
                var refused = await UpgradeAsync(app, client, cookies, hostId, "refused by the pre-upgrade snapshot");
                Assert.False(refused.Success);
                Assert.Contains("Schedule:Passphrase", refused.Message, StringComparison.Ordinal);
                Assert.True(System.IO.File.Exists(package), "a refused upgrade consumed the framework package");

                // The old verb no longer reaches it, and nobody but the Host does.
                var get = await GateDrive.GetAsync(client, cookies, Host, "/api/Installation/upgrade?backup=True", output, "the old GET");
                Assert.NotEqual(HttpStatusCode.OK, get.Status);
                using var anonymous = GateDrive.NewClient(app, Host);
                var anonymousPost = await GateDrive.SendJsonAsync(app, anonymous, null, Host, null, HttpMethod.Post, "/api/Installation/upgrade", true, output, "anonymous");
                Assert.True(anonymousPost.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"anonymous upgrade answered {(int)anonymousPost.Status}");
                Assert.True(System.IO.File.Exists(package));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<Installation> UpgradeAsync(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int hostId, string label)
        {
            var response = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Installation/upgrade", true, output, label);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            return JsonSerializer.Deserialize<Installation>(response.Body, Json)!;
        }
    }
}
