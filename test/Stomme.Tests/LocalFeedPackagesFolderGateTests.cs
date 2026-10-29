#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The examples' local feed (examples/local-feed) holds this tree's packages under the framework's release version
    /// numbers. Restored into the machine-wide NuGet cache, they stand in for the published packages of the same version
    /// in every later build on the machine - measured: a host built after the release compiled against the local, pre-fix
    /// bytes. So every NuGet config that restores from the local feed keeps its packages in a folder of its own.
    /// </summary>
    public class LocalFeedPackagesFolderGateTests
    {
        [Fact]
        public void Every_config_that_restores_from_the_local_feed_keeps_its_packages_out_of_the_machine_wide_cache()
        {
            var root = RepoRoot();
            var configs = Directory.EnumerateFiles(Path.Combine(root, "examples"), "*.config", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".nuget-local" or "local-feed"))
                .ToList();
            var fromLocalFeed = configs.Where(path => XDocument.Load(path).Descendants("packageSources").Descendants("add")
                .Any(source => ((string?)source.Attribute("value") ?? "").Contains("local-feed", StringComparison.OrdinalIgnoreCase))).ToList();
            Assert.True(fromLocalFeed.Count >= 5, $"Expected the examples' local-feed configs; found {fromLocalFeed.Count} - the scan has stopped matching.");

            var offenders = fromLocalFeed.Where(path =>
            {
                var folder = XDocument.Load(path).Descendants("config").Descendants("add")
                    .FirstOrDefault(setting => (string?)setting.Attribute("key") == "globalPackagesFolder");
                var value = (string?)folder?.Attribute("value");
                return value is null || !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, value))
                    .Equals(Path.Combine(root, "examples", ".nuget-local"), StringComparison.OrdinalIgnoreCase);
            }).Select(path => Path.GetRelativePath(root, path)).ToList();
            Assert.True(offenders.Count == 0,
                "These configs restore from examples/local-feed into the machine-wide NuGet cache, where a release version must " +
                "mean the published bytes; give each <config><add key=\"globalPackagesFolder\" value=\"(relative path to " +
                "examples/.nuget-local)\" /></config>: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_exemplar_drive_points_its_generated_module_at_the_examples_packages_folder_too()
        {
            // The drive rewrites the generated module's config to restore from the local feed; it must set the folder as well.
            var drive = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "Modules", "KnowledgeBase", "KnowledgeBase.Drive", "KnowledgeBaseInstallDriveTests.cs"));
            Assert.Contains("\"local-feed\"", drive);
            Assert.Contains("globalPackagesFolder", drive);
            Assert.Contains("\".nuget-local\"", drive);
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root (Stomme.slnx) was not found.");
        }
    }
}
