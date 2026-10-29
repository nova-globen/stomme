using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Repository.Identity;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// LOCKOUT DRIVEN OVER HTTP on a REAL install: the proof that the lockout <c>IdentityOptions</c>
    /// actually BITE through the login page, not merely that store methods round-trip.
    /// <para>
    /// Stands up the real host (<see cref="Program.BuildAppAsync"/>, transport swapped for TestServer), runs the
    /// real installer, then drives <c>POST /pages/login</c> — whose handler calls
    /// <c>CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)</c> — with the WRONG password
    /// MaxFailedAccessAttempts times, and asserts the CORRECT password is then REJECTED while the account is
    /// locked out. The control (the same correct-password POST succeeding BEFORE the failures) proves the
    /// rejection is the lockout, not a broken credential.
    /// </para>
    /// <para>
    /// PROVIDER DIMENSION (the D46/D62 discipline): SQLite is the EF control — the behaviour a relational
    /// install has always had — and RavenDB runs UNCONDITIONALLY (fail-not-skip), because until BL-6 the Raven
    /// <c>IUserStore</c> omitted <c>IUserLockoutStore</c>/<c>IUserTwoFactorStore</c> and this exact drive let
    /// the sixth, correct-password login STRAIGHT IN after five failures. Identical steps on both providers,
    /// identical assertions: a Raven install must behave like the EF install.
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class IdentityLockoutHttpDriveTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";
        private const string HostUsername = "hostlockout";
        private const string HostPassword = "L0ck0ut!Drive$2026";
        private const string WrongPassword = "Wr0ng!Password$2026";
        private const string HostEmail = "hostlockout@example.com";

        private readonly ITestOutputHelper _output;

        public IdentityLockoutHttpDriveTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { "SQLite" };
            yield return new object[] { "RavenDB" }; // unconditional: unreachable Raven FAILS, never skips
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Lockout_bites_over_http_after_max_failed_logins(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new ScratchInstall(provider);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    // TestServer has no real connection; stamp loopback so anything reading RemoteIpAddress
                    // (logging, ValidateUser) sees what a real Kestrel host reports.
                    builder.Services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                });

            try
            {
                await InstallAsync(app.Services.GetRequiredService<IDatabaseManager>(), new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantConnectionString,
                    Aliases = Host,
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = HostUsername,
                    HostPassword = HostPassword,
                    HostEmail = HostEmail,
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }, providerName);

                await app.StartAsync();

                // ---- Arrange: the store must SUPPORT lockout, and the host account must have it enabled. ----
                // (On Raven before BL-6 this assert is the first RED: SupportsUserLockout was false, so every
                // lockout knob was silently inert.) LockoutEnabled is forced true identically on both providers
                // so the drive is deterministic regardless of what AllowedForNewUsers was at install time.
                int maxFailed;
                using (var scope = await CreateAliasScopeAsync(app))
                {
                    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    Assert.True(userManager.SupportsUserLockout,
                        $"[{providerName}] SupportsUserLockout is false — the identity store does not implement " +
                        "IUserLockoutStore, so MaxFailedAccessAttempts/DefaultLockoutTimeSpan are silently inert.");
                    Assert.True(userManager.SupportsUserTwoFactor,
                        $"[{providerName}] SupportsUserTwoFactor is false — the identity store does not implement " +
                        "IUserTwoFactorStore.");

                    var host = await userManager.FindByNameAsync(HostUsername);
                    Assert.NotNull(host);
                    _output.WriteLine($"[{providerName}] host LockoutEnabled at install: {await userManager.GetLockoutEnabledAsync(host!)}");
                    Assert.True((await userManager.SetLockoutEnabledAsync(host!, true)).Succeeded);

                    maxFailed = userManager.Options.Lockout.MaxFailedAccessAttempts;
                    _output.WriteLine($"[{providerName}] MaxFailedAccessAttempts = {maxFailed}, DefaultLockoutTimeSpan = {userManager.Options.Lockout.DefaultLockoutTimeSpan}");
                    Assert.True(maxFailed > 0);
                }

                var server = app.GetTestServer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };

                // ---- Control: the correct password LOGS IN before any failures. ----------------------------
                var control = await PostLoginAsync(app, client, HostPassword);
                _output.WriteLine($"[{providerName}][control] correct password -> {(int)control.StatusCode}; Identity cookie: {HasIdentityCookie(control)}");
                Assert.Equal(HttpStatusCode.Redirect, control.StatusCode);
                Assert.True(HasIdentityCookie(control),
                    $"[{providerName}] the control login with the CORRECT password did not issue the Identity " +
                    "cookie — the drive's credential or antiforgery plumbing is broken, so nothing below would prove lockout.");

                // ---- Fail the login MaxFailedAccessAttempts times over HTTP. --------------------------------
                for (var attempt = 1; attempt <= maxFailed; attempt++)
                {
                    var failed = await PostLoginAsync(app, client, WrongPassword);
                    _output.WriteLine($"[{providerName}][fail {attempt}/{maxFailed}] wrong password -> {(int)failed.StatusCode}; Identity cookie: {HasIdentityCookie(failed)}");
                    Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
                    Assert.False(HasIdentityCookie(failed), $"[{providerName}] a WRONG password issued the Identity cookie.");
                }

                // ---- The deliverable: the CORRECT password is now REJECTED — the account is locked out. -----
                var lockedOut = await PostLoginAsync(app, client, HostPassword);
                _output.WriteLine($"[{providerName}][locked] correct password -> {(int)lockedOut.StatusCode}; Identity cookie: {HasIdentityCookie(lockedOut)}");
                Assert.Equal(HttpStatusCode.Redirect, lockedOut.StatusCode);
                Assert.False(HasIdentityCookie(lockedOut),
                    $"[{providerName}] the correct password STILL logged in after {maxFailed} failed attempts — " +
                    "lockout did not bite (the exact BL-6 defect).");

                // ---- Corroborate through the manager: locked out, lockout end in the future. ----------------
                using (var scope = await CreateAliasScopeAsync(app))
                {
                    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    var host = await userManager.FindByNameAsync(HostUsername);
                    Assert.NotNull(host);
                    Assert.True(await userManager.IsLockedOutAsync(host!),
                        $"[{providerName}] HTTP rejected the login but IsLockedOutAsync is false — the rejection was not lockout.");
                    var lockoutEnd = await userManager.GetLockoutEndDateAsync(host);
                    Assert.NotNull(lockoutEnd);
                    Assert.True(lockoutEnd > DateTimeOffset.UtcNow);
                    _output.WriteLine($"[{providerName}] IsLockedOut=true, LockoutEnd={lockoutEnd:o}");
                }

                // ---- Raven only: the lockout scalars PHYSICALLY round-tripped to the document. --------------
                await provider.AssertPersistedLockoutAsync(app, _output);

                _output.WriteLine($"[{providerName}] LOCKOUT DRIVEN OVER HTTP: control login succeeded, {maxFailed} failures locked the account, correct password rejected.");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        // ---- HTTP plumbing --------------------------------------------------------------------------------

        /// <summary>
        /// POSTs <c>/pages/login</c> exactly as the login form does: a fresh antiforgery cookie/request-token
        /// pair generated through the app's own DataProtection ring, form-encoded credentials, no cookie reuse
        /// between attempts (each attempt is an independent anonymous request, like a fresh browser).
        /// </summary>
        private static async Task<HttpResponseMessage> PostLoginAsync(WebApplication app, HttpClient client, string password)
        {
            string requestToken;
            string antiforgeryCookie;
            using (var scope = app.Services.CreateScope())
            {
                var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
                var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                var tokens = antiforgery.GetAndStoreTokens(httpContext);
                requestToken = tokens.RequestToken!;
                antiforgeryCookie = httpContext.Response.Headers.SetCookie.ToString().Split(';')[0];
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = HostUsername,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            return await client.SendAsync(request);
        }

        private static bool HasIdentityCookie(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var setCookies)
                && setCookies.Any(cookie => cookie.Contains("Identity.Application="));

        // ---- host plumbing --------------------------------------------------------------------------------

        /// <summary>A scope with the installed alias set, so tenant-bound services (the EF user store's
        /// TenantDBContext) resolve outside an HTTP request — the OpenIddict suites' pattern.</summary>
        private static async Task<IServiceScope> CreateAliasScopeAsync(WebApplication app)
        {
            var scope = app.Services.CreateScope();
            var aliasRepository = scope.ServiceProvider.GetRequiredService<IAliasRepository>();
            var alias = await aliasRepository.GetAliasAsync(Host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{Host}'.");
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return scope;
        }

        private async Task InstallAsync(IDatabaseManager databaseManager, InstallConfig config, string label)
        {
            var installTask = Task.Run(() => databaseManager.InstallAsync(config));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"Install of {label} did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
            }
            var install = await installTask;
            _output.WriteLine($"[install {label}] Success={install.Success} Message={install.Message}");
            Assert.True(install.Success, $"Install of {label} failed: {install.Message}");
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        private static IProvider ResolveProvider(string name) => name switch
        {
            "SQLite" => new SqliteProvider(),
            "RavenDB" => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        private interface IProvider : IDisposable
        {
            string DatabaseType { get; }
            string TenantConnectionString { get; }
            string? DatabaseTypePreseed { get; }
            void BindToScratch(string scratchDirectory);

            /// <summary>Provider-specific physical-persistence assertion after the lockout drive (no-op on EF —
            /// the EF columns are stock Identity's own and the manager corroboration above covers them).</summary>
            Task AssertPersistedLockoutAsync(WebApplication app, ITestOutputHelper output);
        }

        private sealed class SqliteProvider : IProvider
        {
            private string _tenant = null!;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantConnectionString => _tenant;
            public string? DatabaseTypePreseed => null;

            public void BindToScratch(string scratch) => _tenant = $"Data Source={Path.Combine(scratch, "tenant.db")};";

            public Task AssertPersistedLockoutAsync(WebApplication app, ITestOutputHelper output) => Task.CompletedTask;

            public void Dispose()
            {
                // This provider's own database, never the process-wide ClearAllPools.
                ScratchSqlite.Release(_tenant);
            }
        }

        private sealed class RavenProvider : IProvider
        {
            private static string BaseUrl =>
                Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

            private readonly string _database = "stomme_lockoutdrive_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantConnectionString => $"Url={BaseUrl};Database={_database}";
            public string? DatabaseTypePreseed => DatabaseType;

            public void BindToScratch(string scratch) { }

            /// <summary>The raw document proof: LockoutEnd/LockoutEnabled physically live on the Raven user
            /// document after the HTTP drive — persistence, not an in-memory echo.</summary>
            public async Task AssertPersistedLockoutAsync(WebApplication app, ITestOutputHelper output)
            {
                string hostId;
                using (var scope = await CreateAliasScopeAsync(app))
                {
                    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    var host = await userManager.FindByNameAsync(HostUsername);
                    Assert.NotNull(host);
                    hostId = host!.Id;
                }

                using var verifier = (DocumentStore)new DocumentStore { Urls = new[] { BaseUrl }, Database = _database }.Initialize();
                using var session = verifier.OpenAsyncSession();
                var document = await session.LoadAsync<RavenIdentityUser>("RavenIdentityUsers/" + hostId);
                Assert.NotNull(document);
                output.WriteLine($"[RavenDB][raw doc] LockoutEnabled={document.LockoutEnabled} LockoutEnd={document.LockoutEnd:o} AccessFailedCount={document.AccessFailedCount}");
                Assert.True(document.LockoutEnabled, "LockoutEnabled did not persist to the Raven document.");
                Assert.NotNull(document.LockoutEnd);
                Assert.True(document.LockoutEnd > DateTimeOffset.UtcNow, "The persisted LockoutEnd is not in the future.");
            }

            public void Dispose()
            {
                try
                {
                    using var store = new DocumentStore { Urls = new[] { BaseUrl } };
                    store.Initialize();
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(_database, hardDelete: true));
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, continuation) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                    return continuation();
                });
                next(app);
            };
        }

        /// <summary>A throwaway content root; the Raven provider preseeds Database:DefaultDBType exactly as the
        /// other Raven install suites do (the wizard writes it mid-install; these drives skip the wizard).</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall(IProvider provider)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-lockoutdrive-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject();
                if (provider.DatabaseTypePreseed != null)
                {
                    overrides["Database"] = new JsonObject { ["DefaultDBType"] = provider.DatabaseTypePreseed };
                }

                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
