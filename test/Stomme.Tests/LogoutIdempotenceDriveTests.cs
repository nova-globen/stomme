using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-190, driven on an installed SQLite host: signing out is idempotent. Found on 2026-10-01 by deleting an account from
    /// its profile in a browser - the account was deleted and its erasure queued, then the page posted to <c>/pages/logout/</c>
    /// as Logout does, with a session the deletion had already revoked; the page's <c>[Authorize]</c> challenged, the cookie
    /// scheme answers a challenge with a bare 403, and the person who had just deleted their account saw a blank page. The
    /// same met anyone whose session was revoked elsewhere and who then clicked Logout.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class LogoutIdempotenceDriveTests
    {
        private const string Host = "localhost";
        private const string LeaverUsername = "leaver";
        private const string LeaverPassword = "L3aver!Gone$2026";

        private readonly ITestOutputHelper _output;

        public LogoutIdempotenceDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_sign_out_after_the_session_is_gone_returns_home_and_never_leaves_the_site()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // 1. The browser flow that found it: the account deletes itself, then the page signs out with the old cookie.
                var leaver = await GateDrive.AddUserAsync(app, Host, LeaverUsername, LeaverPassword, "leaver@gate.test");
                using var leaverClient = GateDrive.NewClient(app, Host);
                var leaverCookies = await GateDrive.LoginAsync(app, leaverClient, Host, LeaverUsername, LeaverPassword, _output);

                var deleted = await GateDrive.SendJsonAsync(app, leaverClient, leaverCookies, Host, leaver.UserId, HttpMethod.Delete, "/api/User/me", null, _output, "self-delete");
                Assert.Equal(HttpStatusCode.OK, deleted.Status);

                var afterDelete = await PostLogoutAsync(leaverClient, leaverCookies, "/", "after the account deleted itself");
                Assert.Equal(HttpStatusCode.Redirect, afterDelete.Status);
                Assert.Equal("/", afterDelete.Location);

                // 2. With no cookie at all - a second click, another tab - the same answer.
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var anonymous = await PostLogoutAsync(anonymousClient, null, "/", "anonymous");
                Assert.Equal(HttpStatusCode.Redirect, anonymous.Status);
                Assert.Equal("/", anonymous.Location);

                // 3. The return address stays on this site, signed in or not.
                var anonymousOffSite = await PostLogoutAsync(anonymousClient, null, "//evil.example/x", "anonymous, off-site return");
                Assert.Equal(HttpStatusCode.Redirect, anonymousOffSite.Status);
                Assert.Equal("/", anonymousOffSite.Location);

                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var signedInOffSite = await PostLogoutAsync(hostClient, hostCookies, "//evil.example/x", "signed in, off-site return");
                Assert.Equal(HttpStatusCode.Redirect, signedInOffSite.Status);
                Assert.False(IsOffSite(signedInOffSite.Location), $"A sign-out sent the browser off the site: '{signedInOffSite.Location}'.");

                // 4. And a signed-in sign-out still signs out: the host's cookie no longer reaches a Host-only API.
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var afterSignOut = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Get, "/api/Erasure/receipts", null, _output, "a Host-only read after signing out");
                Assert.NotEqual(HttpStatusCode.OK, afterSignOut.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static bool IsOffSite(string location) =>
            string.IsNullOrEmpty(location) is false &&
            (location.StartsWith("//", StringComparison.Ordinal) || location.StartsWith("/\\", StringComparison.Ordinal) || Uri.TryCreate(location, UriKind.Absolute, out var absolute) && absolute.Host != Host);

        private async Task<(HttpStatusCode Status, string Location)> PostLogoutAsync(HttpClient client, System.Net.CookieContainer cookies, string returnUrl, string label)
        {
            var uri = new Uri("http://" + Host + "/pages/logout/");
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnurl"] = returnUrl, ["everywhere"] = "false" })
            };
            var header = cookies?.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await client.SendAsync(request);
            if (cookies is not null)
            {
                GateDrive.CaptureCookies(cookies, uri, response);
            }

            var location = response.Headers.Location?.OriginalString ?? "";
            _output.WriteLine($"[{label}] POST /pages/logout/ returnurl={returnUrl} -> {(int)response.StatusCode} Location: {location}");
            return (response.StatusCode, location);
        }
    }
}
