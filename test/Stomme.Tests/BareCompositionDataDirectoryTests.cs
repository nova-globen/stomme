using System;
using System.IO;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-230 (BL-153's remainder): a composition with no host environment - a test fixture that runs a module's startup on a
    /// bare service collection - must not die on a data directory another fixture has already deleted.
    /// </summary>
    /// <remarks>
    /// The process-wide <c>Constants.DataDirectory</c> slot holds whatever host set it last; in a test run that is often a
    /// fixture whose <c>Dispose</c> deleted the folder. Two gates in a row (2026-10-05) died on <c>DirectoryNotFoundException</c>
    /// at <c>HostDataDirectory.Expand</c> - a Dashboards and an MCP startup composed bare. A stale slot now resolves to a
    /// folder of the process's own; a slot whose folder exists is kept, so a fixture that set it on purpose is unchanged.
    /// Serialised with the other hosts that touch the slot.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BareCompositionDataDirectoryTests : IDisposable
    {
        private readonly object? _previous = AppDomain.CurrentDomain.GetData(Constants.DataDirectory);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-bl230-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, _previous);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public void A_bare_composition_whose_slot_names_a_deleted_folder_gets_a_folder_of_its_own()
        {
            Directory.CreateDirectory(_root);
            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(_root, "Data"));
            Directory.Delete(_root, recursive: true); // the owner disposed

            var expanded = HostDataDirectory.Expand(new ServiceCollection(), "Data Source=|DataDirectory|modules.db");

            Assert.Equal("Data Source=" + HostDataDirectory.BareFallback + Path.DirectorySeparatorChar + "modules.db", expanded);
            Assert.True(Directory.Exists(HostDataDirectory.BareFallback));
            Assert.False(Directory.Exists(_root), "the deleted owner's folder is not recreated");
        }

        [Fact]
        public void A_slot_whose_folder_exists_is_kept()
        {
            Directory.CreateDirectory(_root);
            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(_root, "Data"));

            var expanded = HostDataDirectory.Expand(new ServiceCollection(), "Data Source=|DataDirectory|modules.db");

            Assert.Equal("Data Source=" + Path.Combine(_root, "Data") + Path.DirectorySeparatorChar + "modules.db", expanded);
        }
    }
}
