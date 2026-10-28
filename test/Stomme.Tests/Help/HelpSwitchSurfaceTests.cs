#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// BL-212 - the site the client receives says whether Help, Portability and Collaboration are on, so the help launcher, a
    /// person's data on their profile and the collaboration panel ask only when they are.
    /// </summary>
    /// <remarks>
    /// The launcher asked <c>api/Help/topic</c> on every page; with <c>Help:Enabled</c> off - the default - the router
    /// answers 404 (BL-210), and a WebAssembly site's browser logged it on every page (found driving BL-201, 2026-10-03). The
    /// switch travels in the site's settings, the channel D70 made for SAML's and Windows' switches; this reads it as a client
    /// does, over <c>GET api/Site/{id}</c>, from an installation with Help off and one with it on.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HelpSwitchSurfaceTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Theory]
        [InlineData(false, "false")]
        [InlineData(true, "true")]
        public async Task The_site_says_whether_each_client_switch_is_on(bool enabled, string expected)
        {
            var settings = enabled
                ? new JsonObject { ["Help"] = new JsonObject { ["Enabled"] = true }, ["Portability"] = new JsonObject { ["Enabled"] = true }, ["Collaboration"] = new JsonObject { ["Enabled"] = true } }
                : null;
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var site = await GateDrive.GetAsync(client, null, Host, "/api/Site/1", output, $"the site, Help {(enabled ? "on" : "off")}");
                Assert.Equal(HttpStatusCode.OK, site.Status);

                var values = JsonSerializer.Deserialize<JsonObject>(site.Body)!["settings"]!.Deserialize<Dictionary<string, string>>()!;
                Assert.All(HostSwitches.Published, section => Assert.Equal(expected, values.GetValueOrDefault(HostSwitches.SettingName(section))));
                Assert.Equal(new[] { "Help", "Portability", "Collaboration" }, HostSwitches.Published); // the three components that probed
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
