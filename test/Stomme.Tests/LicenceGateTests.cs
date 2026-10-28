#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-118: the licence gate proves every RESOLVED package permissive, direct and transitive, instead of passing every
    /// package its denylist does not name. Driven both ways through the real script against a fabricated restore - a
    /// <c>project.assets.json</c> and nuspecs in a package folder of the test's own - so a planted copyleft package fails
    /// without anything being restored from a feed.
    /// </summary>
    public sealed class LicenceGateTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "licence-gate-" + Guid.NewGuid().ToString("N"));

        public LicenceGateTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("<license type=\"expression\">MIT</license>", true)]
        [InlineData("<license type=\"expression\">Apache-2.0 OR MPL-2.0</license>", true)]         // one permissive alternative
        [InlineData("<license type=\"expression\">GPL-3.0-only</license>", false)]                 // the planted copyleft package
        [InlineData("<license type=\"expression\">MIT AND GPL-2.0-only</license>", false)]         // AND needs every part
        [InlineData("<license type=\"expression\">LGPL-2.1-or-later</license>", false)]
        [InlineData("<license type=\"file\">LICENSE.txt</license>", false)]                         // a file nobody read
        [InlineData("<licenseUrl>https://example.com/eula.html</licenseUrl>", false)]               // a URL nobody read
        [InlineData("", false)]                                                                     // no licence at all
        public void A_resolved_package_passes_only_when_its_licence_is_PROVEN_permissive(string licenceElement, bool passes)
        {
            Restore("Planted.Package", "1.2.3", licenceElement);
            var (exit, output) = RunGate();

            Assert.True((exit == 0) == passes, $"exit {exit} for [{licenceElement}]:{Environment.NewLine}{output}");
            if (!passes)
            {
                Assert.Contains("Planted.Package/1.2.3", output, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_package_whose_nuspec_is_not_in_the_package_folder_FAILS_rather_than_passing_unread()
        {
            Restore("Missing.Package", "9.9.9", licenceElement: null);
            var (exit, output) = RunGate();
            Assert.Equal(1, exit);
            Assert.Contains("Missing.Package/9.9.9", output, StringComparison.Ordinal);
        }

        private void Restore(string id, string version, string? licenceElement)
        {
            var packages = Path.Combine(_root, "packages") + Path.DirectorySeparatorChar;
            var obj = Path.Combine(_root, "project", "obj");
            Directory.CreateDirectory(obj);

            var assets = new
            {
                version = 3,
                libraries = new System.Collections.Generic.Dictionary<string, object>
                {
                    [$"{id}/{version}"] = new { type = "package", path = $"{id.ToLowerInvariant()}/{version}" },
                },
                packageFolders = new System.Collections.Generic.Dictionary<string, object> { [packages] = new { } },
            };
            File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(assets));

            if (licenceElement is not null)
            {
                var folder = Path.Combine(packages, id.ToLowerInvariant(), version);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, id.ToLowerInvariant() + ".nuspec"),
                    $"<?xml version=\"1.0\"?><package><metadata><id>{id}</id><version>{version}</version>{licenceElement}</metadata></package>");
            }
        }

        private (int Exit, string Output) RunGate()
        {
            var script = Path.Combine(RepositoryRoot(), "scripts", "check-licenses.ps1");
            var start = new ProcessStartInfo("pwsh", $"-NoProfile -File \"{script}\" -AssetsRoot \"{Path.Combine(_root, "project")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            _output.WriteLine(output);
            return (process.ExitCode, output);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
