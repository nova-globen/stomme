#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-120 driven on an installed host: real sign-ins through the login form and a real role grant through the API each
    /// write an identity audit line, and the ledger is read back through its Administrators-only API and page.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class IdentityAuditDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public IdentityAuditDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Sign_ins_and_a_role_grant_are_recorded_and_only_an_Administrator_reads_them()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // A wrong password, an account that does not exist, and a real sign-in - through the login FORM.
                using (var wrong = GateDrive.NewClient(app, Host))
                {
                    await LoginFormAsync(app, wrong, GateDrive.HostUsername, "not-the-password");
                }

                using (var unknown = GateDrive.NewClient(app, Host))
                {
                    await LoginFormAsync(app, unknown, "Hunter2-typed-into-the-username-field", "whatever");
                }

                using var admin = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, admin, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // A role grant through the API, by the host, to a new user.
                var member = await GateDrive.AddUserAsync(app, Host, "bl120-member", "Bl120!Member$2026", "bl120-member@gate.test");
                var role = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId)).Single(r => r.Name == RoleNames.Admin));
                var granted = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, hostId, HttpMethod.Post, "/api/UserRole",
                    new UserRole { UserId = member.UserId, RoleId = role.RoleId }, _output, "grant Administrators");
                Assert.Equal(HttpStatusCode.OK, granted.Status);

                // ---- read back through the API, as an Administrator ------------------------------------------------------
                var read = await GateDrive.GetAsync(admin, adminCookies, Host, "/api/IdentityAudit?days=1", _output, "read the ledger");
                Assert.Equal(HttpStatusCode.OK, read.Status);
                var entries = JsonDocument.Parse(read.Body).RootElement.EnumerateArray().Select(e => new
                {
                    Event = e.GetProperty("event").GetInt32(),
                    Subject = e.GetProperty("subjectUserId").ValueKind == JsonValueKind.Number ? e.GetProperty("subjectUserId").GetInt32() : (int?)null,
                    Name = e.GetProperty("subjectName").GetString(),
                    Actor = e.GetProperty("actorUserId").ValueKind == JsonValueKind.Number ? e.GetProperty("actorUserId").GetInt32() : (int?)null,
                    Detail = e.GetProperty("detail").GetString(),
                }).ToList();

                Assert.Contains(entries, e => e.Event == (int)Models.IdentityAudit.IdentityAuditEvent.SignInFailed && e.Subject == hostId && e.Name == GateDrive.HostUsername);
                Assert.Contains(entries, e => e.Event == (int)Models.IdentityAudit.IdentityAuditEvent.SignInFailed && e.Subject == null && e.Name == "" && e.Detail == "unknown account");
                Assert.Contains(entries, e => e.Event == (int)Models.IdentityAudit.IdentityAuditEvent.SignIn && e.Subject == hostId);
                Assert.Contains(entries, e => e.Event == (int)Models.IdentityAudit.IdentityAuditEvent.RoleGranted && e.Subject == member.UserId && e.Actor == hostId && e.Detail == RoleNames.Admin);

                // The text typed for the unknown account is nowhere in the ledger.
                Assert.DoesNotContain("Hunter2", read.Body, System.StringComparison.Ordinal);

                // ---- nobody else reads it ----------------------------------------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, "bl120-plain", "Bl120!Plain$2026", "bl120-plain@gate.test");
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, "bl120-plain", "Bl120!Plain$2026", _output);
                var plain = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/IdentityAudit?days=1", _output, "plain user reads the ledger");
                Assert.True(plain.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a plain user got {(int)plain.Status}");

                using var anonymous = GateDrive.NewClient(app, Host);
                var anon = await GateDrive.GetAsync(anonymous, null, Host, "/api/IdentityAudit?days=1", _output, "anonymous reads the ledger");
                Assert.True(anon.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"anonymous got {(int)anon.Status}");

                // ---- and the admin page is placed and served ---------------------------------------------------------------
                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/identity-audit", alias.SiteId));
                Assert.NotNull(page);
                var served = await GateDrive.GetAsync(admin, adminCookies, Host, "/admin/identity-audit", _output, "the admin page");
                Assert.Equal(HttpStatusCode.OK, served.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_ledger_that_cannot_be_written_never_breaks_the_sign_in_it_audits()
        {
            var recorder = new Stomme.Modules.IdentityAudit.Application.IdentityAuditRecorder(
                ThrowingAudit.Create(),
                new FixedTenant(),
                new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = Host } },
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Stomme.Modules.IdentityAudit.Application.IdentityAuditRecorder>.Instance);

            // The store throws; the recorder logs and returns - the caller's sign-in carries on.
            await recorder.RecordAsync(Models.IdentityAudit.IdentityAuditEvent.SignIn, 1, "host");
        }

        private sealed class FixedTenant : Stomme.Tenancy.Abstractions.ITenantContext
        {
            public Stomme.Tenancy.Domain.TenantId? CurrentTenantId => Stomme.Tenancy.Domain.TenantId.Default;
            public bool IsSystemScope => false;
        }

        public class ThrowingAudit : System.Reflection.DispatchProxy
        {
            public static Stomme.IdentityAudit.Persistence.IIdentityAuditRepository Create() =>
                System.Reflection.DispatchProxy.Create<Stomme.IdentityAudit.Persistence.IIdentityAuditRepository, ThrowingAudit>();

            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
                throw new System.InvalidOperationException("the store is down");
        }

        private static async Task LoginFormAsync(WebApplication app, HttpClient client, string username, string password)
        {
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app);
            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username, ["password"] = password, ["remember"] = "false", ["returnurl"] = "/", ["__RequestVerificationToken"] = token,
                }),
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
            await client.SendAsync(request);
        }
    }
}
