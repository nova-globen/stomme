#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The headless, stored-row permission evaluator against the request path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A queued export is rendered by a job, as its requester, with no request and no claims. The evaluator that
    /// decides for that absent user is NEW logic, and new authorisation logic is exactly where a quiet divergence
    /// becomes a hole. So every case here is put to BOTH paths - the real <see cref="IUserPermissions"/> over a
    /// principal built the way sign-in builds one, and <see cref="IStoredRowPermissionEvaluator"/> - on a really
    /// installed site, and the two must agree on all of them except two named cases: a role that exists only
    /// as a CLAIM, and a role REVOKED after the cookie was issued. In both the request path says yes and the
    /// headless path says no - the stricter answer, and the right one for a job.
    /// </para>
    /// <para>Runs on SQLite, PostgreSQL and RavenDB: the rows and the role hydration differ per provider.</para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class ExportHeadlessAuthorizationTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public ExportHeadlessAuthorizationTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("sqlite")]
        [InlineData("postgres")]
        [InlineData("raven")]
        public async Task The_two_paths_agree_everywhere_except_the_two_documented_divergences(string provider)
        {
            IGateStore store = provider switch
            {
                "sqlite" => new SqliteGateStore(),
                "postgres" => new PostgresGateStore(),
                _ => new RavenGateStore(),
            };

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var editors = await GateDrive.EnsureRoleAsync(app, Host, "Export Editors");
                var banned = await GateDrive.EnsureRoleAsync(app, Host, "Export Banned");
                var ghost = await GateDrive.EnsureRoleAsync(app, Host, "Export Ghost");
                var lapsed = await GateDrive.EnsureRoleAsync(app, Host, "Export Lapsed");
                var revoked = await GateDrive.EnsureRoleAsync(app, Host, "Export Revoked");

                var editor = await GateDrive.AddUserAsync(app, Host, "exp-editor", "Ed1tor!Pass$2026", "exp-editor@gate.test", "Export Editors");
                var outsider = await GateDrive.AddUserAsync(app, Host, "exp-outsider", "0utsider!Pass$2026", "exp-outsider@gate.test");
                var both = await GateDrive.AddUserAsync(app, Host, "exp-both", "B0th!Pass$2026", "exp-both@gate.test", "Export Editors", "Export Banned");
                var formerly = await GateDrive.AddUserAsync(app, Host, "exp-formerly", "F0rmer!Pass$2026", "exp-formerly@gate.test", "Export Revoked");
                var expired = await GateDrive.AddUserAsync(app, Host, "exp-expired", "Exp1red!Pass$2026", "exp-expired@gate.test");
                var doomed = await GateDrive.AddUserAsync(app, Host, "exp-doomed", "D00med!Pass$2026", "exp-doomed@gate.test", "Export Editors");
                var hostUserId = await GateDrive.UserIdAsync(app, Host, "host");

                // The principals are built NOW - "the cookie is issued" - before anything below is revoked.
                var editorPrincipal = await PrincipalAsync(app, editor.UserId);
                var outsiderPrincipal = await PrincipalAsync(app, outsider.UserId);
                var bothPrincipal = await PrincipalAsync(app, both.UserId);
                var formerlyPrincipal = await PrincipalAsync(app, formerly.UserId);
                var hostPrincipal = await PrincipalAsync(app, hostUserId);
                var ghostPrincipal = await PrincipalAsync(app, outsider.UserId, claimOnlyRole: "Export Ghost");

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    Task Row(string operation, Role? role, int? userId, bool authorized) => permissions.AddPermissionAsync(new Permission
                    {
                        SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                        PermissionName = operation, RoleId = role?.RoleId, UserId = userId, IsAuthorized = authorized,
                    });

                    await Row("byrole", editors, null, true);
                    await Row("byuser", null, outsider.UserId, true);
                    await Row("denyuser", editors, null, true);
                    await Row("denyuser", null, editor.UserId, false);
                    await Row("denyrole", editors, null, true);
                    await Row("denyrole", banned, null, false);
                    await Row("ghost", ghost, null, true);
                    await Row("lapsed", lapsed, null, true);
                    await Row("revoked", revoked, null, true);

                    var administrators = (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(siteId)).Single(item => item.Name == RoleNames.Admin);
                    await Row("adminsonly", administrators, null, true);

                    // A membership that ended yesterday.
                    var userRoles = sp.GetRequiredService<IUserRoleRepository>();
                    await userRoles.AddUserRoleAsync(new UserRole
                    {
                        UserId = expired.UserId, RoleId = lapsed.RoleId,
                        EffectiveDate = DateTime.UtcNow.AddDays(-30), ExpiryDate = DateTime.UtcNow.AddDays(-1),
                    });

                    // The revocation, AFTER the principal was built.
                    var membership = (await userRoles.GetUserRolesAsync(formerly.UserId, siteId)).Single(item => item.RoleId == revoked.RoleId);
                    await userRoles.DeleteUserRoleAsync(membership.UserRoleId);
                    return true;
                });

                var expiredPrincipal = await PrincipalAsync(app, expired.UserId, claimOnlyRole: "Export Lapsed");

                var cases = new List<Case>
                {
                    new("no rows at all for the key", editor.UserId, editorPrincipal, "export:nothing", Request: false, Headless: false),
                    new("granted to a role the user holds", editor.UserId, editorPrincipal, "export:byrole", true, true),
                    new("granted to a role the user does NOT hold", outsider.UserId, outsiderPrincipal, "export:byrole", false, false),
                    new("granted to the user directly", outsider.UserId, outsiderPrincipal, "export:byuser", true, true),
                    new("a user DENY beats a role grant", editor.UserId, editorPrincipal, "export:denyuser", false, false),
                    new("a role DENY beats another role's grant", both.UserId, bothPrincipal, "export:denyrole", false, false),
                    new("the same key for a user without the denied role", editor.UserId, editorPrincipal, "export:denyrole", true, true),
                    new("an EXPIRED membership (claimed, and stored as expired)", expired.UserId, expiredPrincipal, "export:lapsed", false, false),
                    new("a malformed key", editor.UserId, editorPrincipal, "export:by:role", false, false),
                    new("a key with no operation", editor.UserId, editorPrincipal, "export", false, false),
                    new("upper-case key, lower-case rows", editor.UserId, editorPrincipal, "EXPORT:ByRole", true, true),
                    // A Host user is an administrator BY RULE (sign-in implies it from the stored Host role), so a key
                    // granted to Administrators must answer the same way with nobody present.
                    new("granted to Administrators, asked for a HOST user", hostUserId, hostPrincipal, "export:adminsonly", true, true),
                    new("granted to Administrators, asked for an ordinary user", editor.UserId, editorPrincipal, "export:adminsonly", false, false),

                    // ---- the two documented divergences: request path yes, headless no ----
                    new("DIVERGENCE 1 - a role that exists only as a CLAIM", outsider.UserId, ghostPrincipal, "export:ghost", true, false),
                    new("DIVERGENCE 2 - a role REVOKED after the cookie was issued", formerly.UserId, formerlyPrincipal, "export:revoked", true, false),
                };

                var surprises = new List<string>();
                foreach (var c in cases)
                {
                    var (request, headless) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    {
                        var onRequest = PermissionRowAccessGuard.TrySplit(c.Key, out var resource, out var operation)
                            && await sp.GetRequiredService<IUserPermissions>().IsAuthorizedAsync(c.Principal, siteId, resource, PermissionRowAccessGuard.KeyEntityId, operation);
                        var offRequest = await sp.GetRequiredService<IStoredRowPermissionEvaluator>().IsAuthorizedAsync(siteId, c.UserId, c.Key);
                        return (onRequest, offRequest);
                    });

                    _output.WriteLine($"[{provider}] {c.Name}: request={request} headless={headless}");
                    if (request != c.Request || headless != c.Headless)
                    {
                        surprises.Add($"{c.Name}: expected request={c.Request}/headless={c.Headless}, got request={request}/headless={headless}");
                    }
                }

                Assert.True(surprises.Count == 0, "The paths did not answer as documented:\n  " + string.Join("\n  ", surprises));

                // Exactly two divergences, both in the STRICTER direction - never a case the job allows and a request refuses.
                Assert.Equal(2, cases.Count(c => c.Request != c.Headless));
                Assert.DoesNotContain(cases, c => c.Headless && !c.Request);

                // ---- system:admin: a stored Host role, not a claim ----
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var evaluator = sp.GetRequiredService<IStoredRowPermissionEvaluator>();
                    Assert.True(hostPrincipal.IsInRole(RoleNames.Host), "the installed host account carries no Host claim - the comparison below would be vacuous");
                    Assert.True(await evaluator.IsAuthorizedAsync(siteId, hostUserId, PermissionRowAccessGuard.SystemAdminKey));
                    Assert.False(await evaluator.IsAuthorizedAsync(siteId, editor.UserId, PermissionRowAccessGuard.SystemAdminKey));

                    // ---- nobody, nowhere, nothing ----
                    Assert.False(await evaluator.IsAuthorizedAsync(siteId, 0, "export:byrole"));
                    Assert.False(await evaluator.IsAuthorizedAsync(siteId, 987_654, "export:byrole"));
                    Assert.False(await evaluator.IsAuthorizedAsync(siteId + 1000, editor.UserId, "export:byrole"));
                    Assert.False(await evaluator.IsAuthorizedAsync(0, editor.UserId, "export:byrole"));
                    Assert.False(await evaluator.IsAuthorizedAsync(siteId, editor.UserId, " "));
                    return true;
                });

                // ---- a DELETED account holds nothing, whatever rows still name it ----
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var evaluator = sp.GetRequiredService<IStoredRowPermissionEvaluator>();
                    Assert.True(await evaluator.IsAuthorizedAsync(siteId, doomed.UserId, "export:byrole"));

                    var users = sp.GetRequiredService<IUserRepository>();
                    var account = await users.GetUserAsync(doomed.UserId, false);
                    account.IsDeleted = true;
                    await users.UpdateUserAsync(account);
                    return true;
                });

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    Assert.False(await sp.GetRequiredService<IStoredRowPermissionEvaluator>().IsAuthorizedAsync(siteId, doomed.UserId, "export:byrole"));
                    return true;
                });

                // ---- the job authorizer: BOTH keys, from the JOB ROW ----
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    await permissions.AddPermissionAsync(new Permission
                    {
                        SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                        PermissionName = "create", RoleId = editors.RoleId, IsAuthorized = true,
                    });
                    return true;
                });

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var authorizer = new StoredRowExportJobAuthorizer(sp.GetRequiredService<IStoredRowPermissionEvaluator>());
                    ExportJobAggregate JobFor(int userId) => ExportJobAggregate.Request(
                        Guid.NewGuid(), TenantId.Default, siteId, userId, "test.rows", ExportFormat.Csv, null, "{}", DateTime.UtcNow);

                    // create + the source's data key: allowed.
                    Assert.True(await authorizer.IsAuthorizedAsync(JobFor(editor.UserId), new KeyedSource("export:byrole")));
                    // create, but NOT the data key.
                    Assert.False(await authorizer.IsAuthorizedAsync(JobFor(editor.UserId), new KeyedSource("export:byuser")));
                    // the data key, but NOT create.
                    Assert.False(await authorizer.IsAuthorizedAsync(JobFor(outsider.UserId), new KeyedSource("export:byuser")));
                    // a source that names no permission is never "no requirement".
                    Assert.False(await authorizer.IsAuthorizedAsync(JobFor(editor.UserId), new KeyedSource(" ")));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A principal built the way sign-in builds one, optionally carrying a role nothing stored backs.</summary>
        private static Task<ClaimsPrincipal> PrincipalAsync(WebApplication app, int userId, string? claimOnlyRole = null) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var user = await sp.GetRequiredService<IUserRepository>().GetUserAsync(userId);
                // The repository row carries no Identity security stamp; sign-in reads it from the Identity store.
                user.SecurityStamp ??= "gate";
                var userRoles = (await sp.GetRequiredService<IUserRoleRepository>().GetUserRolesAsync(userId, alias.SiteId)).ToList();
                var identity = UserSecurity.CreateClaimsIdentity(alias, user, userRoles);
                if (claimOnlyRole is not null && !identity.HasClaim(ClaimTypes.Role, claimOnlyRole))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, claimOnlyRole));
                }

                return new ClaimsPrincipal(identity);
            });

        private sealed record Case(string Name, int UserId, ClaimsPrincipal Principal, string Key, bool Request, bool Headless);

        private sealed class KeyedSource(string permission) : IExportSource
        {
            public string Key => "test.rows";

            public string RequiredPermission => permission;

            public TableCardinality TableCardinality => TableCardinality.Single;

            public string? Validate(ExportSourceContext context) => null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, System.Threading.CancellationToken ct = default) =>
                throw new NotSupportedException();
        }
    }
}
