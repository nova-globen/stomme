using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D88 — the module-definition "Configure" tab SAVE (the master-DB-routed
    /// <see cref="EntityNames.ModuleDefinition"/> settings channel, D86) must succeed for the HOST over HTTP and
    /// STILL be denied for a non-host admin. Drives the REAL composed host (<see cref="Program.BuildAppAsync"/> +
    /// the production middleware pipeline, transport swapped for TestServer):
    /// <list type="number">
    ///   <item>the host logs in via <c>/pages/login</c> (Identity cookie), then PUTs a
    ///     <see cref="EntityNames.ModuleDefinition"/> setting to <c>/api/Setting/{entityName}/{entityId}</c> and
    ///     reads it back — asserting <b>200 + persisted</b>, proving the Web-API principal carries
    ///     <see cref="RoleNames.Host"/> (the D86 finding: this returned 403);</item>
    ///   <item>a REGISTERED (non-host) user logs in and PUTs the same setting — asserting <b>403</b>, proving the
    ///     gate stays Host-only (it was NOT relaxed to Admin).</item>
    /// </list>
    /// SQLite (always available); the write is driven over real HTTP with a matched antiforgery pair, the way the
    /// interactive circuit's cookie-authenticated call does.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModuleDefinitionSettingsAuthorizationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Config$2026";
        private const string HostEmail = "host@example.com";

        private const string AdminUsername = "plainadmin";
        private const string AdminPassword = "Adm1n!Plain$2026";
        private const string AdminEmail = "plainadmin@example.com";

        private readonly ITestOutputHelper _output;

        public ModuleDefinitionSettingsAuthorizationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Host_saves_a_ModuleDefinition_setting_over_http_but_a_non_host_is_denied()
        {
            using var scratch = new ScratchInstall();

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException("The installer did not finish in time.");
                }
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                var server = app.GetTestServer();

                // A real module-definition id to hang the installation-wide setting off of.
                int moduleDefinitionId = await FirstModuleDefinitionIdAsync(app);
                _output.WriteLine($"target ModuleDefinitionId = {moduleDefinitionId}");

                const string settingName = "D88:Probe";
                const string settingValue = "saved-by-host-over-http";

                // ---- 1. HOST: login -> PUT ModuleDefinition setting -> 200 + read-back --------------------
                int hostUserId = await UserIdAsync(app, HostUsername);
                var hostCookies = new CookieContainer();
                using (var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) })
                {
                    await LoginAsync(app, client, hostCookies, HostUsername, HostPassword);
                    AssertIdentityCookie(hostCookies, "host");

                    var putStatus = await PutSettingsAsync(app, client, hostCookies, hostUserId, moduleDefinitionId,
                        new List<Setting>
                        {
                            new Setting
                            {
                                SettingId = 0,
                                EntityName = EntityNames.ModuleDefinition,
                                EntityId = moduleDefinitionId,
                                SettingName = settingName,
                                SettingValue = settingValue
                            }
                        });
                    _output.WriteLine($"[host] PUT /api/Setting/{EntityNames.ModuleDefinition}/{moduleDefinitionId} -> {(int)putStatus}");
                    Assert.Equal(HttpStatusCode.OK, putStatus);

                    // read back over the same authenticated channel
                    var readBack = await GetSettingsAsync(client, hostCookies, moduleDefinitionId);
                    Assert.Contains(readBack, s => s.SettingName == settingName && s.SettingValue == settingValue);
                }

                // definitive proof: the value is in the master settings store
                using (var scope = app.Services.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingRepository>();
                    var stored = await settings.GetSettingAsync(EntityNames.ModuleDefinition, moduleDefinitionId, settingName);
                    Assert.NotNull(stored);
                    Assert.Equal(settingValue, stored!.SettingValue);
                }

                // ---- 2. NON-HOST admin: login -> PUT same setting -> 403 (gate stays Host-only) ----------
                await CreateRegisteredUserAsync(app, AdminUsername, AdminPassword, AdminEmail);
                int adminUserId = await UserIdAsync(app, AdminUsername);

                var adminCookies = new CookieContainer();
                using (var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) })
                {
                    await LoginAsync(app, client, adminCookies, AdminUsername, AdminPassword);
                    AssertIdentityCookie(adminCookies, "non-host");

                    var putStatus = await PutSettingsAsync(app, client, adminCookies, adminUserId, moduleDefinitionId,
                        new List<Setting>
                        {
                            new Setting
                            {
                                SettingId = 0,
                                EntityName = EntityNames.ModuleDefinition,
                                EntityId = moduleDefinitionId,
                                SettingName = settingName,
                                SettingValue = "should-not-be-written"
                            }
                        });
                    _output.WriteLine($"[non-host] PUT /api/Setting/{EntityNames.ModuleDefinition}/{moduleDefinitionId} -> {(int)putStatus}");
                    Assert.Equal(HttpStatusCode.Forbidden, putStatus);
                }

                // and the master store is unchanged by the denied write
                using (var scope = app.Services.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingRepository>();
                    var stored = await settings.GetSettingAsync(EntityNames.ModuleDefinition, moduleDefinitionId, settingName);
                    Assert.Equal(settingValue, stored!.SettingValue);
                }
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private static async Task<int> FirstModuleDefinitionIdAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModuleDefinitionRepository>();
            var definitions = (await repository.GetModuleDefinitionsAsync()).ToList();
            Assert.NotEmpty(definitions);
            return definitions[0].ModuleDefinitionId;
        }

        private static async Task<int> UserIdAsync(WebApplication app, string username)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var aliasRepository = sp.GetRequiredService<IAliasRepository>();
            var alias = await aliasRepository.GetAliasAsync(Host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{Host}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            var users = sp.GetRequiredService<IUserRepository>();
            var user = await users.GetUserAsync(username);
            Assert.NotNull(user);
            return user!.UserId;
        }

        private async Task CreateRegisteredUserAsync(WebApplication app, string username, string password, string email)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var aliasRepository = sp.GetRequiredService<IAliasRepository>();
            var alias = await aliasRepository.GetAliasAsync(Host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{Host}'.");
            var tenantManager = sp.GetRequiredService<ITenantManager>();
            await tenantManager.SetAliasAsync(alias);

            // AddUser auto-assigns the Registered role (the registration path), which is exactly what
            // /pages/login requires — so the created user is a plain Registered (non-host, non-admin) user.
            var userManager = sp.GetRequiredService<IUserManager>();
            var created = await userManager.AddUser(new User
            {
                Username = username,
                Password = password,
                Email = email,
                DisplayName = username,
                SiteId = alias.SiteId,
                EmailConfirmed = true
            });
            Assert.NotNull(created);

            // sanity: the user carries Registered but NOT Host/Admin
            var roles = (await userManager.GetUserAsync(created!.UserId, alias.SiteId)).Roles ?? "";
            Assert.Contains(RoleNames.Registered, roles);
            Assert.DoesNotContain(RoleNames.Host, roles);
            Assert.DoesNotContain(RoleNames.Admin, roles);
        }

        /// <summary>POST /pages/login with a matched antiforgery pair; captures the Identity cookie.</summary>
        private async Task LoginAsync(WebApplication app, HttpClient client, CookieContainer cookies, string username, string password)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app);

            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(request);
            _output.WriteLine($"[login {username}] POST /pages/login -> {(int)response.StatusCode}");
            CaptureCookies(cookies, new Uri("http://" + Host), response);
        }

        /// <summary>
        /// PUT /api/Setting/{entityName}/{entityId} with the caller's Identity cookie and a matched antiforgery
        /// pair (the host globally validates antiforgery on unsafe methods). Returns the response status.
        /// </summary>
        private async Task<HttpStatusCode> PutSettingsAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, int moduleDefinitionId, List<Setting> settings)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), $"/api/Setting/{EntityNames.ModuleDefinition}/{moduleDefinitionId}");

            var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = JsonContent.Create(settings)
            };
            var cookieHeader = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                cookieHeader += "; ";
            }
            cookieHeader += antiforgeryCookie;
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);

            var response = await client.SendAsync(request);
            CaptureCookies(cookies, uri, response);
            return response.StatusCode;
        }

        private static async Task<List<Setting>> GetSettingsAsync(HttpClient client, CookieContainer cookies, int moduleDefinitionId)
        {
            var uri = new Uri(new Uri("http://" + Host), $"/api/Setting?entityname={EntityNames.ModuleDefinition}&entityid={moduleDefinitionId}");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<Setting>>() ?? new List<Setting>();
        }

        /// <summary>
        /// Mint a matched antiforgery cookie/request-token pair through the app's shared DataProtection ring.
        /// When <paramref name="userId"/> is supplied the generating context is AUTHENTICATED with that user's
        /// NameIdentifier claim, so the embedded claim-UID matches the authenticated request the token rides on
        /// (an anonymously-minted token is rejected with 400 on an authenticated request — the antiforgery binds
        /// the token to the user).
        /// </summary>
        private static (string requestToken, string cookie) GenerateAntiforgery(WebApplication app, int? userId = null)
        {
            using var scope = app.Services.CreateScope();
            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (userId.HasValue)
            {
                var identity = new System.Security.Claims.ClaimsIdentity(Constants.AuthenticationScheme);
                identity.AddClaim(new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, userId.Value.ToString()));
                httpContext.User = new System.Security.Claims.ClaimsPrincipal(identity);
            }
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            var setCookie = httpContext.Response.Headers.SetCookie.ToString();
            return (tokens.RequestToken!, setCookie.Split(';')[0]);
        }

        private void AssertIdentityCookie(CookieContainer cookies, string who)
        {
            var names = cookies.GetCookies(new Uri("http://" + Host)).Cast<Cookie>().Select(c => c.Name).ToList();
            _output.WriteLine($"[{who}] cookies after login: {string.Join(", ", names)}");
            Assert.True(names.Any(n => n.Contains("Identity.Application")),
                $"[{who}] Identity cookie was not issued by /pages/login — authentication failed.");
        }

        private static void CaptureCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return;
            }
            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';')[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var name = pair.Substring(0, eq).Trim();
                var value = pair.Substring(eq + 1).Trim();
                try
                {
                    cookies.Add(new Cookie(name, value, "/", uri.Host));
                }
                catch
                {
                    // ignore malformed/deletion cookies
                }
            }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        /// <summary>A throwaway content root + override settings file for a SQLite install, mirroring the render guard.</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-d88-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var connectionString = ScratchSqlite.ConnectionString(Path.Combine(ContentRoot, "stomme-d88.db"));
                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = Host,
                        ["HostUsername"] = HostUsername,
                        ["HostPassword"] = HostPassword,
                        ["HostEmail"] = HostEmail,
                        ["HostName"] = "Host"
                    }
                };
                System.IO.File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only - BL-104
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
