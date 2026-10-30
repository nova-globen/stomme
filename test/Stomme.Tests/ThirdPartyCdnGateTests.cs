#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// No page Stomme renders makes the browser fetch from a public CDN (BL-272). FluentUI 5's <c>FluentNumberInput</c> and its
    /// sortable list load imask and SortableJS from <c>unpkg.com</c> the first time one renders - a request to a third party
    /// carrying the visitor's address, refused by a strict Content-Security-Policy and failing offline - so the source that
    /// ships (the framework, its modules and themes, and the app template) uses neither, and names no CDN host in markup or
    /// code. Documentation may name them, to warn.
    /// </summary>
    public class ThirdPartyCdnGateTests
    {
        private static readonly (string Name, Regex Pattern)[] Rules =
        {
            ("FluentUI's number input (imask from unpkg.com)", new Regex(@"<FluentNumberInput\b")),
            ("FluentUI's sortable list (SortableJS from unpkg.com)", new Regex(@"<FluentSortableList\b")),
            ("a public CDN host", new Regex(@"\b(unpkg\.com|cdn\.jsdelivr\.net|cdnjs\.cloudflare\.com)\b", RegexOptions.IgnoreCase))
        };

        private static readonly string[] Extensions = { ".razor", ".cs", ".cshtml", ".html", ".js" };

        /// <summary>Files allowed to name a CDN host, each with its reason; an entry that no longer matches fails the gate.</summary>
        private static readonly Dictionary<string, string> Allowed = new()
        {
            ["Stomme.Shared/Shared/Constants.cs"] = "BL-274: Bootstrap from cdnjs for the stock BlazorTheme and the external theme template, until it is self-hosted"
        };

        [Fact]
        public void The_shipped_source_uses_no_component_or_script_that_fetches_from_a_public_CDN()
        {
            var src = Path.Combine(RepoRoot(), "src");
            var findings = new List<string>();
            var scanned = 0;
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    || relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules" or "lib")
                    || relative.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;
                var found = Findings(File.ReadAllText(file));
                if (Allowed.ContainsKey(relative))
                {
                    Assert.True(found.Count > 0, $"{relative} is allowed to name a CDN ({Allowed[relative]}) but no longer does: remove the entry.");
                    continue;
                }

                findings.AddRange(found.Select(finding => $"{relative}: {finding}"));
            }

            Assert.True(scanned > 500, $"only {scanned} files scanned - the source folder moved?");
            Assert.True(findings.Count == 0, "Shipped source fetches from a public CDN at run time:\n" + string.Join("\n", findings));
        }

        [Theory]
        [InlineData("<FluentNumberInput id=\"retention\" @bind-Value=\"_retention\" />", 1)]
        [InlineData("<FluentSortableList Items=\"_items\" />", 1)]
        [InlineData("<script src=\"https://unpkg.com/imask@7.6.1/dist/imask.min.js\"></script>", 1)]
        [InlineData("<input id=\"retention\" type=\"number\" class=\"db-input\" @bind=\"@_retention\" />", 0)]
        [InlineData("<FluentTextInput id=\"name\" @bind-Value=\"_name\" />", 0)]
        public void The_rules_find_what_they_should_and_nothing_else(string text, int expected)
        {
            Assert.Equal(expected, Findings(text).Count);
        }

        private static List<string> Findings(string text) =>
            Rules.Where(rule => rule.Pattern.IsMatch(text)).Select(rule => rule.Name).ToList();

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return directory!.FullName;
        }
    }
}
