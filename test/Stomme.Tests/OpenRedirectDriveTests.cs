using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A <c>returnurl</c> never sends anyone off the site. Found by driving the validation: every place that takes
    /// one checked only that it started with '/', and a browser reads <c>//attacker.example</c> and <c>/\attacker.example</c>
    /// as another host. After an external sign-in <c>/pages/external</c> redirected there with the session cookie; the
    /// router handed it to the login page, which sent a signed-in visitor there at once; the pages that end in
    /// <c>LocalRedirect</c> threw on it (500). One rule now decides, <see cref="Utilities.LocalReturnUrl"/>.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OpenRedirectDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Theory]
        [InlineData("/notes?id=1", "/notes?id=1")]
        [InlineData("/", "/")]
        [InlineData("//attacker.example/x", "/")]
        [InlineData("/\\attacker.example/x", "/")]
        [InlineData("https://attacker.example/x", "/")]
        [InlineData("attacker.example", "/")]
        [InlineData("/x\r\nLocation: //attacker.example", "/")]
        [InlineData("", "/")]
        [InlineData(null, "/")]
        public void A_return_address_is_a_path_on_this_site_or_the_fallback(string? given, string expected) =>
            Assert.Equal(expected, Utilities.LocalReturnUrl(given!));

        [Fact]
        public void The_fallback_is_the_callers()
        {
            Assert.Equal(string.Empty, Utilities.LocalReturnUrl("//attacker.example", string.Empty));
            Assert.Equal("/home", Utilities.LocalReturnUrl(null!, "/home"));
        }

        [Fact]
        public async Task The_sign_in_pages_send_an_off_site_return_address_home_not_off_the_site_and_not_to_an_error()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                foreach (var offSite in new[] { "//attacker.example/phish", "/\\attacker.example/phish" })
                {
                    // Signing in with a password (it ended in LocalRedirect, which threw: 500).
                    using var client = GateDrive.NewClient(app, Host);
                    var signedIn = await PostAsync(app, client, "/pages/login", new() { ["username"] = GateDrive.HostUsername, ["password"] = GateDrive.HostPassword, ["remember"] = "false", ["returnurl"] = offSite }, "sign in");
                    Assert.Equal((HttpStatusCode.Redirect, "/"), signedIn);

                    // The external sign-in's return leg (the same).
                    var external = await PostAsync(app, client, "/pages/external", new() { ["returnurl"] = offSite }, "external return");
                    Assert.Equal((HttpStatusCode.Redirect, "/"), external);
                }

                // A path on the site still goes where it says.
                using var local = GateDrive.NewClient(app, Host);
                Assert.Equal((HttpStatusCode.Redirect, "/admin"), await PostAsync(app, local, "/pages/login", new() { ["username"] = GateDrive.HostUsername, ["password"] = GateDrive.HostPassword, ["remember"] = "false", ["returnurl"] = "/admin" }, "sign in, local return"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<(HttpStatusCode Status, string Location)> PostAsync(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, string path, Dictionary<string, string> fields, string label)
        {
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app);
            fields["__RequestVerificationToken"] = token;
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://" + Host + path)) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            var response = await client.SendAsync(request);
            var location = response.Headers.Location?.OriginalString ?? string.Empty;
            output.WriteLine($"[{label}] POST {path} returnurl={fields["returnurl"]} -> {(int)response.StatusCode} Location: {location}");
            return (response.StatusCode, location);
        }
    }
}
