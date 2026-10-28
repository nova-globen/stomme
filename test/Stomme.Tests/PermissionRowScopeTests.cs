using System.Collections.Generic;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>The shared BL-102 predicate - it must classify rows exactly as the permission sink stamps them.</summary>
    public class PermissionRowScopeTests
    {
        [Fact]
        public void An_empty_entity_name_is_the_entitys_own_row()
        {
            var rows = new List<Permission> { new Permission(PermissionNames.View, RoleNames.Everyone, true) };

            Assert.Empty(PermissionRowScope.ForeignRows(rows, EntityNames.Module));
            Assert.Single(PermissionRowScope.OwnRows(rows, EntityNames.Module));
        }

        [Fact]
        public void The_same_entity_name_is_own_and_a_different_one_is_foreign()
        {
            var own = new Permission(1, EntityNames.Module, 5, PermissionNames.Edit, RoleNames.Admin, null, true);
            var foreign = new Permission(1, EntityNames.User, PermissionNames.Write, RoleNames.Everyone, null, true);
            var rows = new List<Permission> { own, foreign };

            Assert.Equal(new[] { foreign }, PermissionRowScope.ForeignRows(rows, EntityNames.Module));
            Assert.Equal(new[] { own }, PermissionRowScope.OwnRows(rows, EntityNames.Module));
        }

        [Fact]
        public void A_case_variant_of_the_entity_name_is_foreign_because_the_sink_compares_ordinally()
        {
            var rows = new List<Permission> { new Permission(1, "module", PermissionNames.Edit, RoleNames.Admin, null, true) };

            Assert.Single(PermissionRowScope.ForeignRows(rows, EntityNames.Module));
            Assert.Empty(PermissionRowScope.OwnRows(rows, EntityNames.Module));
        }

        [Fact]
        public void A_null_list_is_empty_both_ways()
        {
            Assert.Empty(PermissionRowScope.ForeignRows(null!, EntityNames.Page));
            Assert.Empty(PermissionRowScope.OwnRows(null!, EntityNames.Page));
        }

        [Fact]
        public void Plain_permission_names_strip_every_three_segment_entry_and_keep_order()
        {
            Assert.Equal("View,Edit", PermissionRowScope.PlainPermissionNames("View, gatetest:manage:Administrators ,Edit,User:Write:Administrators"));
        }

        [Fact]
        public void An_all_framework_declaration_has_no_plain_names()
        {
            Assert.Equal("", PermissionRowScope.PlainPermissionNames("User:Write:Administrators,UserRole:Write:Administrators"));
            Assert.Equal("", PermissionRowScope.PlainPermissionNames(""));
            Assert.Equal("", PermissionRowScope.PlainPermissionNames(null!));
        }
    }
}