using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository.Identity;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D54b — THE FIRST FULL RAVENDB INSTALL DRIVEN END-TO-END, INCLUDING A LOGIN.
    /// <para>
    /// Runs the real installer's ENTIRE <c>InstallAsync</c> against a live RavenDB (default 127.0.0.1:7000) — through the
    /// host-account step, where <c>CreateSiteAsync</c> creates the host <c>IdentityUser</c> via
    /// <c>UserManager&lt;IdentityUser&gt;</c> → the hand-written <see cref="RavenIdentityUserStore"/> — and then
    /// asserts, against the LIVE database and through the resolved <c>UserManager</c>, that:
    /// </para>
    /// <list type="number">
    /// <item>the host <c>IdentityUser</c> document physically exists in RavenDB;</item>
    /// <item><c>UserManager.FindByNameAsync</c> finds it by the NORMALIZED username (mixed-case input);</item>
    /// <item><c>CheckPasswordAsync</c> / <c>SignInManager.CheckPasswordSignInAsync</c> AUTHENTICATE it — a real
    /// credential check against the Raven-stored <c>PasswordHash</c>, not a boot check;</item>
    /// <item>the <c>PasswordHash</c> and <c>SecurityStamp</c> round-trip through Raven (token providers depend on
    /// the stamp).</item>
    /// </list>
    /// <para>
    /// HONEST LIMIT: this does NOT assert the home page renders. HtmlText is still pure EF, so a Raven
    /// install cannot render until then; D54b's bar is host account + framework data in Raven + login.
    /// </para>
    /// <para>
    /// GATED — it FAILS rather than skips when RavenDB is unreachable, the same doctrine the conformance suite
    /// enforces. Point it at a reachable server with <c>RAVENDB_TEST_URL</c> (default the local install at
    /// http://127.0.0.1:7000).
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenInstallDrivesLoginTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
        // RavenDB endpoint, shared with the conformance suite via RAVENDB_TEST_URL. Default is the repo's
        // local RavenDB install (http://127.0.0.1:7000); the Docker container is gone (both stores are now
        // local Windows services: RavenDB on 7000, PostgreSQL postgresql-x64-18 on 127.0.0.1:5432). Override
        // the env var for any other server.
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private static Uri Endpoint => new(Url);
        private static string Host => Endpoint.Host;
        private static int Port => Endpoint.Port;

        // Mixed-case on purpose: proves the normalized (upper-cased) lookup neutralizes the EF/Raven == case
        // split by DRIVING it, rather than assuming it.
        private const string HostUsername = "HostAdmin";
        private const string HostPassword = "Pass123!";
        private const string HostEmail = "hostadmin@example.com";

        private readonly ITestOutputHelper _output;

        public RavenInstallDrivesLoginTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Full_install_creates_host_account_in_raven_and_login_authenticates()
        {
            Assert.True(IsReachable(),
                $"RavenDB is not reachable at {Url}, so nothing here proves a Raven install + login works. " +
                "Start the local RavenDB instance (default http://127.0.0.1:7000) or point RAVENDB_TEST_URL at a reachable server.");

            var database = "stomme_d54b_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var connectionString = $"Url={Url};Database={database}";

            using var scratch = new ScratchInstallation(RavenType, connectionString);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" });

            try
            {
                var manager = app.Services.GetRequiredService<IDatabaseManager>();

                var install = new InstallConfig
                {
                    ConnectionString = connectionString,
                    TenantName = TenantNames.Master,
                    DatabaseType = RavenType,
                    IsNewTenant = true,
                    Aliases = "localhost",
                    SiteName = Constants.DefaultSite,
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    DefaultTheme = Constants.DefaultTheme,
                    DefaultContainer = Constants.DefaultContainer,
                    HostUsername = HostUsername,
                    HostPassword = HostPassword,
                    HostEmail = HostEmail,
                    HostName = "Host Administrator",
                };

                // Drive the ENTIRE installer. Its overall Success may be false because InitializeSites touches the
                // still-EF HtmlText module — that is expected and NOT the D54b bar. The host account is
                // created in CreateSiteAsync, and the Raven IUserStore persists it immediately, so it exists
                // regardless of a later render-path failure. Report the outcome either way.
                var result = await manager.InstallAsync(install);
                _output.WriteLine($"[install] Success={result.Success} Message={Truncate(result.Message)}");

                // ---- PROOF 1: the host IdentityUser document physically exists in live RavenDB. --------------
                using var verifier = (DocumentStore)new DocumentStore { Urls = [Url], Database = database }.Initialize();
                var collections = await verifier.Maintenance.SendAsync(new GetCollectionStatisticsOperation());
                _output.WriteLine($"[Raven {database}] collections: " +
                    string.Join(", ", collections.Collections.Select(kv => $"{kv.Key}={kv.Value}")));

                var identityDocs = collections.Collections
                    .Where(kv => kv.Key.Contains("RavenIdentityUser", StringComparison.OrdinalIgnoreCase))
                    .Sum(kv => kv.Value);
                Assert.True(identityDocs >= 1,
                    "The host IdentityUser document was not written to RavenDB — CreateSiteAsync's host-account " +
                    "step did not persist through the Raven IUserStore.");

                using (var identitySession = verifier.OpenAsyncSession())
                {
                    var stored = await identitySession.LoadAsync<RavenIdentityUser>("RavenIdentityUsers/" + await GetHostIdAsync(app));
                    Assert.NotNull(stored);
                    Assert.False(string.IsNullOrEmpty(stored.PasswordHash),
                        "The host account's PasswordHash did not round-trip through Raven.");
                    Assert.False(string.IsNullOrEmpty(stored.SecurityStamp),
                        "The host account's SecurityStamp did not round-trip through Raven (token providers need it).");
                    Assert.Equal(HostUsername.ToUpperInvariant(), stored.NormalizedUserName);
                }

                // ---- PROOF 2 + 3: a REAL login through the resolved UserManager against Raven. ---------------
                using var scope = app.Services.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

                // Confirm the swap actually selected the Raven store on this install.
                var store = scope.ServiceProvider.GetRequiredService<IUserStore<IdentityUser>>();
                Assert.IsType<RavenIdentityUserStore>(store);

                // FindByName normalizes the (mixed-case) input and matches on NormalizedUserName — the intended
                // case behaviour, DRIVEN. Look it up with DIFFERENT casing than it was created with.
                var found = await userManager.FindByNameAsync("hostadmin");
                Assert.NotNull(found);
                Assert.Equal(HostUsername, found!.UserName);
                Assert.False(string.IsNullOrEmpty(found.PasswordHash));
                Assert.False(string.IsNullOrEmpty(found.SecurityStamp));

                // The actual credential check against the Raven-stored hash.
                Assert.True(await userManager.CheckPasswordAsync(found, HostPassword),
                    "CheckPasswordAsync failed — the host password did not verify against the hash stored in Raven.");
                Assert.False(await userManager.CheckPasswordAsync(found, "WrongPass9!"),
                    "A wrong password verified — the password check is not actually running.");

                // And through the SignInManager, the path the login page uses.
                var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<IdentityUser>>();
                var signIn = await signInManager.CheckPasswordSignInAsync(found, HostPassword, lockoutOnFailure: false);
                Assert.True(signIn.Succeeded,
                    $"SignInManager.CheckPasswordSignInAsync did not succeed against Raven (result: {signIn}).");

                // Best-effort database cleanup.
                try
                {
                    await verifier.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
                }
                catch
                {
                    // disposable container; a leftover database is harmless
                }
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        // The host IdentityUser id (a string GUID) is not known ahead of time, so resolve it through the Raven
        // store the same way the app does — FindByName on the normalized username — to address its document.
        private static async Task<string> GetHostIdAsync(Microsoft.AspNetCore.Builder.WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await userManager.FindByNameAsync(HostUsername);
            Assert.NotNull(user);
            return user!.Id;
        }

        private static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= 400 ? value : value.Substring(0, 400) + " …");

        private static bool IsReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        // A throwaway content root plus the override the installer's self-writes land in — so the repository's
        // appsettings.json is never touched, exactly as the render guard and D54a's test isolate themselves.
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation(string databaseType, string connectionString)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-raven-d54b-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = databaseType },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                };

                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
