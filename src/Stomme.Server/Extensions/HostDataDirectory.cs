using System;
using System.IO;
using System.Linq;
using Stomme.Shared;
using Microsoft.AspNetCore.Hosting;

// Same namespace as the composition extensions that use it (ModuleAggregatePersistenceExtensions,
// ModuleRepositoryProviderRegistry), so no using is needed at those call sites.
namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// The host's own data directory, for code that runs while the container is still being COMPOSED and so
    /// cannot inject <c>IConfigManager</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that can inject asks <c>IConfigManager.GetDataDirectory()</c>. A handful of seams cannot,
    /// because they run inside <c>ConfigureServices</c> before anything is resolvable: the module-aggregate
    /// persistence extensions, the module repository provider registry, and FeatureManagement's server startup.
    /// They used to read <c>AppDomain.CurrentDomain.GetData(Constants.DataDirectory)</c> — a PROCESS-global slot,
    /// correct for a deployed app with one host and wrong the moment two hosts coexist, which is the normal state
    /// of a test run and the mechanism behind a cross-suite <c>DirectoryNotFoundException</c>
    /// (<c>DataDirectoryIsProcessGlobalTests</c>).
    /// </para>
    /// <para>
    /// <b>The service collection carries the host environment as an INSTANCE at that point — measured, not
    /// assumed</b> (<c>CompositionSeamSeesHostEnvironmentTests</c>), which is the same route
    /// <c>ResolveConfiguredDatabaseType</c> already takes to reach <c>IConfigurationRoot</c>. So the composing
    /// host can be asked for its own content root rather than the process being asked for the last one.
    /// </para>
    /// <para>
    /// The fallback is the old global, then an <c>App_Data</c> folder beside the assembly: a minimal fixture that
    /// composes a bare <see cref="IServiceCollection"/> with no host environment keeps working exactly as before.
    /// </para>
    /// </remarks>
    internal static class HostDataDirectory
    {
        /// <summary>This host's <c>{ContentRootPath}/Data</c>, or the previous behaviour when there is no host.</summary>
        public static string Resolve(IServiceCollection services)
        {
            var contentRoot = services?
                .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IWebHostEnvironment))?
                .ImplementationInstance as IWebHostEnvironment;

            if (!string.IsNullOrEmpty(contentRoot?.ContentRootPath))
            {
                return Path.Combine(contentRoot.ContentRootPath, "Data");
            }

            // A bare composition reads the process-wide slot, which holds whatever host set it LAST - in a test run,
            // a fixture whose Dispose may already have deleted that folder. A slot
            // whose owning folder is gone is stale: such a composition gets a folder of its own process instead. A fixture
            // that set the slot on purpose, and still has its folder, keeps it.
            var global = AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(global))
            {
                return string.Empty;
            }

            var owner = Path.GetDirectoryName(global.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(owner) || Directory.Exists(owner) ? global : BareFallback;
        }

        /// <summary>The data directory of a bare composition whose process-wide slot is stale: one per process, deleted by no fixture.</summary>
        internal static string BareFallback =>
            Path.Combine(Path.GetTempPath(), "stomme-bare-composition", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Data");

        private static bool HasHost(IServiceCollection services) =>
            services?.Any(descriptor => descriptor.ServiceType == typeof(IWebHostEnvironment)) == true;

        /// <summary>Expands the <c>|DataDirectory|</c> token against THIS host's data directory.</summary>
        public static string Expand(IServiceCollection services, string connectionString)
        {
            // Nothing to expand, nothing to create. This used to resolve - and CREATE - the data directory for
            // every connection string, token or not; a fixture handing in an explicit path then raced whichever suite owned
            // the directory the process-wide fallback pointed at, and died on a DirectoryNotFoundException for a folder it
            // never needed (measured in a full gate: a Notifications SQLite conformance case, another suite's deleted root).
            if (string.IsNullOrEmpty(connectionString) || !connectionString.Contains($"|{Constants.DataDirectory}|", StringComparison.Ordinal))
            {
                return connectionString;
            }

            var dataDirectory = Resolve(services);
            if (string.IsNullOrEmpty(dataDirectory))
            {
                return connectionString;
            }

            try
            {
                Directory.CreateDirectory(dataDirectory);
            }
            catch (DirectoryNotFoundException) when (!HasHost(services))
            {
                // The slot's owner deleted its folder between the check above and this line.
                dataDirectory = BareFallback;
                Directory.CreateDirectory(dataDirectory);
            }

            return connectionString.Replace($"|{Constants.DataDirectory}|", dataDirectory + Path.DirectorySeparatorChar);
        }
    }
}
