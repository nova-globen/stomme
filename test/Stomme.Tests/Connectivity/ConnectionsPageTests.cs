using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Connectivity;
using Stomme.Models;
using Stomme.Modules.Connectivity.Services;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ConnectionsPage = Stomme.Modules.Connectivity.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-229 - the Connections page's form became a component of its own (<c>ConnectionForm</c>) so the pipeline editor makes a
    /// connection where a step needs it. The page still makes one from a connector's fields and its credentials, edits one
    /// without reading its credentials back (blank leaves them), and says what the server refused, in its words.
    /// </summary>
    public sealed class ConnectionsPageTests : BunitContext
    {
        private static readonly Guid Billing = Guid.Parse("0c7e5a10-0000-4000-8000-000000000002");
        private readonly FakeConnections _connections = new();

        public ConnectionsPageTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLocalization(); // a row's delete confirmation
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IConnectionsService>(_connections);
        }

        [Fact]
        public void A_connection_is_made_from_its_connectors_fields_and_its_credentials()
        {
            var cut = Render<ConnectionsPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#connection-" + Billing)));

            cut.Find("#connections-connector").Change("postgres");
            cut.Find("#connections-name").Change("Orders DB");
            cut.Find("#connections-endpoint").Change("Host=orders.db.test;Database=orders");
            cut.Find("#connections-credential-username").Change("orders");
            cut.Find("#connections-credential-password").Change("test-only-secret");
            Assert.Equal("password", cut.Find("#connections-credential-password").GetAttribute("type"));
            cut.Find("#connections-save").Click();

            cut.WaitForAssertion(() => Assert.Equal("'Orders DB' saved.", cut.Find("#connections-message").TextContent.Trim()));
            var made = Assert.Single(_connections.Created);
            Assert.Equal(("Orders DB", "postgres", "Host=orders.db.test;Database=orders"), (made.Name, made.ConnectorType, made.Endpoint));
            Assert.Equal(new Dictionary<string, string> { ["username"] = "orders", ["password"] = "test-only-secret" }, made.Credentials);
            Assert.Equal("", cut.Find("#connections-name").GetAttribute("value") ?? ""); // the form ready for the next one
            Assert.Equal(2, _connections.Listed); // the list read again
        }

        [Fact]
        public void A_connection_is_edited_without_its_credentials_read_back_and_a_refusal_is_said()
        {
            var cut = Render<ConnectionsPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#connection-" + Billing)));

            cut.Find("#connection-" + Billing).QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Edit").Click();
            Assert.Equal("Billing", cut.Find("#connections-name").GetAttribute("value"));
            Assert.Equal("Host=billing.db.test;Database=billing", cut.Find("#connections-endpoint").GetAttribute("value"));
            Assert.Equal("", cut.Find("#connections-credential-password").GetAttribute("value") ?? ""); // never read back
            Assert.Contains("blank keeps the stored value", cut.Find("#connections-credential-password").GetAttribute("placeholder"));

            cut.Find("#connections-name").Change("Billing (EU)");
            cut.Find("#connections-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("'Billing (EU)' saved.", cut.Find("#connections-message").TextContent.Trim()));
            var (id, request) = Assert.Single(_connections.Updated);
            Assert.Equal((Billing, "Billing (EU)"), (id, request.Name));
            Assert.Null(request.Credentials); // blank: left as stored
            Assert.Empty(cut.FindAll("#connections-cancel")); // back to a new connection

            _connections.Refusal = "You may not manage this site's connections.";
            cut.Find("#connections-connector").Change("postgres");
            cut.Find("#connections-name").Change("Another");
            cut.Find("#connections-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("The connection was not saved: You may not manage this site's connections.", cut.Find("#connections-refusal").TextContent.Trim()));
            Assert.Equal("Another", cut.Find("#connections-name").GetAttribute("value")); // nothing typed is lost
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page() => new()
        {
            Alias = Alias(),
            Site = new Site { SiteId = 1, Name = "Site" },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/connections", Name = "Connections" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
            QueryString = new Dictionary<string, string>(),
            UrlParameters = "",
            Action = "",
        };

        /// <summary>The key rows as the module definition seeds them - every key to Administrators.</summary>
        private static ModuleModel Module() => new()
        {
            ModuleId = 51,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(ConnectionPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        internal sealed class FakeConnections : IConnectionsService
        {
            public List<ConnectionRequest> Created { get; } = new();

            public List<(Guid Id, ConnectionRequest Request)> Updated { get; } = new();

            public int Listed { get; private set; }

            /// <summary>When set, a save is refused with these words.</summary>
            public string? Refusal { get; set; }

            private readonly List<ConnectionView> _connections = new() { View(Billing, "Billing", "Host=billing.db.test;Database=billing") };

            private static ConnectionView View(Guid id, string name, string endpoint) =>
                new(id, name, "postgres", "Postgres", endpoint, new Dictionary<string, string>(), new[] { "username", "password" }, 30, "Untested", null);

            public Task<List<ConnectionView>> ListAsync()
            {
                Listed++;
                return Task.FromResult(_connections.ToList());
            }

            public Task<ConnectionView> GetAsync(Guid id) => Task.FromResult(_connections.Single(connection => connection.Id == id));

            public Task<List<ConnectorView>> ListConnectorsAsync() => Task.FromResult(new List<ConnectorView>
            {
                new("postgres", "Postgres", "Basic", "ConnectionString", false, Array.Empty<ConnectorFieldView>(),
                    new[] { new ConnectorAuthFieldView("username", "Username", false), new ConnectorAuthFieldView("password", "Password", true) },
                    new[] { new ConnectorCategoryView("data", "Data and files", 1) }, Array.Empty<ConnectorOperationView>(), null, null, ""),
            });

            public Task<ConnectionView> CreateAsync(ConnectionRequest request)
            {
                if (Refusal is { } refusal)
                {
                    throw new InvalidOperationException(refusal);
                }

                Created.Add(request);
                var made = View(Guid.NewGuid(), request.Name, request.Endpoint);
                _connections.Add(made);
                return Task.FromResult(made);
            }

            public Task<ConnectionView> UpdateAsync(Guid id, ConnectionRequest request)
            {
                if (Refusal is { } refusal)
                {
                    throw new InvalidOperationException(refusal);
                }

                Updated.Add((id, request));
                return Task.FromResult(View(id, request.Name, request.Endpoint));
            }

            public Task<ConnectionTestView> TestAsync(Guid id) => Task.FromResult(new ConnectionTestView(true, ConnectorOutcome.Succeeded, "Connected to PostgreSQL.", "Healthy"));

            public Task DeleteAsync(Guid id) => Task.CompletedTask;

            public Task<ConnectionEgressView> GetEgressAsync() => Task.FromResult(new ConnectionEgressView(Array.Empty<string>(), false));

            public Task<ConnectionEgressView> SaveEgressAsync(string allowedHosts) => throw new NotSupportedException();
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
