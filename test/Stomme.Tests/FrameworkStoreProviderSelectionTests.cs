using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stomme.Repository.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// D54 — resolve-time framework-store provider selection. Both providers are registered in the host; the
    /// one actually resolved for each closed store contract is chosen from <c>IConfigManager.GetDatabaseType()</c>
    /// at first resolve. These cases prove the RELATIONAL direction and the lazy-store property without a live
    /// RavenDB server; the Raven direction (and that Tenant/Alias documents actually land) is proven live by
    /// <see cref="RavenInstallDrivesFrameworkWritesTests"/>.
    /// </summary>
    public class FrameworkStoreProviderSelectionTests : IDisposable
    {
        private const string PostgresType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
        private readonly List<string> _tempRoots = new();

        // On a relational install the EF implementation is resolved for every closed store contract — which is
        // the whole of the lazy-store guarantee stated behaviourally: the RavenDB store is never constructed, so
        // its IDocumentStore (built by a factory that would connect to RavenDB) is never resolved, so no
        // connection to RavenDB is attempted. If the selector picked Raven here, resolving any store below would
        // build the lazy document store and try to open http://... — there is no RavenDB in this test.
        [Fact]
        public void Relational_config_resolves_the_ef_provider_for_every_contract()
        {
            using var app = ComposeHost(new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = PostgresType,
                // A relational connection string on purpose: if the Raven store factory were (wrongly) invoked
                // here it would try to parse this as a RavenDB descriptor and throw, so the test would fail
                // loudly rather than pass by luck.
                ["ConnectionStrings:DefaultConnection"] = "Server=127.0.0.1;Port=5432;Database=stomme;User Id=u;Password=p;",
            });

            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            AssertEfImplementation(sp.GetRequiredService<ISiteStore>());
            AssertEfImplementation(sp.GetRequiredService<IPermissionStore>());
            AssertEfImplementation(sp.GetRequiredService<IUserAccountStore>());
            AssertEfImplementation(sp.GetRequiredService<IFrameworkStoreSessionFactory>());
            AssertEfImplementation(sp.GetRequiredService<IMasterStoreSessionFactory>());
            AssertEfImplementation(sp.GetRequiredService<ISearchQueryStore>());
        }

        // The fresh-boot default: no configured provider means the relational default (SqlServer), so the EF
        // provider must be selected. This is the case a composition-time branch would get wrong — it is the only
        // thing config says before an install writes the real provider.
        [Fact]
        public void Default_empty_config_resolves_the_ef_provider()
        {
            using var app = ComposeHost(new Dictionary<string, string?>());

            using var scope = app.Services.CreateScope();
            AssertEfImplementation(scope.ServiceProvider.GetRequiredService<ISiteStore>());
            AssertEfImplementation(scope.ServiceProvider.GetRequiredService<IFrameworkStoreSessionFactory>());
        }

        private static void AssertEfImplementation(object store)
        {
            var name = store.GetType().Name;
            Assert.False(name.StartsWith("Raven", StringComparison.Ordinal),
                $"Expected the EF implementation on a relational install; resolved the RavenDB one ({name}). " +
                "The lazy RavenDB document store would have been built and a connection attempted.");
        }

        // Mirrors HostCompositionTests: build the REAL host container (real AddStomme, real assembly scan) so
        // the selection runs against the actual registrations, with DI validation on.
        private WebApplication ComposeHost(Dictionary<string, string?> settings)
        {
            LoadFrameworkAssemblies();

            var contentRoot = CreateTempContentRoot();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                WebRootPath = "wwwroot",
                EnvironmentName = Environments.Development,
                ApplicationName = typeof(Stomme.Server.Program).Assembly.GetName().Name,
            });

            builder.Logging.ClearProviders();
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(contentRoot, "Data"));

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            builder.Services.AddStomme(configuration, builder.Environment);

            return builder.Build();
        }

        private static void LoadFrameworkAssemblies()
        {
            var probePath = Path.GetDirectoryName(typeof(FrameworkStoreProviderSelectionTests).Assembly.Location)!;
            foreach (var dll in Directory.EnumerateFiles(probePath, "Stomme*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // Not a managed Stomme assembly, or already loaded — the scan is name-based and tolerates this.
                }
            }
        }

        private string CreateTempContentRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-provider-selection", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            _tempRoots.Add(root);
            return root;
        }

        public void Dispose()
        {
            foreach (var root in _tempRoots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }
}
