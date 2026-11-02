using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Models;
using Stomme.Models.IdentityAudit;
using Stomme.Modules.IdentityAudit.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AuditPage = Stomme.Modules.IdentityAudit.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// The identity audit page in words (BL-241, driven): an event said as words ("Sign in failed", not "SignInFailed"), and who
    /// did it by name - "self" on their own account, a name the page holds, else the id - where it showed "#1" beside "host".
    /// </summary>
    public sealed class IdentityAuditPageTests : BunitContext
    {
        [Fact]
        public void Events_and_actors_are_said_in_words()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" } });
            Services.AddSingleton(HelpControlClickTests.Inert<Stomme.Services.ILogService>.Create());
            Services.AddSingleton<IIdentityAuditService>(new Ledger(new List<IdentityAuditEntryView>
            {
                new(Guid.NewGuid(), new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc), IdentityAuditEvent.RoleGranted, 1, 2, "ada", "::1", "Administrators"),
                new(Guid.NewGuid(), new DateTime(2026, 10, 31, 11, 0, 0, DateTimeKind.Utc), IdentityAuditEvent.SignIn, 1, 1, "host", "::1", null),
                new(Guid.NewGuid(), new DateTime(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc), IdentityAuditEvent.SignInFailed, null, null, null, "::1", "unknown account"),
                new(Guid.NewGuid(), new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc), IdentityAuditEvent.SessionRevoked, 9, 2, "ada", "::1", null),
            }));

            var cut = Render<AuditPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(new ModuleModel { ModuleId = 1, SiteId = 1 }));
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("tbody tr").Count));

            var rows = cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td").Select(cell => cell.TextContent.Trim()).ToArray()).ToList();
            Assert.Equal(("Role granted", "ada", "host"), (rows[0][1], rows[0][2], rows[0][3]));   // the actor named from the page
            Assert.Equal(("Sign in", "self"), (rows[1][1], rows[1][3]));
            Assert.Equal(("Sign in failed", "(unknown account)", "—"), (rows[2][1], rows[2][2], rows[2][3]));
            Assert.Equal(("Session revoked", "#9"), (rows[3][1], rows[3][3]));                     // not on the page: its id
        }

        private static PageState Page() => new()
        {
            Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" },
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Stomme.Models.Page { PageId = 1, SiteId = 1, Path = "admin/identity-audit", Name = "Identity Audit" },
            User = new User { UserId = 1, Username = "host", Roles = ";" + RoleNames.Host + ";" + RoleNames.Admin + ";" },
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
            Action = "",
        };

        private sealed class Ledger(List<IdentityAuditEntryView> entries) : IIdentityAuditService
        {
            public Task<List<IdentityAuditEntryView>> GetRecentAsync(int days = 30, int take = 500) => Task.FromResult(entries);
        }
    }
}
