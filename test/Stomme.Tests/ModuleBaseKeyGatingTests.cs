using System.Collections.Generic;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;
using Stomme.Shared;
using Stomme.UI;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The CLIENT half of the authorization bridge (D99 point 8, BL-99): a lifted page asks
    /// <see cref="ModuleBase.IsAuthorizedPermission(string)"/> the SAME question the server's
    /// <c>[Requires]</c> asks - and gets its answer from the module instance's own
    /// <c>PermissionList</c>, which already carries the foreign <c>EntityId = -1</c> key rows
    /// (<c>PageModuleRepository</c>'s ':'-merge). No claim, no HTTP call, every render mode.
    /// </summary>
    /// <remarks>
    /// F14: the method ALREADY EXISTS and is called with a plain permission name (<c>View</c>,
    /// <c>Edit</c>) all over the framework. The key form therefore BRANCHES INSIDE it on the ':' -
    /// an overload with the same signature is not possible, and a second method name would leave a
    /// lifted page silently ungated if an author picked the wrong one.
    /// </remarks>
    public class ModuleBaseKeyGatingTests
    {
        [Fact]
        public void The_key_form_answers_from_the_foreign_minus_one_rows()
        {
            var module = Module(
                new Permission(1, "gatetest", -1, "manage", "GateRole", null, true),
                new Permission(1, EntityNames.Module, 5, PermissionNames.View, RoleNames.Everyone, null, true));

            var member = Component(User("GateRole"), module);
            Assert.True(member.IsAuthorizedPermission("gatetest:manage"));

            var outsider = Component(User(RoleNames.Registered), module);
            Assert.False(outsider.IsAuthorizedPermission("gatetest:manage"));
        }

        [Fact]
        public void The_key_form_is_denied_when_no_row_carries_that_resource_and_operation()
        {
            // The operation half must match too: a 'gatetest:view' grant does not admit 'gatetest:manage'
            // (the legacy path filters on PermissionName ALONE, which is exactly why the key form cannot
            // reuse it - 'view' is also the stock module View permission's name on a lifted module).
            var module = Module(new Permission(1, "gatetest", -1, "view", "GateRole", null, true));

            Assert.False(Component(User("GateRole"), module).IsAuthorizedPermission("gatetest:manage"));
            Assert.True(Component(User("GateRole"), module).IsAuthorizedPermission("gatetest:view"));
        }

        [Fact]
        public void The_key_form_honours_a_deny_row_first_like_every_other_stomme_decision()
        {
            var module = Module(
                new Permission(1, "gatetest", -1, "manage", RoleNames.Everyone, null, true),
                new Permission(1, "gatetest", -1, "manage", "GateRole", null, false));

            Assert.False(Component(User("GateRole"), module).IsAuthorizedPermission("gatetest:manage"));
            Assert.True(Component(User(RoleNames.Registered), module).IsAuthorizedPermission("gatetest:manage"));
        }

        [Fact]
        public void The_key_form_is_split_at_the_last_colon_and_matched_case_insensitively()
        {
            var module = Module(new Permission(1, "platform.modules", -1, "manage", "GateRole", null, true));

            Assert.True(Component(User("GateRole"), module).IsAuthorizedPermission("Platform.Modules:Manage"));
        }

        [Fact]
        public void The_legacy_form_is_unchanged()
        {
            // The stock question every framework module asks: a plain permission name over the module's own
            // rows. The merged key rows sit in the SAME list, and the legacy path filters on PermissionName
            // ALONE - what keeps them out of this answer is that a declared key is lower-cased at declaration
            // (PermissionKeys.IsValidSegment REJECTS an upper-case segment) while the framework's permission
            // names are title-cased, and the filter is ordinal. MEASURED: seed the same list with a
            // capitalised 'Edit' key row instead and this assertion flips - which is why declaration, not the
            // reader, is where the casing contract is enforced (D49: normalise the data).
            var module = Module(
                new Permission(1, EntityNames.Module, 5, PermissionNames.Edit, RoleNames.Admin, null, true),
                new Permission(1, "gatetest", -1, "edit", RoleNames.Everyone, null, true));

            Assert.True(Component(User(RoleNames.Admin), module).IsAuthorizedPermission(PermissionNames.Edit));
            Assert.False(Component(User(RoleNames.Registered), module).IsAuthorizedPermission(PermissionNames.Edit));

            // ...and the key form still sees that same row, from the other side of the branch.
            Assert.True(Component(User(RoleNames.Registered), module).IsAuthorizedPermission("gatetest:edit"));
        }

        [Fact]
        public void A_malformed_key_is_denied_rather_than_falling_back_to_the_legacy_match()
        {
            var module = Module(new Permission(1, "gatetest", -1, "manage", RoleNames.Everyone, null, true));

            Assert.False(Component(User("GateRole"), module).IsAuthorizedPermission("gatetest:"));
            Assert.False(Component(User("GateRole"), module).IsAuthorizedPermission(":manage"));
        }

        private static Models.Module Module(params Permission[] permissions)
        {
            return new Models.Module { ModuleId = 5, SiteId = 1, PermissionList = new List<Permission>(permissions) };
        }

        private static User User(params string[] roles)
        {
            return new User { UserId = 9, Username = "alice", Roles = string.Join(";", roles) + ";" };
        }

        private static GatedModule Component(User user, Models.Module module)
        {
            var component = new GatedModule();
            component.Bind(new PageState { User = user }, module);
            return component;
        }

        /// <summary>
        /// A ModuleBase with no markup: the cascading parameters are protected, so a derived type is how
        /// the decision is driven WITHOUT a renderer (the answer is pure - PageState.User + PermissionList).
        /// </summary>
        private sealed class GatedModule : ModuleBase
        {
            public void Bind(PageState pageState, Models.Module module)
            {
                PageState = pageState;
                ModuleState = module;
            }
        }
    }
}