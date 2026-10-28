using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-102: Edit on ONE module, page or folder must not be enough to write a SITE-WIDE permission row for a
    /// different entity - the privilege escalation the Module/Page/Folder write paths carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, read at the sink.</b> <c>PermissionRepository.UpdatePermissionsAsync</c> defaults an
    /// empty EntityName to the entity being written and stamps <c>EntityId = -1</c> on ANY row whose EntityName
    /// differs - so a foreign row in a Module/Page/Folder <c>PermissionList</c> lands as a site-wide row.
    /// D99's correction measured what one such row does: a <c>(User, -1, Write)</c> row for
    /// <see cref="RoleNames.Everyone"/> turns the row-less, role-decided Host-only policy on
    /// <c>UserController.Delete</c> row-decided, and the DELETE answered 200 ANONYMOUSLY.
    /// </para>
    /// <para>
    /// <b>The principal.</b> A REGISTERED user holding a USER-level Edit row on each target and nothing else -
    /// the least a caller needs to reach every one of the six actions. Each attempt is INDEPENDENT (a fresh
    /// victim, any escalated row removed afterwards) and every failure is COLLECTED before asserting, so one
    /// run names every open path rather than the first.
    /// </para>
    /// <para>
    /// <b>No partial write.</b> A guard that only threw inside the repository would still let the entity write
    /// land first (every repository writes the entity BEFORE its permissions, and <c>PageController.Put</c>
    /// writes the page before even calling it) and answer an empty 500. The PUT attempts therefore also pin the
    /// target's <c>ModifiedOn</c>, and the POST attempts pin that no child entity was created.
    /// </para>
    /// <para>
    /// SQLite and PostgreSQL here, RavenDB in <see cref="RavenEntityPermissionWriteValidationTests"/>; each FAILS
    /// rather than skips when its store is unreachable (D46).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class EntityPermissionWriteValidationTests
    {
        public const string Host = "localhost";

        private const string EditorUsername = "entityeditor";
        private const string EditorPassword = "Ed1tor!Entity$2026";
        private const string EditorEmail = "entityeditor@gate.test";
        private const string VictimPassword = "V1ctim!Entity$2026";

        private readonly ITestOutputHelper _output;

        public EntityPermissionWriteValidationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new TheoryData<string>
        {
            SqliteGateStore.StoreName,
            PostgresGateStore.StoreName
        };

        [Theory]
        [MemberData(nameof(Stores))]
        public Task A_foreign_permission_row_on_a_module_page_or_folder_write_is_rejected(string storeName) =>
            RunAsync(LiftedRequiresGateTests.ResolveStore(storeName), _output);

        /// <summary>
        /// The repository guard on its own (defence in depth under the controller 400): a foreign row is refused
        /// and NOTHING is written, and the lifted-key opt-in never admits a FRAMEWORK entity.
        /// </summary>
        [Fact]
        public async Task The_permission_repository_refuses_a_foreign_row_and_writes_nothing()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var target = await TargetsAsync(app);

                var thrown = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    var rows = new List<Permission> { new Permission(alias.SiteId, EntityNames.User, PermissionNames.Write, RoleNames.Everyone, null, true) };
                    try
                    {
                        await permissions.UpdatePermissionsAsync(alias.SiteId, EntityNames.Module, target.ModuleId, rows);
                        return (Exception?)null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                });
                _output.WriteLine($"[repository] foreign User:Write row -> {thrown?.GetType().Name ?? "no exception"}: {thrown?.Message}");
                var userWrite = await GateDrive.RowsAsync(app, Host, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[repository] (User,Write) rows = {GateDrive.Describe(userWrite)}");
                Assert.IsType<InvalidOperationException>(thrown);
                Assert.Contains("User:Write", thrown!.Message);
                Assert.Empty(userWrite);

                var thrownWithFlag = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    var rows = new List<Permission> { new Permission(alias.SiteId, EntityNames.User, PermissionNames.Write, RoleNames.Everyone, null, true) };
                    try
                    {
                        await permissions.UpdatePermissionsAsync(alias.SiteId, EntityNames.ModuleDefinition, 1, rows, allowDeclaredKeyRows: true);
                        return (Exception?)null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                });
                _output.WriteLine($"[repository, allowDeclaredKeyRows] framework User:Write row -> {thrownWithFlag?.GetType().Name ?? "no exception"}: {thrownWithFlag?.Message}");
                userWrite = await GateDrive.RowsAsync(app, Host, EntityNames.User, PermissionNames.Write);
                Assert.IsType<InvalidOperationException>(thrownWithFlag);
                Assert.Empty(userWrite);

                // Non-vacuity: the flag DOES admit a lifted (non-framework) key row, so "throws" above is about
                // the entity, not about the flag being ignored.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var rows = new List<Permission> { new Permission(alias.SiteId, "bl102probe", PermissionKeys.KeyEntityId, "use", RoleNames.Admin, null, true) };
                    await sp.GetRequiredService<IPermissionRepository>().UpdatePermissionsAsync(alias.SiteId, EntityNames.ModuleDefinition, 1, rows, allowDeclaredKeyRows: true);
                    return true;
                });
                var probe = await GateDrive.RowsAsync(app, Host, "bl102probe", "use");
                _output.WriteLine($"[repository, allowDeclaredKeyRows] bl102probe:use rows = {GateDrive.Describe(probe)}");
                Assert.Contains(probe, row => row.EntityId == PermissionKeys.KeyEntityId && row.RoleName == RoleNames.Admin);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public Task A_host_save_of_the_users_module_instance_in_the_grid_submission_shape_writes_no_framework_row(string storeName) =>
            RunHostGridSaveAsync(LiftedRequiresGateTests.ResolveStore(storeName), _output);

        /// <summary>
        /// BL-102, the module Settings page's side: the SUBMISSION CONTRACT of the module-instance permission grid,
        /// driven as the HOST against the stock Users admin module instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Before the client fix, <c>Admin/Modules/Settings.razor</c> handed <c>PermissionGrid</c> the definition's
        /// FULL PermissionNames (<c>View,Edit,User:Write:Administrators,UserRole:Write:Administrators</c>) and the
        /// module's MERGED PermissionList (<c>PageModuleRepository</c> merges every ':' entry's site-wide rows in).
        /// The grid's host branch (<c>ValidatePermissions</c>) then adds a <c>Host Users</c> row for every column
        /// with no Administrators row - so a plain host save submitted <c>(User,-1,Write,Host Users)</c>, which,
        /// written, would flip the row-less role-decided User:Write policy row-decided and lock site
        /// administrators out of user management (the D99 correction). That OLD shape must now be refused (400,
        /// nothing written); the shape the FIXED page sends - own Module rows, host rows for View/Edit only - must
        /// save (200) and leave the framework policies row-less, with a site administrator still holding user
        /// management and still refused the Host-only DELETE.
        /// </para>
        /// <para>
        /// LIMIT, stated: this proves the submission CONTRACT the page is built to, reproduced here by
        /// <see cref="SimulateHostValidate"/>; it does not render the Blazor component.
        /// </para>
        /// </remarks>
        internal static async Task RunHostGridSaveAsync(IGateStore store, ITestOutputHelper output)
        {
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var usersPageModule = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var page = await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/users", alias.SiteId);
                    Assert.True(page != null, "The installed site has no 'admin/users' page.");
                    var pageModules = (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                        .Where(item => item.PageId == page!.PageId && !item.IsDeleted)
                        .ToList();
                    return pageModules.Single(item => item.Module.ModuleDefinitionName.Contains("Admin.Users"));
                });
                var definition = usersPageModule.Module.ModuleDefinition;
                Assert.True(definition != null, "The Users module instance carries no ModuleDefinition.");
                var definitionNames = definition!.PermissionNames;
                var merged = usersPageModule.Module.PermissionList ?? new List<Permission>();
                var moduleId = usersPageModule.ModuleId;
                output.WriteLine($"[{store.Name}] Users module {moduleId}: definition PermissionNames = '{definitionNames}'");
                output.WriteLine($"[{store.Name}] merged PermissionList = {GateDrive.Describe(merged)}");
                Assert.Contains($"{EntityNames.User}:{PermissionNames.Write}:", definitionNames);

                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var victim = await GateDrive.AddUserAsync(app, Host, "bl102gridvictim", VictimPassword, "bl102gridvictim@gate.test");
                var siteAdmin = await GateDrive.AddUserAsync(app, Host, "bl102siteadmin", EditorPassword, "bl102siteadmin@gate.test", RoleNames.Admin);
                var siteAdminRoles = await GateDrive.RolesAsync(app, Host, siteAdmin.UserId);
                Assert.Contains(RoleNames.Admin, siteAdminRoles);
                Assert.DoesNotContain(RoleNames.Host, siteAdminRoles);

                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);

                var failures = new List<string>();

                // ---- (1) the OLD client's submission: full names + merged list + host-branch rows --------------
                var oldShape = SimulateHostValidate(merged.Select(Clone).ToList(), definitionNames, siteId);
                output.WriteLine($"[{store.Name}] OLD submission = {GateDrive.Describe(oldShape)}");
                Assert.Contains(oldShape, row => row.EntityName == EntityNames.User && row.PermissionName == PermissionNames.Write && row.RoleName == RoleNames.Host);
                Assert.Contains(oldShape, row => row.EntityName == EntityNames.UserRole && row.PermissionName == PermissionNames.Write && row.RoleName == RoleNames.Host);

                var modifiedBefore = await ModifiedOnAsync(app, EntityNames.Module, moduleId);
                var oldBody = await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(moduleId));
                oldBody.PageModuleId = usersPageModule.PageModuleId;
                oldBody.PermissionList = oldShape;
                var oldSent = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Put, $"/api/Module/{moduleId}", oldBody, output, $"{store.Name} host grid save (OLD shape)");
                if (oldSent.Status != HttpStatusCode.BadRequest)
                {
                    failures.Add($"OLD shape: expected 400, got {(int)oldSent.Status}");
                }

                var framework = await FrameworkWriteRowsAsync(app);
                output.WriteLine($"[{store.Name}] after OLD shape: framework Write rows = {GateDrive.Describe(framework)}");
                if (framework.Count != 0)
                {
                    failures.Add($"OLD shape: wrote framework rows {GateDrive.Describe(framework)}");
                }

                if (await ModifiedOnAsync(app, EntityNames.Module, moduleId) != modifiedBefore)
                {
                    failures.Add("OLD shape: PARTIAL WRITE - the module was updated before the refusal");
                }

                // ---- (2) NON-VACUITY: the FIXED client's submission: own Module rows, plain names only ---------
                var newShape = SimulateHostValidate(PermissionRowScope.OwnRows(merged.Select(Clone), EntityNames.Module), PermissionRowScope.PlainPermissionNames(definitionNames), siteId);
                output.WriteLine($"[{store.Name}] FIXED submission = {GateDrive.Describe(newShape)}");
                Assert.Empty(PermissionRowScope.ForeignRows(newShape, EntityNames.Module));

                var newBody = await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(moduleId));
                newBody.PageModuleId = usersPageModule.PageModuleId;
                newBody.PermissionList = newShape;
                var newSent = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Put, $"/api/Module/{moduleId}", newBody, output, $"{store.Name} host grid save (FIXED shape)");
                if (newSent.Status != HttpStatusCode.OK)
                {
                    failures.Add($"FIXED shape: expected 200, got {(int)newSent.Status}");
                }

                framework = await FrameworkWriteRowsAsync(app);
                output.WriteLine($"[{store.Name}] after FIXED shape: framework Write rows = {GateDrive.Describe(framework)}");
                if (framework.Count != 0)
                {
                    failures.Add($"FIXED shape: wrote framework rows {GateDrive.Describe(framework)}");
                }

                var moduleRows = (await GateDrive.RowsAsync(app, Host, EntityNames.Module, PermissionNames.View))
                    .Concat(await GateDrive.RowsAsync(app, Host, EntityNames.Module, PermissionNames.Edit))
                    .Where(row => row.EntityId == moduleId)
                    .ToList();
                output.WriteLine($"[{store.Name}] after FIXED shape: module {moduleId} rows = {GateDrive.Describe(moduleRows)}");
                if (!moduleRows.Any(row => row.PermissionName == PermissionNames.View) || !moduleRows.Any(row => row.PermissionName == PermissionNames.Edit))
                {
                    failures.Add($"FIXED shape: the module's own View/Edit rows were not kept {GateDrive.Describe(moduleRows)}");
                }

                // ---- (3) the lockout check in reverse, as a SITE administrator (Administrators, not Host) -------
                using var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, "bl102siteadmin", EditorPassword, output);

                var adminGet = await GateDrive.GetAsync(adminClient, adminCookies, Host, $"/api/User/{victim.UserId}?siteid={siteId}", output, $"{store.Name} site admin GET user");
                if (adminGet.Status != HttpStatusCode.OK || !adminGet.Body.Contains("bl102gridvictim@gate.test"))
                {
                    failures.Add($"site admin lost User:Write (private user fields hidden): {(int)adminGet.Status} {adminGet.Body}");
                }

                var adminDelete = await GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, siteAdmin.UserId, HttpMethod.Delete, $"/api/User/{victim.UserId}?siteid={siteId}", null, output, $"{store.Name} site admin DELETE user");
                if (adminDelete.Status != HttpStatusCode.Forbidden)
                {
                    failures.Add($"site admin DELETE /api/User/{victim.UserId}: expected 403 (Host-only), got {(int)adminDelete.Status}");
                }

                if (await InTenantRead(app, sp => sp.GetRequiredService<IUserRepository>().GetUserAsync(victim.UserId)) == null)
                {
                    failures.Add($"the victim user {victim.UserId} was DELETED");
                }

                foreach (var failure in failures)
                {
                    output.WriteLine($"[{store.Name}] FAILURE: {failure}");
                }

                Assert.True(failures.Count == 0, $"[{store.Name}] {failures.Count} failure(s): " + string.Join(" | ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// <c>PermissionGrid.ValidatePermissions</c>'s HOST branch, reproduced: drop Host Users rows, then add a
        /// Host Users row for every declared column (a ':' entry names its own entity) with no Administrators row.
        /// </summary>
        private static List<Permission> SimulateHostValidate(List<Permission> rows, string permissionNames, int siteId)
        {
            var names = string.IsNullOrEmpty(permissionNames)
                ? new List<string> { PermissionNames.View, PermissionNames.Edit }
                : permissionNames.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            rows.RemoveAll(row => row.RoleName == RoleNames.Host);
            foreach (var name in names)
            {
                var entityName = name.Contains(':') ? name.Split(':')[0] : EntityNames.Module;
                var permissionName = name.Contains(':') ? name.Split(':')[1] : name;
                if (!rows.Any(row => row.EntityName == entityName && row.PermissionName == permissionName && row.RoleName == RoleNames.Admin))
                {
                    rows.Add(new Permission(siteId, entityName, PermissionKeys.KeyEntityId, permissionName, RoleNames.Host, null, true));
                }
            }

            return rows;
        }

        private static Permission Clone(Permission row) => JsonSerializer.Deserialize<Permission>(JsonSerializer.Serialize(row))!;

        private static async Task<List<Permission>> FrameworkWriteRowsAsync(WebApplication app) =>
            (await GateDrive.RowsAsync(app, Host, EntityNames.User, PermissionNames.Write))
                .Concat(await GateDrive.RowsAsync(app, Host, EntityNames.UserRole, PermissionNames.Write))
                .ToList();
        internal sealed record Targets(int SiteId, int ModuleId, int PageId, int FolderId);

        private sealed record Attempt(string Label, string EntityName, bool IsPut, int TargetId, Func<Task<(HttpMethod Method, string Path, object Body)>> Build, Func<Task<string?>> NoChildCreated);

        internal static async Task RunAsync(IGateStore store, ITestOutputHelper output)
        {
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                var target = await TargetsAsync(app);
                var siteId = target.SiteId;
                output.WriteLine($"[{store.Name}] targets: site {siteId}, module {target.ModuleId}, page {target.PageId}, folder {target.FolderId}");

                // ---- (a) the principal: REGISTERED, not an administrator ------------------------------------
                var editor = await GateDrive.AddUserAsync(app, Host, EditorUsername, EditorPassword, EditorEmail);
                var editorRoles = await GateDrive.RolesAsync(app, Host, editor.UserId);
                Assert.Contains(RoleNames.Registered, editorRoles);
                Assert.DoesNotContain(RoleNames.Admin, editorRoles);
                Assert.DoesNotContain(RoleNames.Host, editorRoles);

                // ---- (c) Edit on each target, as a USER row --------------------------------------------------
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    await permissions.AddPermissionAsync(new Permission(siteId, EntityNames.Module, target.ModuleId, PermissionNames.Edit, null, editor.UserId, true));
                    await permissions.AddPermissionAsync(new Permission(siteId, EntityNames.Page, target.PageId, PermissionNames.Edit, null, editor.UserId, true));
                    await permissions.AddPermissionAsync(new Permission(siteId, EntityNames.Folder, target.FolderId, PermissionNames.Edit, null, editor.UserId, true));
                    return true;
                });

                using var editorClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var editorCookies = await GateDrive.LoginAsync(app, editorClient, Host, EditorUsername, EditorPassword, output);

                var before = await GateDrive.RowsAsync(app, Host, EntityNames.User, PermissionNames.Write);
                output.WriteLine($"[{store.Name}] (User,Write) rows before = {GateDrive.Describe(before)}");
                Assert.Empty(before);

                var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                var modulesBefore = 0;
                var attempts = new List<Attempt>
                {
                    new Attempt("Module PUT", EntityNames.Module, true, target.ModuleId,
                        async () =>
                        {
                            var module = await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(target.ModuleId));
                            module.PermissionList.Add(Foreign(siteId));
                            return (HttpMethod.Put, $"/api/Module/{target.ModuleId}", module);
                        },
                        () => Task.FromResult<string?>(null)),
                    new Attempt("Page PUT", EntityNames.Page, true, target.PageId,
                        async () =>
                        {
                            var page = await InTenantRead(app, sp => sp.GetRequiredService<IPageRepository>().GetPageAsync(target.PageId));
                            page.PermissionList.Add(Foreign(siteId));
                            return (HttpMethod.Put, $"/api/Page/{target.PageId}", page);
                        },
                        () => Task.FromResult<string?>(null)),
                    new Attempt("Folder PUT", EntityNames.Folder, true, target.FolderId,
                        async () =>
                        {
                            var folder = await InTenantRead(app, sp => sp.GetRequiredService<IFolderRepository>().GetFolderAsync(target.FolderId));
                            folder.PermissionList.Add(Foreign(siteId));
                            return (HttpMethod.Put, $"/api/Folder/{target.FolderId}", folder);
                        },
                        () => Task.FromResult<string?>(null)),
                    new Attempt("Module POST", EntityNames.Module, false, target.PageId,
                        async () =>
                        {
                            var source = await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(target.ModuleId));
                            modulesBefore = (await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModulesAsync(siteId))).Count();
                            var module = JsonSerializer.Deserialize<Module>(JsonSerializer.Serialize(source))!;
                            module.ModuleId = 0;
                            module.PageId = target.PageId;
                            module.AllPages = false;
                            module.PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                new Permission(PermissionNames.Edit, editor.UserId, true),
                                Foreign(siteId)
                            };
                            return (HttpMethod.Post, "/api/Module", module);
                        },
                        async () =>
                        {
                            var after = (await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModulesAsync(siteId))).Count();
                            return after == modulesBefore ? null : $"a module was created ({modulesBefore} -> {after})";
                        }),
                    new Attempt("Page POST", EntityNames.Page, false, target.PageId,
                        async () =>
                        {
                            var source = await InTenantRead(app, sp => sp.GetRequiredService<IPageRepository>().GetPageAsync(target.PageId));
                            var page = JsonSerializer.Deserialize<Page>(JsonSerializer.Serialize(source))!;
                            page.PageId = 0;
                            page.ParentId = target.PageId;
                            page.Name = "bl102page" + suffix;
                            page.Title = "bl102page" + suffix;
                            page.Path = "bl102page" + suffix;
                            page.PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                new Permission(PermissionNames.Edit, editor.UserId, true),
                                Foreign(siteId)
                            };
                            return (HttpMethod.Post, "/api/Page", page);
                        },
                        async () =>
                        {
                            var created = await InTenantRead(app, sp => sp.GetRequiredService<IPageRepository>().GetPageAsync("bl102page" + suffix, siteId));
                            return created == null ? null : $"page {created.PageId} was created";
                        }),
                    new Attempt("Folder POST", EntityNames.Folder, false, target.FolderId,
                        async () =>
                        {
                            var folder = new Folder
                            {
                                SiteId = siteId,
                                ParentId = target.FolderId,
                                Name = "bl102folder" + suffix,
                                Type = FolderTypes.Public,
                                Path = "",
                                Order = 1,
                                ImageSizes = "",
                                Capacity = 0,
                                IsSystem = false,
                                PermissionList = new List<Permission>
                                {
                                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                    new Permission(PermissionNames.Edit, editor.UserId, true),
                                    Foreign(siteId)
                                }
                            };
                            return (HttpMethod.Post, "/api/Folder", folder);
                        },
                        async () =>
                        {
                            var created = await InTenantRead(app, sp => sp.GetRequiredService<IFolderRepository>().GetFolderAsync(siteId, "Public/bl102folder" + suffix + "/"));
                            return created == null ? null : $"folder {created.FolderId} was created";
                        })
                };

                var failures = new List<string>();
                var victimIndex = 0;
                foreach (var attempt in attempts)
                {
                    var label = $"{store.Name} {attempt.Label}";
                    var victimName = $"bl102victim{++victimIndex}";
                    var victim = await GateDrive.AddUserAsync(app, Host, victimName, VictimPassword, victimName + "@gate.test");

                    var modifiedBefore = attempt.IsPut ? await ModifiedOnAsync(app, attempt.EntityName, attempt.TargetId) : (DateTime?)null;

                    var (method, path, body) = await attempt.Build();
                    var sent = await GateDrive.SendJsonAsync(app, editorClient, editorCookies, Host, editor.UserId, method, path, body, output, label);
                    if (sent.Status != HttpStatusCode.BadRequest)
                    {
                        failures.Add($"{attempt.Label}: expected 400, got {(int)sent.Status}");
                    }

                    var escalated = (await GateDrive.RowsAsync(app, Host, EntityNames.User, PermissionNames.Write))
                        .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                        .ToList();
                    output.WriteLine($"[{label}] (User,-1,Write) rows afterwards = {GateDrive.Describe(escalated)}");
                    if (escalated.Count != 0)
                    {
                        failures.Add($"{attempt.Label}: wrote a site-wide row {GateDrive.Describe(escalated)}");
                    }

                    var anonymousDelete = await GateDrive.SendJsonAsync(app, anonymousClient, null, Host, null, HttpMethod.Delete, $"/api/User/{victim.UserId}?siteid={siteId}", null, output, label + " anonymous DELETE");
                    if ((int)anonymousDelete.Status is >= 200 and < 300)
                    {
                        failures.Add($"{attempt.Label}: ANONYMOUS DELETE /api/User/{victim.UserId} answered {(int)anonymousDelete.Status}");
                    }

                    var editorDelete = await GateDrive.SendJsonAsync(app, editorClient, editorCookies, Host, editor.UserId, HttpMethod.Delete, $"/api/User/{victim.UserId}?siteid={siteId}", null, output, label + " editor DELETE");
                    if ((int)editorDelete.Status is >= 200 and < 300)
                    {
                        failures.Add($"{attempt.Label}: the editor's DELETE /api/User/{victim.UserId} answered {(int)editorDelete.Status}");
                    }

                    var victimExists = await InTenantRead(app, sp => sp.GetRequiredService<IUserRepository>().GetUserAsync(victim.UserId)) != null;
                    if (!victimExists)
                    {
                        failures.Add($"{attempt.Label}: the victim user {victim.UserId} was DELETED");
                    }

                    if (attempt.IsPut)
                    {
                        var editRows = await GateDrive.RowsAsync(app, Host, attempt.EntityName, PermissionNames.Edit);
                        if (!editRows.Any(row => row.UserId == editor.UserId && row.EntityId == attempt.TargetId && row.IsAuthorized))
                        {
                            failures.Add($"{attempt.Label}: the editor's own Edit row on {attempt.EntityName} {attempt.TargetId} is gone {GateDrive.Describe(editRows)}");
                        }

                        var modifiedAfter = await ModifiedOnAsync(app, attempt.EntityName, attempt.TargetId);
                        if (modifiedAfter != modifiedBefore)
                        {
                            failures.Add($"{attempt.Label}: PARTIAL WRITE - {attempt.EntityName} {attempt.TargetId} ModifiedOn {modifiedBefore:O} -> {modifiedAfter:O}");
                        }
                    }
                    else
                    {
                        var created = await attempt.NoChildCreated();
                        if (created != null)
                        {
                            failures.Add($"{attempt.Label}: PARTIAL WRITE - {created}");
                        }
                    }

                    // Independence: an escalated row left behind would decide every later attempt's DELETE.
                    if (escalated.Count != 0)
                    {
                        await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                        {
                            foreach (var row in escalated)
                            {
                                await sp.GetRequiredService<IPermissionRepository>().DeletePermissionAsync(row.PermissionId);
                            }
                            return true;
                        });
                    }
                }

                // ---- (e) NON-VACUITY: the same PUTs without the foreign row still save ------------------------
                foreach (var (entityName, id, route) in new[] { (EntityNames.Module, target.ModuleId, "Module"), (EntityNames.Page, target.PageId, "Page"), (EntityNames.Folder, target.FolderId, "Folder") })
                {
                    object body = entityName switch
                    {
                        EntityNames.Module => await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(id)),
                        EntityNames.Page => await InTenantRead(app, sp => sp.GetRequiredService<IPageRepository>().GetPageAsync(id)),
                        _ => await InTenantRead(app, sp => sp.GetRequiredService<IFolderRepository>().GetFolderAsync(id))
                    };
                    var sent = await GateDrive.SendJsonAsync(app, editorClient, editorCookies, Host, editor.UserId, HttpMethod.Put, $"/api/{route}/{id}", body, output, $"{store.Name} {route} PUT (own rows only)");
                    if (sent.Status != HttpStatusCode.OK)
                    {
                        failures.Add($"non-vacuity {route} PUT: expected 200, got {(int)sent.Status}");
                    }

                    var editRows = await GateDrive.RowsAsync(app, Host, entityName, PermissionNames.Edit);
                    if (!editRows.Any(row => row.UserId == editor.UserId && row.EntityId == id && row.IsAuthorized))
                    {
                        failures.Add($"non-vacuity {route} PUT: the editor's Edit row is gone {GateDrive.Describe(editRows)}");
                    }
                }

                foreach (var failure in failures)
                {
                    output.WriteLine($"[{store.Name}] FAILURE: {failure}");
                }

                Assert.True(failures.Count == 0, $"[{store.Name}] {failures.Count} failure(s): " + string.Join(" | ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>The known-dirty row: the framework's own Host-only User:Write policy granted to All Users.</summary>
        private static Permission Foreign(int siteId) =>
            new Permission(siteId, EntityNames.User, PermissionNames.Write, RoleNames.Everyone, null, true);

        internal static Task<Targets> TargetsAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var pages = (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).ToList();
                var home = pages.First(page => page.Path == "");
                var pageModules = (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                    .Where(pageModule => pageModule.PageId == home.PageId && !pageModule.IsDeleted)
                    .ToList();
                var pageModule = pageModules.FirstOrDefault(item => item.Module.ModuleDefinitionName.Contains("HtmlText"))
                    ?? pageModules.First();
                var folder = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(alias.SiteId, "Public/");
                Assert.True(folder != null, "The installed site has no 'Public/' folder.");
                return new Targets(alias.SiteId, pageModule.ModuleId, home.PageId, folder!.FolderId);
            });

        private static Task<T> InTenantRead<T>(WebApplication app, Func<IServiceProvider, Task<T>> read) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) => read(sp));

        private static async Task<DateTime> ModifiedOnAsync(WebApplication app, string entityName, int id) => entityName switch
        {
            EntityNames.Module => (await InTenantRead(app, sp => sp.GetRequiredService<IModuleRepository>().GetModuleAsync(id))).ModifiedOn,
            EntityNames.Page => (await InTenantRead(app, sp => sp.GetRequiredService<IPageRepository>().GetPageAsync(id))).ModifiedOn,
            _ => (await InTenantRead(app, sp => sp.GetRequiredService<IFolderRepository>().GetFolderAsync(id))).ModifiedOn
        };
    }

    /// <summary>The identical BL-102 scenario on RavenDB - fails, never skips, when the server is unreachable (D46).</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenEntityPermissionWriteValidationTests
    {
        private readonly ITestOutputHelper _output;

        public RavenEntityPermissionWriteValidationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public Task A_foreign_permission_row_on_a_module_page_or_folder_write_is_rejected_on_ravendb() =>
            EntityPermissionWriteValidationTests.RunAsync(new RavenGateStore(), _output);

        [Fact]
        public Task A_host_save_of_the_users_module_instance_in_the_grid_submission_shape_writes_no_framework_row_on_ravendb() =>
            EntityPermissionWriteValidationTests.RunHostGridSaveAsync(new RavenGateStore(), _output);
    }
}
