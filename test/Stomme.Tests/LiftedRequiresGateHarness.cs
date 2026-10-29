using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The shared harness of the BL-99 DRIVEN gates: one installed store, one composed production host, and
    /// the HTTP verbs the three gate suites drive it with. Nothing here asserts - the suites do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything goes over REAL HTTP through <see cref="Program.BuildAppAsync"/> + the production middleware
    /// pipeline with the transport swapped for TestServer, the recipe
    /// <c>ModuleDefinitionSettingsAuthorizationTests</c> established (Identity cookie from
    /// <c>/pages/login</c>, a matched antiforgery pair on every unsafe method). Compiling and unit-green has
    /// been wrong here before - the D47 rename was green on 452 unit tests, the conformance suite on both
    /// providers AND the render guard while the feature was broken.
    /// </para>
    /// </remarks>
    internal static class GateDrive
    {
        public const string HostUsername = "host";
        public const string HostPassword = "H0st!Gate$2026";
        public const string HostEmail = "host@gate.test";

        public const string GateUsername = "gateuser";
        public const string GatePassword = "G4te!User$2026";
        public const string GateEmail = "gateuser@gate.test";

        public const string PlainUsername = "plainuser";
        public const string PlainPassword = "Pl4in!User$2026";
        public const string PlainEmail = "plainuser@gate.test";

        /// <summary>The custom role the key is granted to - NOT Administrators, so no role fallback can admit it.</summary>
        public const string GateRole = "GateRole";

        public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        // ---- the host --------------------------------------------------------------------------------------

        /// <summary>
        /// Every framework log entry at Error or above, captured from the host's own <c>ILogger</c> pipeline.
        /// </summary>
        /// <remarks>
        /// <c>ExceptionMiddleware</c> answers an unhandled exception with an EMPTY 500 and files the only
        /// account of it through <c>ILogManager</c> (the site's Log TABLE) or, with no alias, the framework
        /// logger. When the failure IS the database, the table write fails too and the 500 has no cause
        /// attached anywhere a test can read - measured: a site-log read after such a 500 returned nothing.
        /// This provider is the channel that survives that, so a red gate names its own cause.
        /// </remarks>
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> CapturedErrors = new();

        public static Task<WebApplication> InstallAsync(ScratchGateInstall scratch, ITestOutputHelper output) =>
            InstallAsync(scratch, output, configure: null, start: true);

        /// <summary>
        /// Installs (or, over an existing store, boots) a host. <paramref name="configure"/> runs inside
        /// <see cref="Program.BuildAppAsync"/>'s hook - BEFORE <c>AddStomme</c> composes the host, so a service
        /// registered there pre-empts any framework <c>TryAdd</c> of the same service (a test clock, for instance).
        /// <paramref name="start"/> false stops BEFORE the hosted services start - exactly where <c>Program.Main</c>
        /// stands after <c>InstallAsync</c> and before <c>RunAsync</c>.
        /// </summary>
        public static Task<WebApplication> InstallAsync(
            ScratchGateInstall scratch, ITestOutputHelper output, Action<WebApplicationBuilder>? configureBuilder) =>
            InstallAsync(scratch, output, configureBuilder, start: true);

        /// <inheritdoc cref="InstallAsync(ScratchGateInstall, ITestOutputHelper, Action{WebApplicationBuilder})"/>
        public static async Task<WebApplication> InstallAsync(
            ScratchGateInstall scratch, ITestOutputHelper output, Action<WebApplicationBuilder> configure, bool start)
        {
            CapturedErrors.Clear();
            var phase = System.Diagnostics.Stopwatch.StartNew();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Logging.AddProvider(new CapturingLoggerProvider(CapturedErrors));
                    configure?.Invoke(builder);
                });
            var buildMs = phase.ElapsedMilliseconds;

            if (scratch.IsClone)
            {
                // A clone whose copy silently did nothing is an EMPTY store, which the installer below would simply
                // install - every case green, and the template bought nothing. Asked BEFORE the installer runs.
                var aliases = 0;
                try
                {
                    using var scope = app.Services.CreateScope();
                    aliases = (await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync()).Count();
                }
                catch (Exception ex)
                {
                    output.WriteLine($"[{scratch.Store.Name}] reading the cloned store's aliases threw: {ex.GetType().Name}: {Truncate(ex.Message)}");
                }

                if (aliases == 0)
                {
                    await ShutdownAsync(app);
                }

                Assert.True(aliases > 0,
                    $"[{scratch.Store.Name}] this install is a CLONE of a template, yet its store holds no alias before the installer ran - " +
                    "the copy did not happen, and booting it would quietly install afresh.");
            }

            phase.Restart();

            var installTask = Task.Run(() => Program.InstallAsync(app));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"The {scratch.Store.Name} installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
            }

            var install = await installTask;
            output.WriteLine($"[{scratch.Store.Name}][install] Success={install.Success} Message={Truncate(install.Message)}");
            Assert.True(install.Success, $"[{scratch.Store.Name}] installer failed: {install.Message}");

            var installMs = phase.ElapsedMilliseconds;
            phase.Restart();

            if (start)
            {
                await app.StartAsync();
            }

            // Where a driven test's seconds go. Kept on every run, because the gate's duration was argued
            // from guesses for a long time and the answer differs per provider.
            output.WriteLine($"[{scratch.Store.Name}][timing] compose {buildMs} ms, install {installMs} ms, start {phase.ElapsedMilliseconds} ms");

            return app;
        }

        /// <summary>
        /// Starts every hosted service EXCEPT the scheduled jobs (<see cref="HostedServiceBase"/>), for a drive that runs a
        /// job's <c>ExecuteJobAsync</c> itself and must not race the job's own per-minute loop. The transport is not
        /// started, so this host serves no HTTP; drive it through DI scopes.
        /// </summary>
        public static async Task StartNonJobHostedServicesAsync(WebApplication app)
        {
            foreach (var hosted in app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .Where(service => service is not HostedServiceBase && service.GetType().Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true))
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public static async Task ShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        public static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= 400 ? value : value.Substring(0, 400) + " ...");

        // ---- tenant-scoped work ----------------------------------------------------------------------------

        public static async Task<T> InTenantAsync<T>(WebApplication app, string host, Func<IServiceProvider, Alias, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{host}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return await work(sp, alias);
        }

        public static Task<Alias> AliasAsync(WebApplication app, string host) =>
            InTenantAsync(app, host, (sp, alias) => Task.FromResult(alias));

        public static Task<ModuleDefinition> GateTestDefinitionAsync(WebApplication app, string host) =>
            InTenantAsync(app, host, async (sp, alias) =>
            {
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>().GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.Single(item => item.Name == "GateTest");
            });

        public static Task<List<Permission>> RowsAsync(WebApplication app, string host, string entityName, string permissionName) =>
            InTenantAsync(app, host, async (sp, alias) =>
                (await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(alias.SiteId, entityName, permissionName)).ToList());

        public static string Describe(IEnumerable<Permission> rows) =>
            "[" + string.Join(", ", rows.Select(row =>
                $"{row.EntityName}:{row.PermissionName}:{row.RoleName ?? ("user " + row.UserId)}={row.IsAuthorized}(entityId {row.EntityId})")) + "]";

        /// <summary>Creates a site role if it is not there yet, and returns it.</summary>
        public static Task<Role> EnsureRoleAsync(WebApplication app, string host, string roleName) =>
            InTenantAsync(app, host, async (sp, alias) =>
            {
                var roles = sp.GetRequiredService<IRoleRepository>();
                return (await roles.GetRolesAsync(alias.SiteId)).FirstOrDefault(item => item.Name == roleName)
                    ?? await roles.AddRoleAsync(new Role
                    {
                        SiteId = alias.SiteId,
                        Name = roleName,
                        Description = "BL-99 gate role",
                        IsAutoAssigned = false,
                        IsSystem = false
                    });
            });

        /// <summary>
        /// Adds a user to the site through the real <see cref="IUserManager"/> (which auto-assigns Registered -
        /// the role <c>/pages/login</c> requires), then adds the named extra roles.
        /// </summary>
        /// <remarks>
        /// Roles are added BEFORE the user ever logs in, on purpose: role membership is baked into the Identity
        /// cookie at sign-in, so a role granted afterwards would not be in the principal and the gate would be
        /// measuring the wrong thing. Permission ROWS are read per request and are the moving part the gates
        /// actually drive.
        /// </remarks>
        public static Task<User> AddUserAsync(WebApplication app, string host, string username, string password, string email, params string[] extraRoles) =>
            InTenantAsync(app, host, async (sp, alias) =>
            {
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
                Assert.True(created != null, $"IUserManager.AddUser returned null for '{username}' on site {alias.SiteId}.");

                if (extraRoles.Length != 0)
                {
                    var roles = (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId)).ToList();
                    var userRoles = sp.GetRequiredService<IUserRoleRepository>();
                    foreach (var roleName in extraRoles)
                    {
                        var role = roles.Single(item => item.Name == roleName);
                        await userRoles.AddUserRoleAsync(new UserRole { UserId = created.UserId, RoleId = role.RoleId });
                    }
                }

                return created;
            });

        public static Task<int> UserIdAsync(WebApplication app, string host, string username) =>
            InTenantAsync(app, host, async (sp, alias) =>
            {
                var user = await sp.GetRequiredService<IUserRepository>().GetUserAsync(username);
                Assert.True(user != null, $"No user '{username}'.");
                return user.UserId;
            });

        public static Task<string> RolesAsync(WebApplication app, string host, int userId) =>
            InTenantAsync(app, host, async (sp, alias) =>
                (await sp.GetRequiredService<IUserManager>().GetUserAsync(userId, alias.SiteId)).Roles ?? "");

        // ---- HTTP ------------------------------------------------------------------------------------------

        /// <summary>
        /// A client with its OWN remote address. The host rate-limits the account endpoints per (site, client
        /// address), and a TestServer request has no address at all - so without this every login a test performed on one
        /// host shared one partition, and a suite that signs several users in would be refused by a limiter guarding
        /// against exactly nothing. Distinct clients are distinct browsers; one client is still one browser.
        /// </summary>
        public static HttpClient NewClient(WebApplication app, string host)
        {
            var n = Interlocked.Increment(ref _clientAddress);
            var address = new IPAddress(new byte[] { 10, 77, (byte)((n >> 8) & 0xFF), (byte)(n & 0xFF) });
            return new HttpClient(app.GetTestServer().CreateHandler(context => context.Connection.RemoteIpAddress = address)) { BaseAddress = new Uri("http://" + host) };
        }

        private static int _clientAddress;

        public static async Task<CookieContainer> LoginAsync(WebApplication app, HttpClient client, string host, string username, string password, ITestOutputHelper output)
        {
            var cookies = new CookieContainer();
            var (requestToken, antiforgeryCookie) = Antiforgery(app);

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
            CaptureCookies(cookies, new Uri("http://" + host), response);

            var names = cookies.GetCookies(new Uri("http://" + host)).Cast<Cookie>().Select(cookie => cookie.Name).ToList();
            output.WriteLine($"[login {username}@{host}] POST /pages/login -> {(int)response.StatusCode}; cookies: {string.Join(", ", names)}");
            Assert.True(names.Any(name => name.Contains("Identity.Application")),
                $"[{username}@{host}] the Identity cookie was not issued by /pages/login - authentication failed, so nothing below would mean anything.");
            return cookies;
        }

        /// <summary>A GET carrying the caller's Identity cookie (or none, for the anonymous case).</summary>
        public static async Task<(HttpStatusCode Status, string Body, string ContentType)> GetAsync(
            HttpClient client, CookieContainer? cookies, string host, string path, ITestOutputHelper output, string label)
        {
            var uri = new Uri(new Uri("http://" + host), path);
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = cookies?.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            output.WriteLine($"[{label}] GET {path} -> {(int)response.StatusCode} {contentType} {Truncate(body)}");
            return (response.StatusCode, body, contentType);
        }

        /// <summary>PUT /api/ModuleDefinition/{id} - the admin-grid save path, the way a browser performs it.</summary>
        public static async Task<HttpStatusCode> PutDefinitionAsync(
            WebApplication app, HttpClient client, CookieContainer cookies, string host, int userId, ModuleDefinition definition, ITestOutputHelper output, string label)
        {
            var (requestToken, antiforgeryCookie) = Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + host), $"/api/ModuleDefinition/{definition.ModuleDefinitionId}");

            var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = JsonContent.Create(definition) };
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

            // The BODY and the SITE LOG on a non-2xx, always. A bare status turned an intermittent 500 on this
            // very PUT into a mystery once already: ExceptionMiddleware answers an unhandled exception with an
            // EMPTY 500 and puts the only account of it in the site's own Log table, so a test that reads
            // neither is measuring a number with no cause attached.
            if (!response.IsSuccessStatusCode)
            {
                output.WriteLine($"[{label}] PUT /api/ModuleDefinition/{definition.ModuleDefinitionId} -> {(int)response.StatusCode} " +
                    Truncate(await response.Content.ReadAsStringAsync()));
                foreach (var error in DrainCapturedErrors())
                {
                    output.WriteLine($"[{label}] host log: {error}");
                }

                foreach (var error in await LastErrorsAsync(app, host))
                {
                    output.WriteLine($"[{label}] site log: {error}");
                }
            }
            else
            {
                output.WriteLine($"[{label}] PUT /api/ModuleDefinition/{definition.ModuleDefinitionId} -> {(int)response.StatusCode}");
            }

            return response.StatusCode;
        }

        /// <summary>
        /// Any verb with an optional JSON body, carrying the caller's Identity cookie when there is one and a
        /// matched antiforgery pair always - the shape the admin UI sends. Pass <c>cookies: null</c> and
        /// <c>actorUserId: null</c> for the ANONYMOUS caller.
        /// </summary>
        /// <remarks>
        /// Modelled on <see cref="PutDefinitionAsync"/>, including its rule that a non-2xx logs the BODY, the
        /// host's captured errors and the site log: an unhandled exception is an EMPTY 500 whose only account
        /// is in those channels.
        /// </remarks>
        public static async Task<(HttpStatusCode Status, string Body)> SendJsonAsync(
            WebApplication app, HttpClient client, CookieContainer? cookies, string host, int? actorUserId,
            HttpMethod method, string path, object? body, ITestOutputHelper output, string label)
        {
            var (requestToken, antiforgeryCookie) = Antiforgery(app, actorUserId);
            var uri = new Uri(new Uri("http://" + host), path);

            var request = new HttpRequestMessage(method, uri);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            var cookieHeader = cookies?.GetCookieHeader(uri) ?? "";
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                cookieHeader += "; ";
            }
            cookieHeader += antiforgeryCookie;
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);

            var response = await client.SendAsync(request);
            if (cookies != null)
            {
                CaptureCookies(cookies, uri, response);
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                output.WriteLine($"[{label}] {method} {path} -> {(int)response.StatusCode} {Truncate(responseBody)}");
                foreach (var error in DrainCapturedErrors())
                {
                    output.WriteLine($"[{label}] host log: {error}");
                }

                foreach (var error in await LastErrorsAsync(app, host))
                {
                    output.WriteLine($"[{label}] site log: {error}");
                }
            }
            else
            {
                output.WriteLine($"[{label}] {method} {path} -> {(int)response.StatusCode}");
            }

            return (response.StatusCode, responseBody);
        }

        /// <summary>Drains what the host's own logger recorded at Error or above.</summary>
        public static IReadOnlyList<string> DrainCapturedErrors()
        {
            var drained = new List<string>();
            while (CapturedErrors.TryDequeue(out var entry))
            {
                drained.Add(entry);
            }

            return drained;
        }
        /// <summary>The site's last few ERROR log entries - where ExceptionMiddleware files an empty 500.</summary>
        public static async Task<IReadOnlyList<string>> LastErrorsAsync(WebApplication app, string host, int rows = 3)
        {
            try
            {
                return await InTenantAsync(app, host, async (sp, alias) =>
                    (await sp.GetRequiredService<ILogRepository>().GetLogsAsync(alias.SiteId, "Error", "", rows))
                        .Select(log => Truncate($"{log.Category}.{log.Feature}: {log.Message} {log.Exception}"))
                        .ToList() as IReadOnlyList<string>);
            }
            catch (Exception ex)
            {
                return new[] { "(could not read the site log: " + ex.Message + ")" };
            }
        }
        public static ModuleDefinition Clone(ModuleDefinition definition)
        {
            var clone = JsonSerializer.Deserialize<ModuleDefinition>(JsonSerializer.Serialize(definition))!;
            clone.PermissionList = definition.PermissionList.Select(row => row.Clone()).ToList();
            return clone;
        }

        public static (string RequestToken, string Cookie) Antiforgery(WebApplication app, int? userId = null)
        {
            using var scope = app.Services.CreateScope();
            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (userId.HasValue)
            {
                var identity = new ClaimsIdentity(Constants.AuthenticationScheme);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
                httpContext.User = new ClaimsPrincipal(identity);
            }
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            var setCookie = httpContext.Response.Headers.SetCookie.ToString();
            return (tokens.RequestToken!, setCookie.Split(';')[0]);
        }

        public static void CaptureCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
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

                try
                {
                    cookies.Add(new Cookie(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim(), "/", uri.Host));
                }
                catch
                {
                    // ignore malformed/deletion cookies
                }
            }
        }
    }

    /// <summary>A store the gates can install against. Adding one is an implementation plus a suite row.</summary>
    internal interface IGateStore
    {
        string Name { get; }

        /// <summary>The assembly-qualified IDatabase type, as it appears in AvailableDatabases.</summary>
        string DatabaseType { get; }

        /// <summary>True when the store is actually reachable. A gate FAILS (never skips) when it is not.</summary>
        bool IsReachable();

        /// <summary>What to tell the reader when it is unreachable - the D46 fail-not-skip message.</summary>
        string UnreachableMessage { get; }

        string CreateConnectionString(string scratchDirectory);

        void Cleanup(string connectionString);

        // ---- BL-164: cloning an installed template (GateInstallTemplates) -------------------------------------

        /// <summary>
        /// The one token that names THIS store's database in a settings file - what a clone rewrites. Empty for
        /// SQLite, whose database is a file under the content root and moves with it.
        /// </summary>
        string DatabaseToken { get; }

        /// <summary>A sibling store under the TEMPLATE prefix, which <see cref="SweepStaleTemplatesAsync"/> owns.</summary>
        IGateStore NewTemplate();

        /// <summary>Removes templates a dead run left behind. Once per process.</summary>
        Task SweepStaleTemplatesAsync();

        /// <summary>Makes an installed template copyable: no session may hold it while it is being copied.</summary>
        Task QuiesceAsync();

        /// <summary>Copies the installed <paramref name="template"/> into THIS store's own, new database.</summary>
        Task CloneFromAsync(IGateStore template);
    }

    internal sealed class SqliteGateStore : IGateStore
    {
        public const string StoreName = "SQLite";

        public string Name => StoreName;

        public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

        public bool IsReachable() => true;

        public string UnreachableMessage => "";

        public string DatabaseToken => "";

        public IGateStore NewTemplate() => new SqliteGateStore();

        public Task SweepStaleTemplatesAsync() => Task.CompletedTask;

        public Task QuiesceAsync() => Task.CompletedTask;

        /// <summary>Nothing to do: the database is a file under the content root, and the content root was copied.</summary>
        public Task CloneFromAsync(IGateStore template) => Task.CompletedTask;

        /// <summary>
        /// KNOWN INTERMITTENT ON THIS LEG, cause captured, NOT the bridge's: an empty HTTP 500 from
        /// <c>SQLite Error 5: 'database is locked'</c>. Read this before "fixing" a red SQLite run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What was observed.</b> In roughly 4 of 40 executions - only ever inside a full run, never once in
        /// 22 isolated runs of the same case and never in 150 back-to-back PUTs - the module-definition PUT
        /// answered an EMPTY 500. Captured through the host's own logger (that is what
        /// <see cref="GateDrive.CapturedErrors"/> exists for; the SITE log holds nothing, because when the
        /// failure IS the database the log write fails too):
        /// <code>
        /// Microsoft.EntityFrameworkCore.Update: An exception occurred in the database while saving changes for
        /// context type 'Stomme.Repository.MasterDBContext'.
        ///   ---&gt; Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'.
        /// </code>
        /// </para>
        /// <para>
        /// <b>The mechanism.</b> A file-backed SQLite database admits ONE writer, and a Stomme host writes to
        /// it from a BACKGROUND thread on every single request - the D34 log drain, which takes its own scope
        /// and its own connection - while the request writes too. A scratch install puts the master and tenant
        /// stores in the SAME file, so the definition PUT's <c>MasterDBContext.SaveChanges</c> collides with a
        /// drain write. SQLite does not run the busy handler for a read-then-write upgrade that another
        /// connection already holds reserved, so the command fails immediately rather than waiting out the
        /// command timeout: no amount of timeout tuning helps, and that is why this only shows up when the
        /// machine is loaded enough for the two windows to overlap.
        /// </para>
        /// <para>
        /// <b>It is not this gate's defect and it is not the bridge's.</b> The same PUT served 150/150 without
        /// contention, and the PostgreSQL and RavenDB legs - which have real concurrent writers - have never
        /// shown it. It belongs to file-SQLite hosting (a SUPPORTED install option), it is the best candidate
        /// yet for the "~1 in 5 SQLite render FAILURE" the render guard's own remarks record as never
        /// reproduced in 102 isolated runs, and the fix (WAL journal mode plus a busy handler, or serialising
        /// the drain against request writes) is a product change, not a test change. Escalated, not hidden: a
        /// retry here would mask exactly the defect worth knowing about.
        /// </para>
        /// <para>
        /// <b>Pooling was off here, and that MADE IT WORSE - measured, and corrected.</b> The original reason
        /// was sound in half: <c>SqliteConnection.ClearAllPools()</c> is PROCESS-WIDE, about a dozen classes in
        /// this assembly call it in teardown, and a probe calling it every 25 ms while this PUT ran produced 3
        /// non-200 in 60 - so this fixture must not call it. But <c>Pooling=False</c> was the wrong way to earn
        /// that: it makes every operation open a NEW connection, which multiplies the writers competing for
        /// the single write lock above. Driven on the sibling suite that had just been given the same
        /// treatment: <b>2 failures in 4 runs unpooled, 0 in 5 pooled</b>, same binary, same machine, same
        /// minute. Pooled plus a TARGETED <c>ClearPool</c> for this connection string alone releases the
        /// scratch file without touching any other suite's pool. The render guard's teardown still justifies
        /// ClearAllPools with "the suite's other SQLite users are all in-memory", which stopped being true once
        /// the install suites became file-backed (backlog BL-104).
        /// </para>
        /// </remarks>
        public string CreateConnectionString(string scratchDirectory)
        {
            // The lock above is real product behaviour on a file-backed SQLite install - one writer at a
            // time, and the D34 log drain writes from a background thread while a request writes. The HARNESS
            // stops paying for it twice: "Default Timeout" makes a writer WAIT for the lock instead of failing
            // immediately, and WAL (set on the file below, since it is a database-level property) lets readers
            // run while one writer holds it. Neither hides the product hazard - BL-104 still carries the
            // product-side fix - they stop a green run from being decided by thread timing.
            var path = Path.Combine(scratchDirectory, "stomme-gate.db");
            var connectionString = $"Data Source={path};Default Timeout=30;";
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                connection.Open();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                pragma.ExecuteNonQuery();
            }

            return connectionString;
        }

        public void Cleanup(string connectionString)
        {
            // ClearPool, never ClearAllPools: this releases the pooled connections of THIS scratch database so
            // the directory can be deleted, and leaves every other suite's pool alone.
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
            catch
            {
                // best effort - the scratch directory delete is already best-effort too
            }
        }
    }

    internal sealed class PostgresGateStore : IGateStore
    {
        public const string StoreName = "PostgreSQL";

        private const string TemplatePrefix = "stomme_gate_tmpl_";
        private static int _swept;

        private readonly string _database;

        public PostgresGateStore()
            : this("stomme_authz_gate_")
        {
        }

        private PostgresGateStore(string prefix) => _database = prefix + Guid.NewGuid().ToString("N").Substring(0, 12);

        public string Name => StoreName;

        public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";

        public string DatabaseToken => _database;

        public IGateStore NewTemplate() => new PostgresGateStore(TemplatePrefix);

        public async Task SweepStaleTemplatesAsync()
        {
            if (Interlocked.Exchange(ref _swept, 1) != 0)
            {
                return;
            }

            var stale = new List<string>();
            await using (var connection = new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString("postgres")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT datname FROM pg_database WHERE datname LIKE @prefix";
                command.Parameters.AddWithValue("prefix", TemplatePrefix.Replace("_", "\\_") + "%");
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    stale.Add(reader.GetString(0));
                }
            }

            foreach (var database in stale)
            {
                PostgresTestEndpoint.DropDatabase(database);
            }
        }

        /// <summary>CREATE DATABASE ... TEMPLATE refuses a template anybody is connected to.</summary>
        public async Task QuiesceAsync()
        {
            await using var connection = new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString("postgres"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()";
            command.Parameters.AddWithValue("database", _database);
            await command.ExecuteNonQueryAsync();
        }

        public async Task CloneFromAsync(IGateStore template)
        {
            await using var connection = new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString("postgres"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_database}\" TEMPLATE \"{template.DatabaseToken}\"";
            await command.ExecuteNonQueryAsync();
        }

        public bool IsReachable() => PostgresTestEndpoint.IsReachable();

        public string UnreachableMessage =>
            $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}, so nothing here " +
            "proves the authorization bridge decides against a REAL relational store. This gate FAILS rather than " +
            "skips (the D46 principle): a suite that quietly shrinks reports green while proving nothing. Point " +
            "POSTGRES_TEST_HOST/POSTGRES_TEST_PORT at a reachable instance - the shared infra UAT tier is the default.";

        public string CreateConnectionString(string scratchDirectory) => PostgresTestEndpoint.ConnectionString(_database);

        public void Cleanup(string connectionString)
        {
            // The scratch database lives on a SHARED instance: a missed drop is visible to everyone else.
            var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database ?? "";
            PostgresTestEndpoint.DropDatabase(database);
        }
    }

    /// <summary>
    /// A full install on the shared infra SQL Server (UAT <c>:1434</c>, <see cref="SqlServerTestEndpoint"/>). It has
    /// no template clone - copying a SQL Server database needs a BACKUP file on the server's own disk - so
    /// <see cref="GateInstallTemplates.CloneAsync(IGateStore, string, Xunit.Abstractions.ITestOutputHelper)"/> installs it
    /// afresh; only the few suites that must see SQL Server use it.
    /// </summary>
    internal sealed class SqlServerGateStore : IGateStore
    {
        public const string StoreName = "SQL Server";

        private readonly string _database = "stomme_gate_" + Guid.NewGuid().ToString("N").Substring(0, 12);

        public string Name => StoreName;

        public string DatabaseType => "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server";

        public string DatabaseToken => _database;

        public IGateStore NewTemplate() => throw new NotSupportedException("SQL Server installs are not cloned from a template.");

        public Task SweepStaleTemplatesAsync() => Task.CompletedTask;

        public Task QuiesceAsync() => Task.CompletedTask;

        public Task CloneFromAsync(IGateStore template) => throw new NotSupportedException("SQL Server installs are not cloned from a template.");

        public bool IsReachable() => SqlServerTestEndpoint.IsReachable();

        public string UnreachableMessage =>
            $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port}, so nothing here proves the " +
            "behaviour on a real SQL Server. This gate FAILS rather than skips (the D46 principle). Point SQLSERVER_TEST_HOST/" +
            "SQLSERVER_TEST_PORT at a reachable instance - the shared infra UAT tier is the default.";

        public string CreateConnectionString(string scratchDirectory) => SqlServerTestEndpoint.ConnectionString(_database);

        public void Cleanup(string connectionString)
        {
            // The scratch database lives on a SHARED instance: release this database's pooled sessions, then drop it.
            try
            {
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                Microsoft.Data.SqlClient.SqlConnection.ClearPool(connection);
            }
            catch
            {
                // best effort - DropDatabase rolls back any session still open
            }

            SqlServerTestEndpoint.DropDatabase(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog);
        }
    }

    internal sealed class RavenGateStore : IGateStore
    {
        public const string StoreName = "RavenDB";

        private const string TemplatePrefix = "stomme_gate_tmpl_";

        private readonly string _database;

        public RavenGateStore()
            : this("stomme_authz_gate_")
        {
        }

        private RavenGateStore(string prefix) => _database = prefix + Guid.NewGuid().ToString("N").Substring(0, 12);

        public string DatabaseToken => _database;

        public IGateStore NewTemplate() => new RavenGateStore(TemplatePrefix);

        public async Task SweepStaleTemplatesAsync()
        {
            using var store = new DocumentStore { Urls = new[] { Url } }.Initialize();
            await RavenTestDatabaseJanitor.SweepAsync(store, TemplatePrefix);
        }

        public Task QuiesceAsync() => Task.CompletedTask;

        /// <summary>
        /// A smuggler export of the template streamed straight into a new database: documents, identities,
        /// compare-exchange values and index definitions - everything the installer produced.
        /// </summary>
        public async Task CloneFromAsync(IGateStore template)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var store = new DocumentStore { Urls = new[] { Url } }.Initialize();

            // The index definitions travel IN the new database's record - one cluster command for all of them. Left to
            // the smuggler each index is a command of its own (measured: a copy of 11-18 s against 1 s without them),
            // and left out altogether every first query of a case pays for its auto-index instead (measured on the same
            // eleven classes, same hour: 306 s without the definitions, 189 s with them in the record).
            var source = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(template.DatabaseToken));
            var record = new Raven.Client.ServerWide.DatabaseRecord(_database)
            {
                Indexes = source.Indexes,
                AutoIndexes = source.AutoIndexes,
            };
            await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(record));
            var createMs = timer.ElapsedMilliseconds;
            var operation = await store.Smuggler.ForDatabase(template.DatabaseToken).ExportAsync(
                new Raven.Client.Documents.Smuggler.DatabaseSmugglerExportOptions { OperateOnTypes = Raven.Client.Documents.Smuggler.DatabaseSmugglerOptions.DefaultOperateOnTypes & ~Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes },
                store.Smuggler.ForDatabase(_database));
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));
            LastCloneTiming = $"create {createMs} ms, copy {timer.ElapsedMilliseconds - createMs} ms";
        }

        /// <summary>Where the last clone's time went - the RavenDB half of BL-164's numbers.</summary>
        public static string LastCloneTiming { get; private set; } = "";

        public static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        public string Name => StoreName;

        public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";

        public bool IsReachable()
        {
            try
            {
                var endpoint = new Uri(Url);
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        public string UnreachableMessage =>
            $"RavenDB is not reachable at {Url}, so nothing here proves the authorization bridge decides the same " +
            "way on a DOCUMENT store. This gate FAILS rather than skips (D46): an EF-only run proves nothing about " +
            "the provider that has no rows table at all. Set RAVENDB_TEST_URL to a reachable server.";

        public string CreateConnectionString(string scratchDirectory) => $"Url={Url};Database={_database}";

        public void Cleanup(string connectionString)
        {
            try
            {
                // The install's own database AND its siblings: an OpenIddict-enabled case leaves {db}_OpenIddict, a
                // two-tenant case {db}_second, and deleting the one name left those behind on every run.
                using var store = new DocumentStore { Urls = new[] { Url } }.Initialize();
                RavenTestDatabaseJanitor.DeleteWithSiblings(store, _database);
            }
            catch
            {
                // best effort - the test-host lock's start-of-run sweep (RavenTestDatabaseSweep) removes what this misses
            }
        }
    }

    /// <summary>
    /// A throwaway content root + the <c>appsettings.override.json</c> the installer's self-writes land in, so
    /// the repository's own appsettings.json is never touched.
    /// </summary>
    internal sealed class ScratchGateInstall : IDisposable
    {
        private readonly string? _previousOverridePath;
        private readonly string _connectionString;

        public ScratchGateInstall(IGateStore store, string defaultAlias)
            : this(store, defaultAlias, colocateModuleAggregates: false, extraSettings: null)
        {
        }

        /// <param name="colocateModuleAggregates">
        /// True puts the relational module store (<c>ConnectionStrings:ModuleAggregates</c>) in the install's OWN
        /// database, so a PostgreSQL leg exercises PostgreSQL for module aggregates too; false keeps the production
        /// default, the SQLite sidecar under the content root's Data folder. Ignored meaning on RavenDB (module data
        /// lives in the document store regardless).
        /// </param>
        public ScratchGateInstall(IGateStore store, string defaultAlias, bool colocateModuleAggregates)
            : this(store, defaultAlias, colocateModuleAggregates, extraSettings: null)
        {
        }

        /// <param name="extraSettings">
        /// Top-level sections merged into <c>appsettings.override.json</c> BEFORE the host is built - for settings read
        /// at COMPOSITION (e.g. <c>Deployment:Mode</c>), which a write after <see cref="Program.BuildAppAsync"/> is too
        /// late for. A section named here replaces the default one of the same name.
        /// </param>
        public ScratchGateInstall(IGateStore store, string defaultAlias, JsonObject? extraSettings)
            : this(store, defaultAlias, colocateModuleAggregates: false, extraSettings)
        {
        }

        /// <inheritdoc cref="ScratchGateInstall(IGateStore, string, bool)"/>
        /// <inheritdoc cref="ScratchGateInstall(IGateStore, string, JsonObject)"/>
        public ScratchGateInstall(IGateStore store, string defaultAlias, bool colocateModuleAggregates, JsonObject? extraSettings)
        {
            Store = store;
            ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-authz-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ContentRoot);
            Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
            System.IO.File.Copy(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                Path.Combine(ContentRoot, "appsettings.json"));

            _connectionString = store.CreateConnectionString(ContentRoot);

            var connectionStrings = new JsonObject { ["DefaultConnection"] = _connectionString };
            if (colocateModuleAggregates)
            {
                connectionStrings["ModuleAggregates"] = _connectionString;
            }

            var overrides = new JsonObject
            {
                ["Database"] = new JsonObject { ["DefaultDBType"] = store.DatabaseType },
                ["ConnectionStrings"] = connectionStrings,
                ["Installation"] = new JsonObject
                {
                    ["DefaultAlias"] = defaultAlias,
                    ["HostUsername"] = GateDrive.HostUsername,
                    ["HostPassword"] = GateDrive.HostPassword,
                    ["HostEmail"] = GateDrive.HostEmail,
                    ["HostName"] = "Host"
                }
            };
            if (extraSettings != null)
            {
                foreach (var (name, section) in extraSettings)
                {
                    overrides[name] = section?.DeepClone();
                }
            }

            System.IO.File.WriteAllText(
                Path.Combine(ContentRoot, "appsettings.override.json"),
                overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
        }

        /// <summary>
        /// A pristine COPY of an installed template - its content root copied and re-pointed, its database
        /// cloned. See <see cref="GateInstallTemplates"/>; cases call <see cref="GateInstallTemplates.CloneAsync(IGateStore, string, ITestOutputHelper)"/>.
        /// </summary>
        internal static async Task<ScratchGateInstall> CloneOfAsync(IGateStore store, GateInstallTemplate template)
        {
            var clone = new ScratchGateInstall(store, template);
            try
            {
                await store.CloneFromAsync(template.Scratch.Store);
            }
            catch
            {
                clone.Dispose();
                throw;
            }

            return clone;
        }

        private ScratchGateInstall(IGateStore store, GateInstallTemplate template)
        {
            Store = store;
            IsClone = true;
            ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-authz-gate-" + Guid.NewGuid().ToString("N"));
            GateInstallTemplates.CopyTree(template.Scratch.ContentRoot, ContentRoot);
            GateInstallTemplates.RewriteSettings(ContentRoot, template, store);
            _connectionString = store.CreateConnectionString(ContentRoot);

            _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
        }

        public IGateStore Store { get; }

        public string ContentRoot { get; }

        /// <summary>True for a copy of an installed template: the store must ALREADY be installed when it boots.</summary>
        public bool IsClone { get; }

        private bool _environmentReleased;

        /// <summary>
        /// Hands the process-global override path back WITHOUT deleting anything - a template outlives the install
        /// that produced it, and must not stay "the current install" while other cases run.
        /// </summary>
        internal void ReleaseEnvironment()
        {
            if (_environmentReleased)
            {
                return;
            }

            _environmentReleased = true;

            // Restore the previous value ONLY if the variable still names this install. The variable is process-global
            // and hosts compose in more than one xunit collection; a fixture that captured "previous" while THIS
            // install was current and restores it after this install has moved on would point the next host at a
            // content root that is already gone - or, worse, still there. Measured in the first full gate after BL-164:
            // three cases saw another case's rows through exactly that stale restore.
            if (string.Equals(Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable), ContentRoot, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
            }
        }

        public void Dispose()
        {
            ReleaseEnvironment();
            try { Store.Cleanup(_connectionString); } catch { /* best effort */ }

            // This used to name ONE sidecar - the module-aggregate store - and that was the bug: an installed host
            // opens more SQLite files than any hand-written list remembers, and the one missing here was
            // FeatureManagement's Stomme.Features.db. Measured: a full gate run left 69 stomme-authz-gate-*
            // trees behind, every one of them holding exactly that file. ReleaseInstall enumerates instead of
            // knowing names, so a sidecar added by a future module is covered with no edit.
            ScratchSqlite.ReleaseInstall(ContentRoot);

            try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
        }
    }

    /// <summary>An <see cref="ILoggerProvider"/> that keeps every Error/Critical entry, message and exception.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sink;

        public CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> sink) => _sink = sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sink;

            public CapturingLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> sink)
            {
                _category = category;
                _sink = sink;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                _sink.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} {exception}");
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}