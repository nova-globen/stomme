using System;
using System.Linq;
using System.Reflection;
using Stomme.Modules;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    // Guards the framework's reflection-based assembly/module discovery — the exact silent-fail
    // class that renders a blank page when the assembly filter stops matching the real assembly
    // names (e.g. a rename that leaves the filter literal out of sync). A green build never
    // catches this; these tests do.
    public class DiscoveryTests
    {
        [Fact]
        public void IsStommeAssembly_matches_framework_assemblies_and_rejects_others()
        {
            // A framework assembly must match.
            Assert.True(typeof(Constants).Assembly.IsStommeAssembly());
            // BCL and third-party assemblies must not.
            Assert.False(typeof(string).Assembly.IsStommeAssembly());
            Assert.False(typeof(FactAttribute).Assembly.IsStommeAssembly());
        }

        [Fact]
        public void GetStommeAssemblies_discovers_the_framework_assemblies()
        {
            // In a running app the loader pulls these from /bin; force them in here, then the
            // discovery filter must return them.
            Assembly.Load("Stomme.Shared");
            Assembly.Load("Stomme.Client");
            Assembly.Load("Stomme.Server");

            var names = AppDomain.CurrentDomain.GetStommeAssemblies()
                .Select(a => a.GetName().Name)
                .ToList();

            Assert.NotEmpty(names);
            Assert.Contains("Stomme.Shared", names);
            Assert.Contains("Stomme.Client", names);
            Assert.Contains("Stomme.Server", names);
        }

        // Theme/container/site-template/database constants are concrete, assembly-qualified TYPES.
        [Theory]
        [InlineData(Constants.DefaultTheme)]
        [InlineData(Constants.DefaultContainer)]
        [InlineData(Constants.DefaultSiteTemplate)]
        [InlineData(Constants.DefaultDBType)]
        public void Framework_type_name_constants_resolve_to_real_types(string assemblyQualifiedName)
        {
            Assembly.Load("Stomme.Client");
            Assembly.Load("Stomme.Server");

            var type = Type.GetType(assemblyQualifiedName);

            Assert.NotNull(type);
        }

        // A ModuleDefinitionName is "Namespace, Assembly" — NOT a concrete type. Discovery keys on a
        // namespace that contains an IModuleControl (see ModuleDefinitionRepository). This is the
        // exact predicate that silently returned nothing and blank-paged the site.
        [Theory]
        [InlineData(Constants.AdminDashboardModule)] // its absence NRE'd SiteService
        [InlineData(Constants.PageManagementModule)]
        public void Framework_module_namespaces_expose_a_module_control(string moduleDefinitionName)
        {
            var (ns, asm) = SplitQualifiedName(moduleDefinitionName);

            var hasControl = GetLoadableTypes(Assembly.Load(asm))
                .Any(t => t.Namespace == ns && typeof(IModuleControl).IsAssignableFrom(t));

            Assert.True(hasControl, $"No IModuleControl found in namespace '{ns}' of assembly '{asm}'.");
        }

        // Installer_seeds_a_renderable_site used to sit here, skipped, and was the ONLY guard for "does an
        // installed site actually render" — which is precisely why a dead master shipped twice while this
        // suite was green. It is now real, un-skipped, and lives in InstalledSiteRenderTests: Program.Main
        // was split into BuildAppAsync/Install/Run so the production host can be driven in-process.

        private static (string Namespace, string Assembly) SplitQualifiedName(string qualified)
        {
            var parts = qualified.Split(',', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null).ToArray()!;
            }
        }
    }
}
