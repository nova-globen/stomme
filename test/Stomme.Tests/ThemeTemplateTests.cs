using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Infrastructure;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The External theme template Create Theme generates from, in the module template's shape (BL-241, BL-239's wC-3): the
    /// slim SDK only, packed by `dotnet pack` over its nuspec, isolated from an enclosing repository, its stylesheet served
    /// at the package id's address. Before, a "local" theme referenced the framework's Debug DLLs by a path that exists only
    /// in the framework's repository and packed with FixProps.exe and nuget.exe, so its output did not build in an app.
    /// Driven 2026-10-31: generated next to an app made from the 10.79.0 template, packed against nuget.org's slim SDK,
    /// installed by the host's InstallationManager and rendered as the site's theme. This suite is the part the gate runs.
    /// </summary>
    public sealed class ThemeTemplateTests
    {
        private static readonly string[] GeneratorTokens =
            { "[Owner]", "[Theme]", "[Description]", "[FrameworkVersion]", "[FrameworkRelease]", "[StommeFeed]", "[ClientReference]", "[SharedReference]" };

        [Fact]
        public void The_generated_theme_builds_on_the_slim_sdk_with_every_token_filled()
        {
            var root = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "Themes", "Templates", "External");
            var replace = new TokenReplace(new ModuleTemplateTests.SilentLog());
            replace.AddSource(() => new Dictionary<string, object>
            {
                { "Owner", "Acme" },
                { "Theme", "Harbor" },
                { "Description", "The Harbor theme." },
                { "FrameworkVersion", "10.5.0-preview.1" },
                { "FrameworkRelease", ModuleTemplateTokens.ReleaseNumber("10.5.0-preview.1") },
                { "StommeFeed", ModuleTemplateTokens.PackageFeed },
            });
            var generated = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !ModuleTemplateTokens.IsBinary(file))
                .Select(file => (Path: replace.ReplaceTokens(Path.GetRelativePath(root, file)), Text: replace.ReplaceTokens(File.ReadAllText(file))))
                .ToList();

            foreach (var (path, text) in generated)
            {
                Assert.All(GeneratorTokens, token => Assert.DoesNotContain(token, path));
                Assert.All(GeneratorTokens, token => Assert.DoesNotContain(token, text));
            }

            var client = generated.Single(file => file.Path.EndsWith("Acme.Theme.Harbor.Client.csproj", StringComparison.Ordinal)).Text;
            Assert.Contains("<PackageReference Include=\"Stomme.Sdk.Slim\" Version=\"10.5.0-preview.1\" />", client);
            Assert.Contains("<StaticWebAssetBasePath>_content/Acme.Theme.Harbor</StaticWebAssetBasePath>", client);
            Assert.Contains("<AssemblyName>Acme.Theme.Harbor.Client.Stomme</AssemblyName>", client);

            var nuspec = generated.Single(file => file.Path.EndsWith(".nuspec", StringComparison.Ordinal)).Text;
            Assert.Contains("<id>Acme.Theme.Harbor</id>", nuspec);
            Assert.Contains("<packageType name=\"Stomme.Framework\" version=\"10.5.0\" />", nuspec);
            Assert.Contains("target=\"staticwebassets\"", nuspec);
            Assert.Contains("<NuspecFile>Acme.Theme.Harbor.nuspec</NuspecFile>", generated.Single(file => file.Path.EndsWith("Package.csproj", StringComparison.Ordinal)).Text);

            // Nothing that only works inside the framework's repository or on Windows.
            Assert.All(generated, file =>
            {
                Assert.DoesNotContain("HintPath", file.Text);
                Assert.DoesNotContain("FixProps", file.Text);
                Assert.DoesNotContain("nuget.exe", file.Text);
                Assert.DoesNotContain("Include=\"Stomme.Server\"", file.Text);
                Assert.DoesNotContain("Include=\"Stomme.Client\"", file.Text);
            });
            Assert.DoesNotContain(generated, file => file.Path.EndsWith(".cmd", StringComparison.Ordinal) || file.Path.EndsWith(".sh", StringComparison.Ordinal));

            // Isolated from an enclosing repository, restoring Stomme.* from one feed.
            Assert.Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", generated.Single(file => file.Path == "Directory.Packages.props").Text);
            Assert.Contains(ModuleTemplateTokens.PackageFeed, generated.Single(file => file.Path == "nuget.config").Text);

            // The theme's colour is the token Stomme's controls read, and the stylesheet is the one ThemeInfo links.
            Assert.Contains("--db-accent:", generated.Single(file => file.Path.EndsWith("Theme.css", StringComparison.Ordinal)).Text);
            Assert.Contains("_content/Acme.Theme.Harbor/Theme.css", generated.Single(file => file.Path.EndsWith("ThemeInfo.cs", StringComparison.Ordinal)).Text);
            Assert.True(File.Exists(Path.Combine(root, "Package", "icon.png")));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root (Stomme.slnx) was not found above the test's bin.");
        }
    }
}