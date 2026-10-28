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
    /// BL-31 — the <c>SettingController</c> sync path must actually invalidate what a setting write makes
    /// stale. Two defects, both driven over real HTTP against the composed host (the D88 harness shape):
    /// <list type="number">
    ///   <item><b>The Host case.</b> <c>ServerSiteService.GetSiteAsync</c> caches the merged Site object, and
    ///     the sync switch refreshed it only for Module/Page/Site — so a Host-level setting change did not
    ///     take effect until restart (measured live on PostgreSQL during the Settings migration). A Host
    ///     setting PUT must fire a <see cref="EntityNames.Site"/>/<see cref="SyncEventActions.Refresh"/>
    ///     sync event.</item>
    ///   <item><b>The mangle.</b> The sync path still ran entity names through the title-casing
    ///     <c>FormatName</c> (the surviving half of the D88 root cause), so a multi-word entity's setting
    ///     event was published as e.g. <c>ModuledefinitionSetting</c> — a name no subscriber matching
    ///     <c>ModuleDefinitionSetting</c> ever sees. The event must carry the exact
    ///     <see cref="EntityNames"/> casing.</item>
    /// </list>
    /// Subscribes to the real <see cref="ISyncManager"/> (D35: subscribers are awaited before the write's
    /// caller proceeds), so an event recorded here IS the invalidation firing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SettingSyncInvalidationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Sync$2026";
        private const string HostEmail = "host@example.com";

        private readonly ITestOutputHelper _output;

        public SettingSyncInvalidationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Setting_writes_fire_the_sync_events_their_caches_depend_on()
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

                // record every sync event the controller publishes (D35: awaited before the PUT returns,
                // so by the time the response arrives the events are in the list)
                var recorded = new List<SyncEvent>();
                var syncManager = app.Services.GetRequiredService<ISyncManager>();
                syncManager.Subscribe(e => { lock (recorded) { recorded.Add(e); } return Task.CompletedTask; });

                int hostUserId = await UserIdAsync(app, HostUsername);
                int moduleDefinitionId = await FirstModuleDefinitionIdAsync(app);

                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };
                await LoginAsync(app, client, cookies, HostUsername, HostPassword);

                // ---- 1. a HOST setting write must refresh the merged Site object -------------------------
                var status = await PutSettingsAsync(app, client, cookies, hostUserId, EntityNames.Host, -1,
                    new List<Setting>
                    {
                        new Setting
                        {
                            SettingId = 0,
                            EntityName = EntityNames.Host,
                            EntityId = -1,
                            SettingName = "BL31:HostProbe",
                            SettingValue = "host-setting-written"
                        }
                    });
                _output.WriteLine($"PUT /api/Setting/{EntityNames.Host}/-1 -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                List<SyncEvent> hostEvents;
                lock (recorded) { hostEvents = recorded.ToList(); }
                _output.WriteLine("events after Host PUT: " +
                    string.Join(", ", hostEvents.Select(e => $"{e.EntityName}/{e.Action}")));

                Assert.Contains(hostEvents, e =>
                    e.EntityName == EntityNames.Site && e.Action == SyncEventActions.Refresh);

                // ---- 2. a multi-word entity's setting event must carry the EXACT entity name -------------
                lock (recorded) { recorded.Clear(); }

                status = await PutSettingsAsync(app, client, cookies, hostUserId, EntityNames.ModuleDefinition,
                    moduleDefinitionId,
                    new List<Setting>
                    {
                        new Setting
                        {
                            SettingId = 0,
                            EntityName = EntityNames.ModuleDefinition,
                            EntityId = moduleDefinitionId,
                            SettingName = "BL31:MangleProbe",
                            SettingValue = "module-definition-setting-written"
                        }
                    });
                _output.WriteLine($"PUT /api/Setting/{EntityNames.ModuleDefinition}/{moduleDefinitionId} -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                List<SyncEvent> mdEvents;
                lock (recorded) { mdEvents = recorded.ToList(); }
                _output.WriteLine("events after ModuleDefinition PUT: " +
                    string.Join(", ", mdEvents.Select(e => $"{e.EntityName}/{e.Action}")));

                Assert.Contains(mdEvents, e => e.EntityName == EntityNames.ModuleDefinition + "Setting");
                Assert.DoesNotContain(mdEvents, e => e.EntityName == "ModuledefinitionSetting");
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

            var names = cookies.GetCookies(new Uri("http://" + Host)).Cast<Cookie>().Select(c => c.Name).ToList();
            Assert.True(names.Any(n => n.Contains("Identity.Application")),
                "Identity cookie was not issued by /pages/login — authentication failed.");
        }

        /// <summary>PUT /api/Setting/{entityName}/{entityId} with the Identity cookie and a matched antiforgery pair.</summary>
        private async Task<HttpStatusCode> PutSettingsAsync(WebApplication app, HttpClient client, CookieContainer cookies,
            int userId, string entityName, int entityId, List<Setting> settings)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), $"/api/Setting/{entityName}/{entityId}");

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

        /// <summary>Mint a matched antiforgery cookie/request-token pair (authenticated when userId is supplied).</summary>
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
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-bl31-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var connectionString = ScratchSqlite.ConnectionString(Path.Combine(ContentRoot, "stomme-bl31.db"));
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
