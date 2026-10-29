using System;
using System.IO;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests
{
    // ConfigManager resolved its write target into STATIC fields from the constructor, so when several hosts
    // run in one process (every integration suite here does) the last-constructed host silently redirected
    // EVERY host's AddOrUpdateSetting into its own file. A prerender install's write once polluted the shared
    // test-bin appsettings.json and broke two unrelated suites. The write target must be per-instance state,
    // captured at construction.
    //
    // Shares the non-parallel collection because it swaps the override-path environment variable, which is
    // process-global.
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ConfigManagerWriteTargetTests : IDisposable
    {
        private readonly string _firstRoot;
        private readonly string _secondRoot;
        private readonly string _previousOverridePath;

        public ConfigManagerWriteTargetTests()
        {
            _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            _firstRoot = NewOverrideRoot();
            _secondRoot = NewOverrideRoot();
        }

        private static string NewOverrideRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-config-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "appsettings.override.json"), "{}");
            return root;
        }

        private sealed class StubEnvironment : IWebHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "Stomme.Tests";
            public string WebRootPath { get; set; } = "";
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = "";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        // Exactly what two hosts in one process do: each resolves the override variable as it composes. The
        // variable is process-global, so it is set immediately before each construction — the value each
        // ConfigManager captures is the one that was current when IT was built.
        private static ConfigManager NewConfigManager(string overrideRoot)
        {
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, overrideRoot);
            return new ConfigManager(new ConfigurationBuilder().Build(), new StubEnvironment(), NullLogger<ConfigManager>.Instance);
        }

        [Fact]
        public void Each_hosts_config_writes_land_in_its_own_override_file()
        {
            var first = NewConfigManager(_firstRoot);
            var second = NewConfigManager(_secondRoot);

            // Constructing the second host must not have redirected the first host's write target.
            first.AddOrUpdateSetting("Bl59Probe", "from-first-host", reload: false);

            Assert.Contains("from-first-host", File.ReadAllText(Path.Combine(_firstRoot, "appsettings.override.json")));
            Assert.DoesNotContain("Bl59Probe", File.ReadAllText(Path.Combine(_secondRoot, "appsettings.override.json")));

            second.AddOrUpdateSetting("Bl59Probe", "from-second-host", reload: false);

            Assert.Contains("from-second-host", File.ReadAllText(Path.Combine(_secondRoot, "appsettings.override.json")));
            Assert.DoesNotContain("from-second-host", File.ReadAllText(Path.Combine(_firstRoot, "appsettings.override.json")));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

            Directory.Delete(_firstRoot, recursive: true);
            Directory.Delete(_secondRoot, recursive: true);
        }
    }
}
