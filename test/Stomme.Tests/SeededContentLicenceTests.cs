using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// What every new install shows its visitors states the licence the product has (PolyForm Noncommercial
    /// OR PolyForm Small Business, a commercial licence for everyone else; D98's "all rights reserved" until 2026-10-26). Found on 2026-10-01 on a fresh install's home page: the default site template still seeded "an open source
    /// multi-tenant CMS", the full MIT licence text under the title "MIT License", MySQL among the stores (removed) and a
    /// <c>git clone</c> of a GitHub repository that does not exist - on every install and every app made from the template,
    /// while every package declares the proprietary licence. The 22 module definitions named that repository as
    /// their contact too. The text is the product's statement to the public, so it is pinned here, read from the source.
    /// </summary>
    public sealed class SeededContentLicenceTests
    {
        // D115 replaced D98's "all rights reserved" for everything published, so its wording is forbidden too.
        private static readonly string[] Forbidden =
        {
            "MIT License", "Permission is hereby granted", "open source", "github.com/nova-globen", "MySQL",
            "All rights reserved", "proprietary software",
        };

        [Fact]
        public void The_default_site_template_states_the_product_licence_and_nothing_else()
        {
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Infrastructure", "SiteTemplates", "DefaultSiteTemplate.cs"));

            Assert.Empty(Forbidden.Where(text => template.Contains(text, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("PolyForm Noncommercial 1.0.0 or PolyForm Small Business 1.0.0", template, StringComparison.Ordinal);
            Assert.Contains("commercial licence from Nova Globen AB", template, StringComparison.Ordinal);
            Assert.Contains("Required Notice: Copyright Nova Globen AB (https://nova-globen.se)", template, StringComparison.Ordinal);
        }

        // 2026-10-28 (the rebranding, infra's ruling C18): the public repository exists now, so a module definition names
        // it as its contact and the product page as its URL - and no other host.
        [Fact]
        public void Every_module_definition_names_the_product_page_and_the_public_repository()
        {
            var modules = Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules");
            var infos = Directory.EnumerateFiles(modules, "ModuleInfo.cs", SearchOption.AllDirectories).ToList();
            Assert.True(infos.Count >= 20, $"Only {infos.Count} ModuleInfo.cs files under {modules}; the scan is not reading the modules.");

            var wrong = new List<string>();
            foreach (var info in infos)
            {
                var text = File.ReadAllText(info);
                if (text.Contains("Url = \"", StringComparison.Ordinal) && !text.Contains("Url = \"https://nova-globen.se/products/stomme\"", StringComparison.Ordinal)
                    || text.Contains("Contact = \"", StringComparison.Ordinal) && !text.Contains("Contact = \"https://github.com/nova-globen/stomme\"", StringComparison.Ordinal))
                {
                    wrong.Add(Path.GetRelativePath(modules, info));
                }
            }

            Assert.True(wrong.Count == 0, "Module definitions naming another URL or contact: " + string.Join(", ", wrong));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
