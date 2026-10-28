using System;
using System.IO;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-228 (BL-221's correction): the <c>?v=</c> on the site's own static assets is the hash of their CONTENT - the same on
    /// every boot of one build, different as soon as a file is. BL-221 hashed <c>InstallationDate</c>, which a container
    /// rewrites at every boot (UAT, 10.7.0: <c>F8427461</c> to <c>F84FA39D</c>, the boot minute's hash, across one restart).
    /// </summary>
    public sealed class SiteFingerprintTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-fingerprint-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private string WebRoot(string name, string css, string interop)
        {
            var root = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(root, "css"));
            Directory.CreateDirectory(Path.Combine(root, "js"));
            File.WriteAllText(Path.Combine(root, "css", "app.css"), css);
            File.WriteAllText(Path.Combine(root, "js", "interop.js"), interop);
            return root;
        }

        [Fact]
        public void The_same_files_give_the_same_fingerprint_whatever_the_boot()
        {
            // Two web roots with the same files, written at different moments: two boots of one build.
            using var first = new PhysicalFileProvider(WebRoot("boot1", "body { color: red; }", "export {};"));
            var once = SiteAssetFingerprint.Of(first);
            File.SetLastWriteTimeUtc(Path.Combine(_root, "boot1", "css", "app.css"), DateTime.UtcNow.AddHours(-3));
            using var second = new PhysicalFileProvider(WebRoot("boot2", "body { color: red; }", "export {};"));

            Assert.Matches("^[0-9A-F]{8}$", once);
            Assert.Equal(once, SiteAssetFingerprint.Of(second));
            Assert.Equal(once, SiteAssetFingerprint.Of(first));
        }

        [Fact]
        public void A_changed_file_changes_the_fingerprint_even_in_a_running_host()
        {
            using var provider = new PhysicalFileProvider(WebRoot("live", "body { color: red; }", "export {};"));
            var before = SiteAssetFingerprint.Of(provider);

            File.WriteAllText(Path.Combine(_root, "live", "css", "app.css"), "body { color: blue; }");
            File.SetLastWriteTimeUtc(Path.Combine(_root, "live", "css", "app.css"), DateTime.UtcNow.AddMinutes(1));

            Assert.NotEqual(before, SiteAssetFingerprint.Of(provider));
        }

        [Fact]
        public void With_no_file_to_read_it_is_the_framework_versions()
        {
            Directory.CreateDirectory(Path.Combine(_root, "empty"));
            using var empty = new PhysicalFileProvider(Path.Combine(_root, "empty"));

            Assert.Equal(Utilities.GenerateSimpleHash(Constants.Version), SiteAssetFingerprint.Of(empty));
            Assert.Equal(Utilities.GenerateSimpleHash(Constants.Version), SiteAssetFingerprint.Of(null));
        }

        [Fact]
        public void The_files_are_the_ones_App_razor_puts_the_fingerprint_on()
        {
            var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Components", "App.razor"));
            foreach (var file in SiteAssetFingerprint.Files)
            {
                Assert.Contains(file + "?v=@_fingerprint", app);
            }

            Assert.Equal(SiteAssetFingerprint.Files.Count, System.Text.RegularExpressions.Regex.Matches(app, @"\?v=@_fingerprint").Count);
        }

        // A module's or a theme's ?v= (found on UAT 2026-10-06: the pipeline editor's script kept one fingerprint across
        // releases, served by the CDN from its cache): a new framework version or a new declared version is a new URL; the same
        // row in the same build is the same one; a Save (ModifiedOn) still busts it.
        [Fact]
        public void A_module_or_theme_resource_fingerprint_follows_the_release_and_its_own_version()
        {
            var modified = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
            var now = ResourceFingerprint.Of(modified, "1.0.0", "10.12.0");

            Assert.Matches("^[0-9A-F]{8}$", now);
            Assert.Equal(now, ResourceFingerprint.Of(modified, "1.0.0", "10.12.0"));
            Assert.NotEqual(now, ResourceFingerprint.Of(modified, "1.0.0", "10.13.0"));
            Assert.NotEqual(now, ResourceFingerprint.Of(modified, "1.1.0", "10.12.0"));
            Assert.NotEqual(now, ResourceFingerprint.Of(modified.AddMinutes(1), "1.0.0", "10.12.0"));
            Assert.Equal(ResourceFingerprint.Of(modified, "1.0.0", Constants.Version), ResourceFingerprint.Of(modified, "1.0.0"));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
        }
    }
}
