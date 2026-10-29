using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests
{
    /// <summary>
    /// A private setting's VALUE must never reach the event log. <c>SettingController</c> passed the
    /// whole <see cref="Setting"/> object to <c>ILogManager.Log</c> (and the settings import passed the whole
    /// CSV), and <c>LogManager</c> serialises arguments into BOTH <c>Log.Properties</c> and the interpolated
    /// <c>Log.Message</c> - so an SMTP password or API key saved through the admin UI landed in the site Log
    /// table, readable by every Administrator in the event-log viewer.
    /// <para>
    /// Driven over real HTTP against the composed host on SQLite. Captured at the REAL sink - the site Log
    /// table fed by LogQueue -> LogDrainService - because a capturing <c>ILogManager</c> registered in the
    /// <c>configureBuilder</c> callback is silently replaced by <c>AddStomme</c>'s later registration. A
    /// capturing <see cref="ILoggerProvider"/> (the Broadcast path) is asserted too. A positive control (the
    /// import's own log row reaching the table) keeps the negative assertions from being vacuous.
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SettingLogRedactionTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

        private const string Host = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Redact$2026";
        private const string HostEmail = "host@example.com";

        private readonly ITestOutputHelper _output;

        public SettingLogRedactionTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Private_setting_values_never_reach_the_log()
        {
            using var scratch = new ScratchInstall();
            var broadcast = new CapturingLoggerProvider();

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<ILoggerProvider>(broadcast);
                });

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

                var (hostUserId, siteId) = await HostUserAndSiteAsync(app);

                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };
                await LoginAsync(app, client, cookies, HostUsername, HostPassword);

                var run = Guid.NewGuid().ToString("N");
                var markerA = "BL112-PRIVATE-A-" + run;
                var markerB = "BL112-PRIVATE-B-" + run;
                var markerC = "BL112-PRIVATE-C-" + run;
                var markerD = "BL112-PRIVATE-D-" + run;
                var markers = new[] { markerA, markerB, markerC, markerD };
                const string settingName = "BL112:Private";

                // a) the bulk PUT's Created path
                var status = await SendAsync(app, client, cookies, hostUserId, HttpMethod.Put, $"/api/Setting/{EntityNames.Site}/{siteId}",
                    JsonContent.Create(new List<Setting>
                    {
                        new Setting { SettingId = 0, EntityName = EntityNames.Site, EntityId = siteId, SettingName = settingName, SettingValue = markerA, IsPrivate = true }
                    }));
                _output.WriteLine($"a) PUT /api/Setting/Site/{siteId} (create) -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                // b) the bulk PUT's Updated path
                status = await SendAsync(app, client, cookies, hostUserId, HttpMethod.Put, $"/api/Setting/{EntityNames.Site}/{siteId}",
                    JsonContent.Create(new List<Setting>
                    {
                        new Setting { SettingId = -1, EntityName = EntityNames.Site, EntityId = siteId, SettingName = settingName, SettingValue = markerB, IsPrivate = true }
                    }));
                _output.WriteLine($"b) PUT /api/Setting/Site/{siteId} (update) -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                // c) the Host CSV import
                var csv = "Entity,EntityId,SettingName,SettingValue,IsPrivate\n" +
                          $"{EntityNames.Site},{siteId},BL112:Import,{markerC},true\n";
                status = await SendAsync(app, client, cookies, hostUserId, HttpMethod.Post, "/api/Setting/import",
                    JsonContent.Create(new Result(false, csv)));
                _output.WriteLine($"c) POST /api/Setting/import -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                // d) the single-setting PUT by id
                var stored = await ReadSettingAsync(app, siteId, settingName);
                Assert.Equal(markerB, stored.SettingValue);
                stored.SettingValue = markerD;
                status = await SendAsync(app, client, cookies, hostUserId, HttpMethod.Put, $"/api/Setting/{stored.SettingId}",
                    JsonContent.Create(stored));
                _output.WriteLine($"d) PUT /api/Setting/{stored.SettingId} -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Equal(markerC, (await ReadSettingAsync(app, siteId, "BL112:Import")).SettingValue);
                Assert.Equal(markerD, (await ReadSettingAsync(app, siteId, settingName)).SettingValue);

                // e) positive control: the drain reached the Log table (the import's own row is written last)
                List<Log> logs = new();
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < DrainTimeout)
                {
                    logs = await ReadLogsAsync(app, siteId);
                    if (logs.Any(l => (l.Message ?? "").Contains("Settings Imported")) &&
                        logs.Count(l => (l.Message ?? "").StartsWith("Setting ")) >= 3)
                    {
                        break;
                    }
                    await Task.Delay(250);
                }
                _output.WriteLine($"log rows read: {logs.Count} after {clock.ElapsedMilliseconds} ms");
                foreach (var log in logs.Where(l => (l.Feature ?? "").Contains("Setting") || (l.Message ?? "").Contains("Setting")))
                {
                    _output.WriteLine($"  [{log.Level}] {log.Message} | props={log.Properties} | url={log.Url}");
                }
                Assert.Contains(logs, l => (l.Message ?? "").Contains("Settings Imported"));
                Assert.True(logs.Count(l => (l.Message ?? "").StartsWith("Setting ")) >= 3,
                    "Expected the Created/Updated setting log rows to reach the Log table (positive control).");

                // f) no marker in any persisted Message/MessageTemplate/Properties, nor in any broadcast message
                foreach (var marker in markers)
                {
                    var leaked = logs.FirstOrDefault(l =>
                        (l.Message ?? "").Contains(marker) ||
                        (l.MessageTemplate ?? "").Contains(marker) ||
                        (l.Properties ?? "").Contains(marker) ||
                        (l.Exception ?? "").Contains(marker) ||
                        // The OTHER door. LogManager copies the request URL into Log.Url for every entry
                        // raised during the request, and the old client route carried the value as a PATH
                        // SEGMENT - so the value reached the log even with every argument redacted. Asserting
                        // only on Message/Properties is what let that stand.
                        (l.Url ?? "").Contains(marker));
                    Assert.True(leaked == null,
                        $"Private setting value marker '{marker}' found in Log row {leaked?.LogId}: Message='{leaked?.Message}' Properties='{leaked?.Properties}' Url='{leaked?.Url}'");

                    var broadcastLeak = broadcast.Entries.FirstOrDefault(e => (e.Message ?? "").Contains(marker));
                    Assert.True(broadcastLeak.Message == null,
                        $"Private setting value marker '{marker}' found in broadcast ILogger message: '{broadcastLeak.Message}'");
                }

                // The Url column is asserted above now, not merely printed. What this test does NOT reach is the
                // legacy value-in-path route - these routes all carry the value in a body - so that door has its
                // own test below.
                _output.WriteLine($"Log.Url rows carrying a marker: {logs.Count(l => markers.Any(m => (l.Url ?? "").Contains(m)))}");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// The legacy value-in-PATH route must not put the value in <c>Log.Url</c>.
        /// </summary>
        /// <remarks>
        /// The test above drives the JSON routes, which carry the value in a body — so it could never have seen
        /// this door, and said so in a comment rather than covering it. <c>LogManager</c> copies the request URL
        /// into <c>Log.Url</c> for EVERY entry raised during a request, so a value in the path reached the site
        /// Log table with every log ARGUMENT correctly redacted. The client no longer uses this route;
        /// it is kept for compatibility and now declares a redacted URL for its own request.
        /// </remarks>
        [Fact]
        public async Task The_legacy_value_in_path_route_keeps_the_value_out_of_the_log_url()
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
                Assert.True((await installTask).Success);

                await app.StartAsync();
                var server = app.GetTestServer();
                var (hostUserId, siteId) = await HostUserAndSiteAsync(app);

                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };
                await LoginAsync(app, client, cookies, HostUsername, HostPassword);

                var marker = "BL148-PATH-" + Guid.NewGuid().ToString("N");
                var status = await SendAsync(app, client, cookies, hostUserId, HttpMethod.Put,
                    $"/api/Setting/{EntityNames.Site}/{siteId}/BL148%3APrivate/{marker}/true", null);
                _output.WriteLine($"legacy PUT (value in path) -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                // POSITIVE CONTROL: the write landed, so the route really ran and really logged.
                Assert.Equal(marker, (await ReadSettingAsync(app, siteId, "BL148:Private")).SettingValue);

                List<Log> logs = new();
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < DrainTimeout)
                {
                    logs = await ReadLogsAsync(app, siteId);
                    if (logs.Any(l => (l.Message ?? "").StartsWith("Setting "))) { break; }
                    await Task.Delay(250);
                }

                Assert.True(logs.Any(l => (l.Message ?? "").StartsWith("Setting ")),
                    "the route's own log entry never reached the table, so the assertion below would prove nothing");

                var leaked = logs.FirstOrDefault(l =>
                    (l.Url ?? "").Contains(marker) || (l.Message ?? "").Contains(marker) ||
                    (l.Properties ?? "").Contains(marker) || (l.MessageTemplate ?? "").Contains(marker));

                foreach (var row in logs.Where(l => (l.Url ?? "").Contains("BL148")))
                {
                    _output.WriteLine($"  url={row.Url}");
                }

                Assert.True(leaked == null,
                    $"The private value reached the log through the URL: Url='{leaked?.Url}' Message='{leaked?.Message}'");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private static async Task<(int userId, int siteId)> HostUserAndSiteAsync(WebApplication app)
        {
            using var scope = await AliasScopeAsync(app);
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetUserAsync(HostUsername);
            Assert.NotNull(user);
            var alias = await scope.ServiceProvider.GetRequiredService<ITenantManager>().GetAliasAsync();
            return (user!.UserId, alias.SiteId);
        }

        private static async Task<Setting> ReadSettingAsync(WebApplication app, int siteId, string settingName)
        {
            using var scope = await AliasScopeAsync(app);
            var setting = await scope.ServiceProvider.GetRequiredService<ISettingRepository>()
                .GetSettingAsync(EntityNames.Site, siteId, settingName);
            Assert.NotNull(setting);
            return setting;
        }

        private static async Task<List<Log>> ReadLogsAsync(WebApplication app, int siteId)
        {
            using var scope = await AliasScopeAsync(app);
            return (await scope.ServiceProvider.GetRequiredService<ILogRepository>().GetLogsAsync(siteId, null, null, 1000)).ToList();
        }

        private static async Task<IServiceScope> AliasScopeAsync(WebApplication app)
        {
            var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(Host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{Host}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return scope;
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
                "Identity cookie was not issued by /pages/login - authentication failed.");
        }

        /// <summary>An API call with the Identity cookie and a matched antiforgery pair.</summary>
        private static async Task<HttpStatusCode> SendAsync(WebApplication app, HttpClient client, CookieContainer cookies,
            int userId, HttpMethod method, string path, HttpContent content)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), path);

            var request = new HttpRequestMessage(method, uri) { Content = content };
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

        /// <summary>Captures every message broadcast through ILogger (LogManager's Broadcast path).</summary>
        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(string Category, MsLogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

            public void Dispose() { }

            private sealed class CapturingLogger(string category, ConcurrentQueue<(string, MsLogLevel, string)> sink) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

                public bool IsEnabled(MsLogLevel logLevel) => true;

                public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    sink.Enqueue((category, logLevel, formatter(state, exception)));

                private sealed class NullScope : IDisposable
                {
                    public static readonly NullScope Instance = new();
                    public void Dispose() { }
                }
            }
        }

        /// <summary>A throwaway content root + override settings file for a SQLite install, mirroring the render guard.</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;
            private readonly string _connectionString;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-bl112-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                _connectionString = ScratchSqlite.ConnectionString(ContentRoot, "stomme-bl112.db");
                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = _connectionString },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = Host,
                        ["HostUsername"] = HostUsername,
                        ["HostPassword"] = HostPassword,
                        ["HostEmail"] = HostEmail,
                        ["HostName"] = "Host"
                    },
                    // persist Information entries (the default) AND broadcast them through ILogger
                    ["Logging"] = new JsonObject
                    {
                        ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Broadcast"] = "Information" }
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
                // This install's pools only, never the process-wide ClearAllPools. Naming one store was
                // not enough - an installed host also opens the FeatureManagement and module-aggregate sidecars -
                // so ReleaseInstall enumerates what is actually under the content root instead.
                ScratchSqlite.ReleaseInstall(ContentRoot);
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}