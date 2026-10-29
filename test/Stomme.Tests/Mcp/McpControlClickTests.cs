using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Mcp;
using Stomme.Models;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Services;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ModuleModel = Stomme.Models.Module;
using McpAudit = Stomme.Modules.Mcp.Audit;
using McpCatalog = Stomme.Modules.Mcp.Catalog;
using McpIndex = Stomme.Modules.Mcp.Index;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// For the MCP module: the controls' <c>@onclick</c> handlers are BOUND.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Razor file compiled without <c>@using Microsoft.AspNetCore.Components.Web</c> turns <c>@onclick</c> into a
    /// plain HTML attribute: the module builds green, renders, and every click is silently dead with no console error.
    /// No SSR or HTTP drive can see it - <c>McpAdminSurfaceDriveTests</c> would stay green through the whole defect.
    /// bUnit's <c>Click()</c> THROWS <c>MissingEventHandlerException</c> on an element with no bound handler, so these
    /// facts go red on exactly that.
    /// </para>
    /// <para>
    /// The controls render with a real <c>PageState</c>/<c>Module</c> cascade carrying the module's key rows as the
    /// definition seeds them, so <c>IsAuthorizedPermission</c> is exercised as well: the three-way key split is
    /// asserted HERE at the control, and over HTTP in <c>McpAdminSurfaceDriveTests</c> - the server decides again
    /// either way, and a control that merely hides an action is not a gate.
    /// </para>
    /// </remarks>
    public sealed class McpControlClickTests : BunitContext
    {
        private readonly RecordingMcpService _service = new();

        public McpControlClickTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IMcpService>(_service);
        }

        [Fact]
        public void Clicking_refresh_on_a_registered_server_calls_the_service()
        {
            var cut = Render<McpIndex>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Admin))
                .AddCascadingValue(Module()));

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-mcp-server-table")));
            cut.FindAll(".db-mcp-server-table button").First(button => button.TextContent.Trim() == "Refresh").Click();

            cut.WaitForAssertion(() => Assert.Equal(1, _service.Refreshes));
        }

        [Fact]
        public void Clicking_register_opens_the_form_and_submits_what_was_typed()
        {
            var cut = Render<McpIndex>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Admin))
                .AddCascadingValue(Module()));

            cut.Find("#mcp-server-new").Click();
            cut.Find("#mcp-server-name").Input("Example");
            cut.Find("#mcp-server-endpoint").Input("https://mcp.example.test/mcp");
            cut.Find("#mcp-server-save").Click();

            cut.WaitForAssertion(() => Assert.Equal(1, _service.Registrations.Count));
            Assert.Equal("Example", _service.Registrations[0].Name);
            Assert.Equal("https://mcp.example.test/mcp", _service.Registrations[0].Endpoint);
        }

        [Fact]
        public void The_catalog_and_audit_controls_load_and_their_refresh_buttons_are_bound()
        {
            var catalog = Render<McpCatalog>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Admin))
                .AddCascadingValue(Module()));
            var catalogReads = _service.PublishedReads;
            catalog.Find("#mcp-catalog-refresh").Click();
            catalog.WaitForAssertion(() => Assert.Equal(catalogReads + 1, _service.PublishedReads));

            var audit = Render<McpAudit>(parameters => parameters
                .AddCascadingValue(Page(RoleNames.Admin))
                .AddCascadingValue(Module()));
            var auditReads = _service.AuditReads;
            audit.Find("#mcp-audit-refresh").Click();
            audit.WaitForAssertion(() => Assert.Equal(auditReads + 1, _service.AuditReads));
        }

        [Fact]
        public void Each_control_is_gated_on_its_OWN_key_and_a_denied_control_never_calls_the_service()
        {
            // The three-way split at the control. A user in a role holding NONE of the MCP keys gets the denial marker
            // on all three and the service is not called - so a control does not leak through an empty list either.
            foreach (var render in new Func<IRenderedComponent<Microsoft.AspNetCore.Components.ComponentBase>>[]
                     {
                         () => Render<McpIndex>(p => p.AddCascadingValue(Page("Some Other Role")).AddCascadingValue(Module())),
                         () => Render<McpCatalog>(p => p.AddCascadingValue(Page("Some Other Role")).AddCascadingValue(Module())),
                         () => Render<McpAudit>(p => p.AddCascadingValue(Page("Some Other Role")).AddCascadingValue(Module())),
                     })
            {
                var cut = render();
                Assert.NotEmpty(cut.FindAll(".db-mcp-denied"));
            }

            Assert.Equal(0, _service.ServerReads);
            Assert.Equal(0, _service.PublishedReads);
            Assert.Equal(0, _service.AuditReads);
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page(string role) => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/mcp", Name = "MCP" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + role + ";" },
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
        };

        /// <summary>The key rows as the module definition seeds them - all three to Administrators.</summary>
        private static ModuleModel Module() => new()
        {
            ModuleId = 11,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(McpPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        private sealed class RecordingMcpService : IMcpService
        {
            public readonly List<McpServerRegistrationRequest> Registrations = new();
            public int ServerReads;
            public int Refreshes;
            public int PublishedReads;
            public int AuditReads;

            public Task<List<McpServerRegistrationView>> GetServersAsync()
            {
                ServerReads++;
                return Task.FromResult(new List<McpServerRegistrationView>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Existing",
                        Endpoint = "https://existing.example.test/mcp",
                        Enabled = true,
                        TrustState = "Unverified",
                        RegisteredOnUtc = DateTime.UtcNow,
                    },
                });
            }

            public Task<McpServerRegistrationView> RegisterServerAsync(McpServerRegistrationRequest request)
            {
                Registrations.Add(request);
                return Task.FromResult(new McpServerRegistrationView { Id = Guid.NewGuid(), Name = request.Name, Endpoint = request.Endpoint });
            }

            public Task RefreshServerAsync(Guid id)
            {
                Refreshes++;
                return Task.CompletedTask;
            }

            public Task SetServerEnabledAsync(Guid id, bool enabled) => Task.CompletedTask;

            public Task RemoveServerAsync(Guid id) => Task.CompletedTask;

            public Task SetApiKeyAsync(Guid id, string apiKey) => Task.CompletedTask;

            public Task<List<McpPublishedItemView>> GetPublishedAsync()
            {
                PublishedReads++;
                return Task.FromResult(new List<McpPublishedItemView>
                {
                    new() { Kind = "resource", Name = "stomme://site", Description = "The site." },
                });
            }

            public Task<List<McpAuditEntryView>> GetAuditAsync(int days)
            {
                AuditReads++;
                return Task.FromResult(new List<McpAuditEntryView>());
            }
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}
