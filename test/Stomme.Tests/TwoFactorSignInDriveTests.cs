using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A site that requires a second factor gets one at the page that sets the cookie. Found by reading the login flow:
    /// the login UI checks the password and the mailed code over the API, then posts the username and password to
    /// <c>/pages/login</c> to set the cookie - and that page checked the password alone, so anyone holding a password signed
    /// in there directly, skipping the second factor (and the confirmed-address rule). Driven on an installed host.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TwoFactorSignInDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task With_a_second_factor_required_the_password_alone_sets_no_session_and_the_code_works_once()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var admin = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, admin, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var required = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, hostId, HttpMethod.Put, $"/api/Setting/{EntityNames.Site}/1/{Uri.EscapeDataString("LoginOptions:TwoFactor")}", new Setting { SettingValue = "required", IsPrivate = false }, output, "require a second factor");
                Assert.True(required.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"{(int)required.Status} {required.Body}");

                Assert.False(await SignsInAsync(app, code: null), "the password alone set a session on a site that requires a second factor");
                await StoreCodeAsync(app, "731954");
                Assert.False(await SignsInAsync(app, code: "000000"), "a wrong code set a session");
                Assert.True(await SignsInAsync(app, code: "731954"), "the mailed code did not sign in");
                Assert.False(await SignsInAsync(app, code: "731954"), "the same code signed in twice");

                await StoreCodeAsync(app, "482617", expired: true);
                Assert.False(await SignsInAsync(app, code: "482617"), "an expired code set a session");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // The code the sign-in would have mailed, as the user's row holds it.
        private static Task StoreCodeAsync(Microsoft.AspNetCore.Builder.WebApplication app, string code, bool expired = false) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                var users = sp.GetRequiredService<IUserRepository>();
                var user = await users.GetUserAsync(GateDrive.HostUsername);
                user.TwoFactorCode = code;
                user.TwoFactorExpiry = expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(5);
                await users.UpdateUserAsync(user);
                return 0;
            });

        private async Task<bool> SignsInAsync(Microsoft.AspNetCore.Builder.WebApplication app, string? code)
        {
            using var client = GateDrive.NewClient(app, Host);
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app);
            var fields = new Dictionary<string, string> { ["username"] = GateDrive.HostUsername, ["password"] = GateDrive.HostPassword, ["remember"] = "false", ["returnurl"] = "/", ["__RequestVerificationToken"] = token };
            if (code != null)
            {
                fields["code"] = code;
            }

            var uri = new Uri("http://" + Host + "/pages/login");
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            using var response = await client.SendAsync(request);
            var cookies = new CookieContainer();
            GateDrive.CaptureCookies(cookies, uri, response);
            var session = cookies.GetCookies(uri).Cast<Cookie>().Any(cookie => cookie.Name.StartsWith(".AspNetCore.Identity", StringComparison.Ordinal) && cookie.Value.Length > 0);
            output.WriteLine($"POST /pages/login code={code ?? "(none)"} -> {(int)response.StatusCode}, session cookie: {session}");
            return session;
        }
    }
}
