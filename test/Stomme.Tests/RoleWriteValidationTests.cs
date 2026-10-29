using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>admin/roles</c> accepted an EMPTY role name. The client marks the input required, but
    /// <c>RoleController.Post/Put</c> checked only <c>ModelState</c> and <c>Role.Name</c> carries no
    /// annotation, so a blank or whitespace name written through the API created a nameless role row.
    /// </summary>
    /// <remarks>
    /// Driven over real HTTP as the HOST (the write policy is Administrators-gated), both directions: a blank
    /// and a whitespace name are refused with 400 and add no row, while an ordinary name still saves and is
    /// still renamable.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class RoleWriteValidationTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public RoleWriteValidationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_blank_role_name_is_refused_on_post_and_put()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var before = await RoleNamesAsync(app, siteId);
                var failures = new List<string>();

                // ---- POST: blank and whitespace are refused, and nothing is written ------------------------
                foreach (var (label, name) in new[] { ("empty", ""), ("whitespace", "   ") })
                {
                    var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Role",
                        new Role { SiteId = siteId, Name = name, Description = "bl87", IsAutoAssigned = false, IsSystem = false },
                        _output, $"POST role ({label} name)");
                    if (sent.Status != HttpStatusCode.BadRequest)
                    {
                        failures.Add($"POST {label} name: expected 400, got {(int)sent.Status}");
                    }

                    var after = await RoleNamesAsync(app, siteId);
                    if (after.Count != before.Count)
                    {
                        failures.Add($"POST {label} name: a role row was written ({before.Count} -> {after.Count})");
                    }
                }

                // ---- NON-VACUITY: an ordinary name still saves ---------------------------------------------
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Role",
                    new Role { SiteId = siteId, Name = "BL87 Role", Description = "bl87", IsAutoAssigned = false, IsSystem = false },
                    _output, "POST role (valid name)");
                if (created.Status != HttpStatusCode.OK)
                {
                    failures.Add($"POST valid name: expected 200, got {(int)created.Status}");
                }

                var role = (await RolesAsync(app, siteId)).FirstOrDefault(item => item.Name == "BL87 Role");
                if (role == null)
                {
                    failures.Add("POST valid name: the role was not created");
                }
                else
                {
                    // ---- PUT: blank and whitespace are refused, and the stored name is unchanged -----------
                    foreach (var (label, name) in new[] { ("empty", ""), ("whitespace", "   ") })
                    {
                        var body = await StoredAsync(app, siteId, role.RoleId);
                        body!.Name = name;
                        var sent = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Put, $"/api/Role/{role.RoleId}", body,
                            _output, $"PUT role ({label} name)");
                        if (sent.Status != HttpStatusCode.BadRequest)
                        {
                            failures.Add($"PUT {label} name: expected 400, got {(int)sent.Status}");
                        }

                        var stored = (await RolesAsync(app, siteId)).FirstOrDefault(item => item.RoleId == role.RoleId);
                        if (stored == null || stored.Name != "BL87 Role")
                        {
                            failures.Add($"PUT {label} name: the stored name changed to '{stored?.Name}'");
                        }
                    }

                    // ---- NON-VACUITY: a real rename still saves -------------------------------------------
                    var renameBody = await StoredAsync(app, siteId, role.RoleId);
                    renameBody!.Name = "BL87 Renamed";
                    var renamed = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Put, $"/api/Role/{role.RoleId}", renameBody,
                        _output, "PUT role (valid name)");
                    if (renamed.Status != HttpStatusCode.OK)
                    {
                        failures.Add($"PUT valid name: expected 200, got {(int)renamed.Status}");
                    }

                    if ((await RolesAsync(app, siteId)).All(item => item.Name != "BL87 Renamed"))
                    {
                        failures.Add("PUT valid name: the rename was not stored");
                    }
                }

                foreach (var failure in failures)
                {
                    _output.WriteLine($"FAILURE: {failure}");
                }

                Assert.True(failures.Count == 0, $"{failures.Count} failure(s): " + string.Join(" | ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<Role?> StoredAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId, int roleId) =>
            (await RolesAsync(app, siteId)).FirstOrDefault(item => item.RoleId == roleId);

        private static Task<List<Role>> RolesAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(siteId)).ToList());

        private static async Task<List<string>> RoleNamesAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId) =>
            (await RolesAsync(app, siteId)).Select(role => role.Name).ToList();
    }
}
