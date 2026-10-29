using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Deleting the LAST alias of the Master tenant through
    /// <c>DELETE /api/Alias/{id}</c> must be refused, and the controller's delete log must say what actually happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DRIVEN through the real composed host over HTTP (the <see cref="GateDrive"/> recipe: SQLite install,
    /// Identity cookie from <c>/pages/login</c>, a matched antiforgery pair on the DELETE). The SQLite store is the
    /// pooled, targeted-<c>ClearPool</c> <see cref="SqliteGateStore"/>.
    /// </para>
    /// <para>
    /// What the UNCHANGED controller did, measured before the fix (the first test's output): the DELETE answered
    /// 200, the alias row was gone, <c>TenantRepository.DeleteTenantAsync</c> silently refused the Master tenant so
    /// its row survived, and the controller still logged <c>Tenant Deleted</c> - a false audit line over an
    /// installation left with no alias at all; the next <c>GET /</c> answered 200 with the page <c>Site Not Configured Correctly - No Matching Alias Exists For Host Name</c> (the installer was NOT re-triggered, and the alias auto-registration cannot fire with zero aliases), so the host name was bricked until an alias row was put back by hand.
    /// </para>
    /// <para>
    /// The truthful-log branch (log <c>Tenant Deleted</c> only when the row is gone) is not reachable over HTTP once
    /// the guard is in place - the only tenant the repository refuses is the Master, and the guard stops that
    /// request first. It is therefore pinned at the controller level with a tenant repository that silently
    /// refuses (<see cref="The_delete_log_line_matches_the_outcome"/>).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MasterTenantAliasGuardTests
    {
        private const string Host = "localhost";
        private const string RefusalMessage = "Refused Deleting The Last Alias Of The Master Tenant";

        private readonly ITestOutputHelper _output;

        public MasterTenantAliasGuardTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Deleting_the_master_tenants_last_alias_as_host_is_refused_and_the_alias_survives()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await InstallRecordingAsync(scratch);
            try
            {
                var master = await MasterTenantAsync(app);
                var masterAliases = (await AliasesAsync(app)).Where(item => item.TenantId == master.TenantId).ToList();
                _output.WriteLine($"[before] master tenant {master.TenantId} '{master.Name}' aliases: {string.Join(", ", masterAliases.Select(a => $"{a.AliasId}:{a.Name}"))}");
                Assert.NotEmpty(masterAliases);

                // A stock install carries exactly one; delete down to one if it does not.
                foreach (var extra in masterAliases.Skip(1))
                {
                    await InScopeAsync(app, sp => sp.GetRequiredService<IAliasRepository>().DeleteAliasAsync(extra.AliasId));
                }
                var target = masterAliases[0];

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var status = await DeleteAliasAsync(app, client, cookies, hostUserId, target.AliasId);

                // Record everything BEFORE asserting, so the RED names what happens today.
                var aliasAfter = (await AliasesAsync(app)).FirstOrDefault(item => item.AliasId == target.AliasId);
                var tenantAfter = (await TenantsAsync(app)).FirstOrDefault(item => item.TenantId == master.TenantId);
                var logs = Captured.ToList();
                var home = await GateDrive.GetAsync(client, null, Host, "/", _output, "after delete");
                                var installerShown = home.Body.Contains("Installer", StringComparison.OrdinalIgnoreCase);
                var noAliasAlert = home.Body.Contains("No Matching Alias Exists", StringComparison.OrdinalIgnoreCase);

                _output.WriteLine($"[observed] DELETE /api/Alias/{target.AliasId} -> {(int)status} {status}");
                _output.WriteLine($"[observed] alias row still present: {aliasAfter != null}");
                _output.WriteLine($"[observed] master tenant row still present: {tenantAfter != null}");
                _output.WriteLine($"[observed] GET / -> {(int)home.Status}; body mentions the installer: {installerShown}; 'No Matching Alias Exists' alert: {noAliasAlert}");
                foreach (var line in logs.Where(IsDeleteAuditLine))
                {
                    _output.WriteLine($"[observed] controller log: {line}");
                }

                Assert.Equal(HttpStatusCode.Forbidden, status);
                Assert.NotNull(aliasAfter);
                Assert.NotNull(tenantAfter);
                Assert.Contains(logs, line => line.StartsWith(RefusalMessage, StringComparison.Ordinal));
                Assert.DoesNotContain(logs, line => line.StartsWith("Tenant Deleted", StringComparison.Ordinal));
                Assert.DoesNotContain(logs, line => line.StartsWith("Alias Deleted", StringComparison.Ordinal));
                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.False(installerShown, "The installer was shown after the refused delete.");
                Assert.False(noAliasAlert, "The host answered 'Site Not Configured Correctly - No Matching Alias Exists' after the refused delete.");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_non_master_tenants_last_alias_still_deletes_the_tenant()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await InstallRecordingAsync(scratch);
            try
            {
                var master = await MasterTenantAsync(app);
                var hostAlias = await GateDrive.AliasAsync(app, Host);

                // A second tenant row with a single alias of its own. The delete path touches only the master
                // bookkeeping (alias row, tenant row), so it needs no database of its own.
                var second = await InScopeAsync(app, sp => sp.GetRequiredService<ITenantRepository>().AddTenantAsync(new Tenant
                {
                    Name = "BL100Second",
                    DBConnectionString = master.DBConnectionString,
                    DBType = master.DBType,
                    Version = master.Version
                }));
                var secondAlias = await InScopeAsync(app, sp => sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = "second.localhost",
                    TenantId = second.TenantId,
                    SiteId = hostAlias.SiteId,
                    IsDefault = true
                }));
                _output.WriteLine($"[before] second tenant {second.TenantId}, alias {secondAlias.AliasId}:{secondAlias.Name}");

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var status = await DeleteAliasAsync(app, client, cookies, hostUserId, secondAlias.AliasId);
                var logs = Captured.ToList();
                _output.WriteLine($"[observed] DELETE /api/Alias/{secondAlias.AliasId} -> {(int)status}");
                foreach (var line in logs.Where(IsDeleteAuditLine))
                {
                    _output.WriteLine($"[observed] controller log: {line}");
                }

                Assert.Equal(HttpStatusCode.OK, status);
                Assert.DoesNotContain(await AliasesAsync(app), item => item.AliasId == secondAlias.AliasId);
                Assert.DoesNotContain(await TenantsAsync(app), item => item.TenantId == second.TenantId);
                Assert.Contains(await TenantsAsync(app), item => item.TenantId == master.TenantId);
                Assert.Contains(logs, line => line == $"Tenant Deleted {second.TenantId}");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Deleting_one_of_two_master_aliases_is_allowed()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await InstallRecordingAsync(scratch);
            try
            {
                var master = await MasterTenantAsync(app);
                var hostAlias = await GateDrive.AliasAsync(app, Host);
                var extra = await InScopeAsync(app, sp => sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = "alt.localhost",
                    TenantId = master.TenantId,
                    SiteId = hostAlias.SiteId,
                    IsDefault = false
                }));

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var status = await DeleteAliasAsync(app, client, cookies, hostUserId, extra.AliasId);
                var logs = Captured.ToList();
                _output.WriteLine($"[observed] DELETE /api/Alias/{extra.AliasId} (one of two master aliases) -> {(int)status}");
                foreach (var line in logs.Where(IsDeleteAuditLine))
                {
                    _output.WriteLine($"[observed] controller log: {line}");
                }

                Assert.Equal(HttpStatusCode.OK, status);
                var aliases = (await AliasesAsync(app)).ToList();
                Assert.DoesNotContain(aliases, item => item.AliasId == extra.AliasId);
                Assert.Contains(aliases, item => item.AliasId == hostAlias.AliasId);
                Assert.Contains(await TenantsAsync(app), item => item.TenantId == master.TenantId);
                Assert.Contains(logs, line => line == $"Alias Deleted {extra.AliasId}");
                Assert.DoesNotContain(logs, line => line.StartsWith("Tenant Deleted", StringComparison.Ordinal));
                Assert.DoesNotContain(logs, line => line.StartsWith(RefusalMessage, StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// The controller logs <c>Tenant Deleted</c> only when the tenant row is actually gone. Pinned with a tenant
        /// repository that silently refuses the delete (the shape <c>TenantRepository</c> has for the Master) on a
        /// NON-master tenant, so the guard does not intervene and only the log branch is under test.
        /// </summary>
        [Fact]
        public async Task The_delete_log_line_matches_the_outcome()
        {
            var aliases = new InMemoryAliasRepository(new Alias { AliasId = 7, Name = "gone.localhost", TenantId = 2, SiteId = 1 });

            // 1. the repository refuses: no 'Tenant Deleted'
            var refusing = new InMemoryTenantRepository(refuseDeletes: true,
                new Tenant { TenantId = 1, Name = TenantNames.Master }, new Tenant { TenantId = 2, Name = "Other" });
            var log = new RecordingLogManager();
            await NewController(aliases, refusing, log).Delete(7);
            _output.WriteLine("[refusing repository] " + string.Join(" | ", log.Messages));
            Assert.Contains(log.Messages, line => line.StartsWith("Alias Deleted", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Messages, line => line.StartsWith("Tenant Deleted", StringComparison.Ordinal));
            Assert.Contains(refusing.Tenants, item => item.TenantId == 2);

            // 2. the repository deletes: 'Tenant Deleted'
            aliases = new InMemoryAliasRepository(new Alias { AliasId = 8, Name = "gone2.localhost", TenantId = 2, SiteId = 1 });
            var deleting = new InMemoryTenantRepository(refuseDeletes: false,
                new Tenant { TenantId = 1, Name = TenantNames.Master }, new Tenant { TenantId = 2, Name = "Other" });
            log = new RecordingLogManager();
            await NewController(aliases, deleting, log).Delete(8);
            _output.WriteLine("[deleting repository] " + string.Join(" | ", log.Messages));
            Assert.Contains(log.Messages, line => line.StartsWith("Tenant Deleted", StringComparison.Ordinal));
            Assert.DoesNotContain(deleting.Tenants, item => item.TenantId == 2);
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static bool IsDeleteAuditLine(string line) =>
            line.StartsWith("Alias Deleted", StringComparison.Ordinal)
            || line.StartsWith("Tenant ", StringComparison.Ordinal)
            || line.StartsWith(RefusalMessage, StringComparison.Ordinal)
            || line.StartsWith("Unauthorized Alias Delete", StringComparison.Ordinal);

        private static AliasController NewController(IAliasRepository aliases, ITenantRepository tenants, ILogManager log) =>
            new AliasController(aliases, tenants, new SyncManager(), log, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

        private static async Task<T> InScopeAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        private static async Task InScopeAsync(WebApplication app, Func<IServiceProvider, Task> work)
        {
            using var scope = app.Services.CreateScope();
            await work(scope.ServiceProvider);
        }

        private static Task<IEnumerable<Alias>> AliasesAsync(WebApplication app) =>
            InScopeAsync(app, sp => sp.GetRequiredService<IAliasRepository>().GetAliasesAsync());

        private static Task<IEnumerable<Tenant>> TenantsAsync(WebApplication app) =>
            InScopeAsync(app, sp => sp.GetRequiredService<ITenantRepository>().GetTenantsAsync());

        private static async Task<Tenant> MasterTenantAsync(WebApplication app) =>
            (await TenantsAsync(app)).Single(item => item.Name.Equals(TenantNames.Master, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Every message the framework's <see cref="ILogManager"/> was handed, formatted, captured at the call - NOT
        /// read back from the site's Log table.
        /// </summary>
        /// <remarks>
        /// The first version of this suite read the Log table after waiting for the log drain to catch up, and it was
        /// wrong on SQLite: the drain's write collided with the request's own master-store write
        /// (<c>SQLite Error 5: 'database is locked'</c>, captured in the host log) and the drain swallows a failed
        /// write, so <c>Alias Deleted</c> lines were silently missing from the table while the controller had logged
        /// them - the single-writer class of SQLite. What the controller SAYS is what this suite is about, so it is
        /// captured where it is said. The collection is serial, so a static sink is not shared across live tests.
        /// </remarks>
        private static readonly ConcurrentQueue<string> Captured = new();

        private async Task<WebApplication> InstallRecordingAsync(ScratchGateInstall scratch)
        {
            Captured.Clear();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Host.UseServiceProviderFactory(new RecordingLogServiceProviderFactory());
                });

            var installTask = Task.Run(() => Program.InstallAsync(app));
            if (await Task.WhenAny(installTask, Task.Delay(GateDrive.InstallTimeout)) != installTask)
            {
                throw new TimeoutException("The SQLite installer did not finish in time.");
            }
            var install = await installTask;
            Assert.True(install.Success, $"Installer failed: {install.Message}");

            await app.StartAsync();
            Captured.Clear();
            return app;
        }

        private static string Format(string message, object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return message;
            }
            var brace = message.IndexOf('{');
            return (brace < 0 ? message : message.Substring(0, brace)) + string.Join(" ", args);
        }

        /// <summary>Replaces every <see cref="ILogManager"/> registration with a capturing decorator over the real one.</summary>
        private sealed class RecordingLogServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
        {
            public IServiceCollection CreateBuilder(IServiceCollection services) => services;

            public IServiceProvider CreateServiceProvider(IServiceCollection services)
            {
                var originals = services.Where(item => item.ServiceType == typeof(ILogManager)).ToList();
                Assert.NotEmpty(originals);
                foreach (var original in originals)
                {
                    services.Remove(original);
                }
                var inner = originals.Last();
                services.Add(new ServiceDescriptor(typeof(ILogManager), sp =>
                {
                    var real = inner.ImplementationType != null
                        ? (ILogManager)ActivatorUtilities.CreateInstance(sp, inner.ImplementationType)
                        : (ILogManager)inner.ImplementationFactory!(sp);
                    return new CapturingLogManager(real);
                }, inner.Lifetime));
                // The Development host's own defaults, so the swap does not loosen what the real host validates.
                return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            }
        }

        private sealed class CapturingLogManager : ILogManager
        {
            private readonly ILogManager _inner;

            public CapturingLogManager(ILogManager inner) => _inner = inner;

            public void Log(LogLevel level, object @class, LogFunction function, string message, params object[] args)
            { Captured.Enqueue(Format(message, args)); _inner.Log(level, @class, function, message, args); }
            public void Log(LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
            { Captured.Enqueue(Format(message, args)); _inner.Log(level, @class, function, exception, message, args); }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, string message, params object[] args)
            { Captured.Enqueue(Format(message, args)); _inner.Log(siteId, level, @class, function, message, args); }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
            { Captured.Enqueue(Format(message, args)); _inner.Log(siteId, level, @class, function, exception, message, args); }
            public void Log(Models.Log log)
            { Captured.Enqueue(log.Message); _inner.Log(log); }
        }
        private async Task<HttpStatusCode> DeleteAliasAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, int aliasId)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), $"/api/Alias/{aliasId}");
            var request = new HttpRequestMessage(HttpMethod.Delete, uri);
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
                _output.WriteLine($"[delete] body: {GateDrive.Truncate(await response.Content.ReadAsStringAsync())}");
            }
            return response.StatusCode;
        }

        private sealed class RecordingLogManager : ILogManager
        {
            public List<string> Messages { get; } = new();

            private void Record(string message, object[] args) =>
                Messages.Add(Format(message, args));

            public void Log(LogLevel level, object @class, LogFunction function, string message, params object[] args) => Record(message, args);
            public void Log(LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) => Record(message, args);
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, string message, params object[] args) => Record(message, args);
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) => Record(message, args);
            public void Log(Models.Log log) => Messages.Add(log.Message);
        }

        private sealed class InMemoryAliasRepository : IAliasRepository
        {
            private readonly List<Alias> _aliases;

            public InMemoryAliasRepository(params Alias[] aliases) => _aliases = aliases.ToList();

            public Task<IEnumerable<Alias>> GetAliasesAsync() => Task.FromResult<IEnumerable<Alias>>(_aliases.ToList());
            public Task<Alias> AddAliasAsync(Alias alias) { _aliases.Add(alias); return Task.FromResult(alias); }
            public Task<Alias> UpdateAliasAsync(Alias alias) => Task.FromResult(alias);
            public Task<Alias> GetAliasAsync(int aliasId) => Task.FromResult(_aliases.FirstOrDefault(item => item.AliasId == aliasId)!);
            public Task<Alias> GetAliasAsync(int aliasId, bool tracking) => GetAliasAsync(aliasId);
            public Task<Alias> GetAliasAsync(string url) => Task.FromResult(_aliases.FirstOrDefault(item => item.Name == url)!);
            public Task DeleteAliasAsync(int aliasId) { _aliases.RemoveAll(item => item.AliasId == aliasId); return Task.CompletedTask; }
        }

        private sealed class InMemoryTenantRepository : ITenantRepository
        {
            private readonly bool _refuseDeletes;

            public InMemoryTenantRepository(bool refuseDeletes, params Tenant[] tenants)
            {
                _refuseDeletes = refuseDeletes;
                Tenants = tenants.ToList();
            }

            public List<Tenant> Tenants { get; }

            public Task<IEnumerable<Tenant>> GetTenantsAsync() => Task.FromResult<IEnumerable<Tenant>>(Tenants.ToList());
            public Task<Tenant> AddTenantAsync(Tenant tenant) { Tenants.Add(tenant); return Task.FromResult(tenant); }
            public Task<Tenant> UpdateTenantAsync(Tenant tenant) => Task.FromResult(tenant);
            public Task<Tenant> GetTenantAsync(int tenantId) => Task.FromResult(Tenants.FirstOrDefault(item => item.TenantId == tenantId)!);

            public Task DeleteTenantAsync(int tenantId)
            {
                if (!_refuseDeletes)
                {
                    Tenants.RemoveAll(item => item.TenantId == tenantId);
                }
                return Task.CompletedTask;
            }
        }
    }
}