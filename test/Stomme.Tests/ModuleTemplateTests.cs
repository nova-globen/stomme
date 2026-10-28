using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Enums;
using Stomme.Infrastructure;
using Log = Stomme.Models.Log;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-86 increment 7 - the in-app External module template, regenerated in the exemplar's shape (split, storage-blind,
    /// tenant-owned, keyed, the slim SDK only). Every template file is expanded here through the host's own token replacer
    /// with the tokens the generator supplies, and the output is checked for what a generated module must and must not be.
    /// The template's generated module is BUILT, packed, installed and driven by the exemplar's drive
    /// (<c>examples/Modules/KnowledgeBase</c>), which has the slim SDK packed beside it; this suite is the part the commit
    /// gate can run.
    /// </summary>
    public sealed class ModuleTemplateTests
    {
        private static readonly string[] GeneratorTokens =
            { "[Owner]", "[Module]", "[ModuleKey]", "[Description]", "[ServerManagerType]", "[FrameworkVersion]", "[FrameworkRelease]", "[StommeFeed]", "[RootFolder]", "[RootPath]" };

        [Theory]
        [InlineData("Notes", "notes")]
        [InlineData("KnowledgeBase", "knowledge-base")]
        [InlineData("FieldNotes2", "field-notes2")]
        [InlineData("My Notes", "my-notes")]
        [InlineData("HTMLText", "htmltext")]
        [InlineData("  Odd--Name!  ", "odd-name")]
        public void A_module_name_becomes_a_lower_case_key_resource(string name, string expected)
        {
            Assert.Equal(expected, ModuleTemplateTokens.ModuleKey(name));
            Assert.True(Stomme.Security.PermissionKeys.IsValidSegment(expected), expected);
        }

        [Theory]
        [InlineData("10.5.0", "10.5.0")]
        [InlineData("10.5.0-preview.1", "10.5.0")]
        [InlineData("10.5.0+5f2a91c", "10.5.0")]
        [InlineData(null, "")]
        public void A_package_type_takes_the_release_number_of_the_framework_version(string version, string expected) =>
            Assert.Equal(expected, ModuleTemplateTokens.ReleaseNumber(version));

        [Theory]
        [InlineData("icon.png", true)]
        [InlineData("Module.WOFF2", true)]
        [InlineData("ModuleInfo.cs", false)]
        [InlineData("[Owner].Module.[Module].nuspec", false)]
        public void Binary_template_files_are_copied_not_rewritten(string fileName, bool binary) =>
            Assert.Equal(binary, ModuleTemplateTokens.IsBinary(fileName));

        [Fact]
        public void The_generated_module_is_the_exemplars_shape_on_the_slim_sdk_with_every_token_filled()
        {
            var generated = Generate(out var templateRoot);

            // Every generator token is filled, in every file and every path.
            foreach (var (path, text) in generated)
            {
                Assert.All(GeneratorTokens, token => Assert.DoesNotContain(token, path));
                Assert.All(GeneratorTokens, token => Assert.DoesNotContain(token, text));
            }

            // C# attributes are not tokens and survive the replacer.
            var controller = generated.Single(file => file.Path.EndsWith(Path.Combine("Controllers", "FieldNotesController.cs"), StringComparison.Ordinal)).Text;
            Assert.Contains("[Route(ControllerRoutes.ApiRoute)]", controller);
            Assert.Contains("[HttpGet]", controller);
            Assert.Contains("[RequiresPermissionKey(FieldNotesPermissions.View)]", controller);
            Assert.Contains("\"field-notes:view\"", generated.Single(file => file.Path.EndsWith("FieldNotesContracts.cs", StringComparison.Ordinal)).Text);

            // The slim SDK only: every project that references Stomme does so through one package, at the chosen version -
            // a preview's, label and all (BL-192) - while the package type, which NuGet refuses with a label, takes its release.
            var projects = generated.Where(file => file.Path.EndsWith(".csproj", StringComparison.Ordinal) && !file.Path.Contains("Package", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, projects.Count);
            Assert.All(projects, project => Assert.Contains("<PackageReference Include=\"Stomme.Sdk.Slim\" Version=\"10.5.0-preview.1\" />", project.Text));
            Assert.Contains("<packageType name=\"Stomme.Framework\" version=\"10.5.0\" />",
                generated.Single(file => file.Path.EndsWith(".nuspec", StringComparison.Ordinal)).Text);
            // None of the retired seam's wiring: no DLL by path, no EF Core or RavenDB of its own, no fat framework package. (The
            // README and the aggregate's remarks SAY there is no DbContext; what is refused is using one.)
            Assert.All(generated, file =>
            {
                Assert.DoesNotContain("HintPath", file.Text);
                Assert.DoesNotContain("using Microsoft.EntityFrameworkCore", file.Text);
                Assert.DoesNotMatch(@":\s*DbContext\b", file.Text);
                Assert.DoesNotContain("RavenDB.Client", file.Text);
                Assert.DoesNotContain("Include=\"Stomme.Server\"", file.Text);
            });
            Assert.DoesNotContain(generated, file => file.Path.Contains("Migrations", StringComparison.Ordinal));

            // WebAssembly safety (BL-13, BL-54): the browser-bound projects drop the OpenApi generator; the pages can click.
            Assert.All(projects.Where(project => !project.Path.Contains("Server", StringComparison.Ordinal)), project => Assert.Contains("RemoveOpenApiSourceGenerator", project.Text));
            Assert.Contains("@using Microsoft.AspNetCore.Components.Web", generated.Single(file => file.Path.EndsWith("_Imports.razor", StringComparison.Ordinal)).Text);

            // Isolation: the module pins its own versions and imports nothing from an enclosing repository.
            Assert.Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", generated.Single(file => file.Path.EndsWith("Directory.Packages.props", StringComparison.Ordinal)).Text);
            Assert.Contains(ModuleTemplateTokens.PackageFeed, generated.Single(file => file.Path.EndsWith("nuget.config", StringComparison.Ordinal)).Text);

            // The server manager the generator names exists in the template.
            Assert.Contains(generated, file => file.Path.EndsWith(Path.Combine("Manager", "FieldNotesManager.cs"), StringComparison.Ordinal)
                && file.Text.Contains("namespace Acme.Module.FieldNotes.Manager", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(templateRoot, "Package", "icon.png")));
        }

        // Every text file of the template, expanded through the host's TokenReplace with the generator's tokens.
        private static List<(string Path, string Text)> Generate(out string templateRoot)
        {
            templateRoot = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "Modules", "Templates", "External");
            var replace = new TokenReplace(new SilentLog());
            replace.AddSource(() => new Dictionary<string, object>
            {
                { "Owner", "Acme" },
                { "Module", "FieldNotes" },
                { "ModuleKey", ModuleTemplateTokens.ModuleKey("FieldNotes") },
                { "Description", "Notes taken in the field." },
                { "ServerManagerType", "Acme.Module.FieldNotes.Manager.FieldNotesManager, Acme.Module.FieldNotes.Server.Stomme" },
                { "FrameworkVersion", "10.5.0-preview.1" },
                { "FrameworkRelease", ModuleTemplateTokens.ReleaseNumber("10.5.0-preview.1") },
                { "StommeFeed", ModuleTemplateTokens.PackageFeed },
            });

            var root = templateRoot;
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !ModuleTemplateTokens.IsBinary(file))
                .Select(file => (replace.ReplaceTokens(Path.GetRelativePath(root, file)), replace.ReplaceTokens(File.ReadAllText(file))))
                .ToList();
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

        private sealed class SilentLog : ILogManager
        {
            public void Log(Stomme.Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args)
            {
            }

            public void Log(Stomme.Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
            {
            }

            public void Log(int siteId, Stomme.Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args)
            {
            }

            public void Log(int siteId, Stomme.Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
            {
            }

            public void Log(Log log)
            {
            }
        }
    }
}
