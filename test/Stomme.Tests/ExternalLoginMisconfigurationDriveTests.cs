using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-247 - one wrong External Login setting does not take the site down. Found by BL-239's validation, driven: saving
    /// an OpenID Connect provider whose authority is plain http made EVERY request of the site answer 500 with an empty body
    /// - the handler's options are built for each request and refuse such an address while https metadata is required -
    /// and a restart did not help; recovery took a database edit. The address is now not handed to the handler, so only
    /// signing in through that provider fails.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ExternalLoginMisconfigurationDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Theory]
        [InlineData("https://idp.example", false, "https://idp.example")]
        [InlineData("http://idp.example", true, "")]
        [InlineData("http://localhost:5100", false, "http://localhost:5100")]
        [InlineData("", true, "")]
        public void An_address_the_handler_would_refuse_is_not_handed_to_it(string address, bool requireHttps, string expected) =>
            Assert.Equal(expected, Stomme.Extensions.StommeSiteAuthenticationBuilderExtensions.UsableMetadataAddress(address, requireHttps));

        [Fact]
        public async Task A_plain_http_authority_saved_by_the_host_leaves_every_page_of_the_site_serving()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                async Task SaveAsync(string name, string value)
                {
                    var saved = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Setting/{EntityNames.Site}/1/{Uri.EscapeDataString(name)}", new Setting { SettingValue = value, IsPrivate = true }, output, "save " + name);
                    Assert.True(saved.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"{name}: {(int)saved.Status} {saved.Body}");
                }

                // What the External Login form saved when it took the site down: a provider at a plain-http authority, with
                // https metadata required (the default; the form has no field for it).
                await SaveAsync("ExternalLogin:ProviderType", AuthenticationProviderTypes.OpenIDConnect);
                await SaveAsync("ExternalLogin:Authority", "http://idp.example");
                await SaveAsync("ExternalLogin:ClientId", "kafe");

                // The site's authentication options are built once and kept; a restart builds them from what was saved -
                // which is where it stayed down.
                await GateDrive.ShutdownAsync(app);
                app = await GateDrive.InstallAsync(scratch, output);

                using var visitor = GateDrive.NewClient(app, Host);
                foreach (var path in new[] { "/", "/login", "/api/Setting?entityName=Site&entityId=1" })
                {
                    var response = await visitor.GetAsync(new Uri("http://" + Host + path));
                    output.WriteLine($"GET {path} -> {(int)response.StatusCode}");
                    Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
