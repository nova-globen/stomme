using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Every browser-side service a page injects has a SERVER side, resolved from an installed host's request scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework registers <c>IClientService</c> implementations only in the WebAssembly client's <c>Program</c>; the server
    /// scans <c>IService</c> and <c>ITransientService</c>. A page rendered on the server - every site whose Runtime is Server,
    /// the default for a fresh install and for every site the admin UI creates - therefore resolves its injected service from
    /// what its module's server startup registers, and a module that registers nothing renders "Missing service(s)" instead
    /// of its page. MCP shipped without its twin once and pinned it for itself (<c>McpAdminSurfaceDriveTests</c>); the Erasure
    /// receipts page then shipped without one too, and was found only by clicking it in a browser - its API
    /// drive was green, because the controller has dependencies of its own. This gate reads the client assembly, so a new
    /// module's service joins it with no edit here.
    /// </para>
    /// <para>
    /// The resolved implementation must also not be the browser's own class: that one calls the host over HTTP, so a server
    /// rendering through it would call itself. ONE service does, by the framework's design and with a reason below; its entry
    /// fails the day it gets a server side of its own.
    /// </para>
    /// <para>
    /// A module that composes nothing while its switch is off (Pipelines) places its page only while the switch is on, so the
    /// host here runs with that switch on - its service is checked where its page can exist.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ClientServiceServerTwinTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public ClientServiceServerTwinTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Client services the server resolves as the browser's own HTTP class, each with its reason. Not a list to grow: a
        /// module's page gets a server side (see any <c>Server*Service</c> under <c>src/Stomme.Server/Modules</c>).
        /// </summary>
        private static readonly IReadOnlyDictionary<Type, string> ServedOverHttpByDesign = new Dictionary<Type, string>
        {
            [typeof(Stomme.Services.ISearchResultsService)] =
                "the framework's core services (ISiteService, IPageService, ISearchService and the rest) reach the API over HTTP on the " +
                "server too; SearchResultsService is registered with them in StommeServiceCollectionExtensions and works the way they do",
        };

        /// <summary>Each browser-side service class in <c>Stomme.Client</c> with the service interfaces it declares itself.</summary>
        internal static IReadOnlyList<(Type Implementation, Type Contract)> ClientServiceContracts()
        {
            var client = typeof(Stomme.Modules.Erasure.Services.ErasureService).Assembly;
            var inherited = new Func<Type, IEnumerable<Type>>(type => type.BaseType?.GetInterfaces() ?? Type.EmptyTypes);
            return client.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IClientService).IsAssignableFrom(type))
                .SelectMany(type => type.GetInterfaces()
                    .Except(inherited(type))
                    .Where(contract => contract != typeof(IClientService))
                    .Select(contract => (type, contract)))
                .OrderBy(pair => pair.contract.FullName, StringComparer.Ordinal)
                .ToList();
        }

        [Fact]
        public void The_scan_finds_the_client_services_it_exists_for()
        {
            // Vacuity guard: a floor, not an exact count - an exact count is a hand-kept list in disguise.
            var contracts = ClientServiceContracts();
            Assert.True(contracts.Count >= 20, $"Only {contracts.Count} client service contracts found; the scan is not reading the client assembly.");
            Assert.Contains(contracts, pair => pair.Contract == typeof(Stomme.Modules.Erasure.Services.IErasureService));
            Assert.Contains(contracts, pair => pair.Contract == typeof(Stomme.Modules.Mcp.Services.IMcpService));
        }

        [Fact]
        public async Task Every_client_service_resolves_on_the_server_and_not_as_the_browsers_http_class()
        {
            var switchedOn = new System.Text.Json.Nodes.JsonObject { ["Pipelines"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: switchedOn);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var problems = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                {
                    var found = new List<string>();
                    foreach (var (implementation, contract) in ClientServiceContracts())
                    {
                        object resolved;
                        try
                        {
                            resolved = sp.GetService(contract);
                        }
                        catch (Exception ex)
                        {
                            found.Add($"{contract.FullName}: resolving it threw {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        if (resolved is null)
                        {
                            found.Add($"{contract.FullName}: nothing is registered on the server, so a server-rendered page injecting it cannot render.");
                        }
                        else if (resolved.GetType() == implementation)
                        {
                            if (!ServedOverHttpByDesign.ContainsKey(contract))
                            {
                                found.Add($"{contract.FullName}: the server resolves the browser's {implementation.Name}, which would call its own host over HTTP.");
                            }
                        }
                        else if (ServedOverHttpByDesign.ContainsKey(contract))
                        {
                            found.Add($"{contract.FullName}: it has a server side now ({resolved.GetType().Name}); remove its entry from ServedOverHttpByDesign.");
                        }
                        else
                        {
                            _output.WriteLine($"{contract.Name} -> {resolved.GetType().FullName}");
                        }
                    }

                    return Task.FromResult(found);
                });

                Assert.True(problems.Count == 0, "Client services with no server side:\n" + string.Join("\n", problems));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
