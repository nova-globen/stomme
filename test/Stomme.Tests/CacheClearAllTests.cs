using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The SystemInfo Caching section's "Clear All" -
    /// <c>POST /api/System/cache/clear</c>, Host only - empties the framework cache for real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DRIVEN through the composed host over HTTP (the <see cref="GateDrive"/> recipe, SQLite). The cache is primed
    /// and read back through the host's own singleton <see cref="ICacheManager"/>, so the test observes the very
    /// FusionCache instance the endpoint cleared.
    /// </para>
    /// <para>
    /// <b>Fail-safe is the trap.</b> A stock install has no <c>Caching</c> section, so FusionCache runs with
    /// fail-safe ON (30 minutes). FusionCache's default <c>ClearAsync()</c> only EXPIRES entries logically, and a
    /// failing factory then gets the pre-clear value back - a "clear" that goes on serving what it cleared exactly
    /// when the store behind it is broken. <see cref="After_clear_all_a_throwing_factory_does_not_serve_the_old_value"/>
    /// pins <c>ClearAsync(allowFailSafe: false)</c>, and was observed RED against the default overload.
    /// </para>
    /// <para>
    /// <b>Not covered:</b> the L2 distributed cache and the backplane. No Redis server is available to the suite,
    /// so a scale-out clear (every node, and the distributed entries) is unverified here.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class CacheClearAllTests
    {
        private const string Host = "localhost";
        private const string ProbeKey = "bl100:probe";

        private const string AdminUsername = "cacheadmin";
        private const string AdminPassword = "C4che!Admin$2026";
        private const string AdminEmail = "cacheadmin@gate.test";

        private readonly ITestOutputHelper _output;

        public CacheClearAllTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Host_clear_all_reinvokes_a_primed_entrys_factory()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var cache = app.Services.GetRequiredService<ICacheManager>();
                Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("old")));
                Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("never")));

                var status = await ClearAsHostAsync(app);
                Assert.True(status == HttpStatusCode.OK || status == HttpStatusCode.NoContent, $"POST /api/System/cache/clear answered {(int)status} {status}.");

                var calls = 0;
                var value = await cache.GetCacheAsync(ProbeKey, _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult("new");
                });
                _output.WriteLine($"[after clear] value={value} factory calls={calls}");
                Assert.Equal(1, calls);
                Assert.Equal("new", value);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task After_clear_all_a_throwing_factory_does_not_serve_the_old_value()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var configuration = app.Services.GetRequiredService<IConfiguration>();
                _output.WriteLine($"[config] Caching section present: {configuration.GetSection("Caching").Exists()} (absent = fail-safe ON, the stock default)");

                var cache = app.Services.GetRequiredService<ICacheManager>();
                Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("old")));

                var status = await ClearAsHostAsync(app);
                Assert.True(status == HttpStatusCode.OK || status == HttpStatusCode.NoContent, $"POST /api/System/cache/clear answered {(int)status} {status}.");

                string? served = null;
                Exception? thrown = null;
                try
                {
                    served = await cache.GetCacheAsync<string>(ProbeKey, _ => throw new InvalidOperationException("the store is down"));
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
                _output.WriteLine($"[after clear, throwing factory] served={served ?? "(nothing)"} thrown={thrown?.GetType().Name ?? "(none)"}");

                Assert.NotEqual("old", served);
                Assert.NotNull(thrown);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Admin_clear_all_is_403()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var admin = await GateDrive.AddUserAsync(app, Host, AdminUsername, AdminPassword, AdminEmail, RoleNames.Admin);
                var roles = await GateDrive.RolesAsync(app, Host, admin.UserId);
                Assert.Contains(RoleNames.Admin, roles);
                Assert.DoesNotContain(RoleNames.Host, roles);

                var cache = app.Services.GetRequiredService<ICacheManager>();
                Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("old")));

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, AdminUsername, AdminPassword, _output);
                var status = await PostClearAsync(app, client, cookies, admin.UserId);
                _output.WriteLine($"[admin] POST /api/System/cache/clear -> {(int)status}");

                Assert.Equal(HttpStatusCode.Forbidden, status);
                Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("new")));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<HttpStatusCode> ClearAsHostAsync(WebApplication app)
        {
            using var client = GateDrive.NewClient(app, Host);
            var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
            var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
            var status = await PostClearAsync(app, client, cookies, hostUserId);
            _output.WriteLine($"[host] POST /api/System/cache/clear -> {(int)status}");
            return status;
        }

        private async Task<HttpStatusCode> PostClearAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/System/cache/clear");
            // No body, the way the client's ServiceBase.PostAsync(uri) sends it.
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var cookieHeader = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                cookieHeader += "; ";
            }
            cookieHeader += antiforgeryCookie;
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"[clear] body: {GateDrive.Truncate(await response.Content.ReadAsStringAsync())}");
            }
            return response.StatusCode;
        }
    }
}
