using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-192: every <c>&lt;Fluent...&gt;</c> tag in this repository's Razor files names a component FluentUI actually ships.
    /// </summary>
    /// <remarks>
    /// A tag whose component no longer exists does not fail the build: Razor reads <c>&lt;FluentTextField&gt;</c> as a plain
    /// HTML element, so after FluentUI 5 removed it a page would compile, render, and show nothing where its field was. The
    /// move to FluentUI 5 renamed or removed five of the thirteen components Stomme used (<c>FluentTextField</c>,
    /// <c>FluentNumberField</c>, <c>FluentSearch</c>, <c>FluentDesignTheme</c>, the non-generic <c>FluentSelect</c>/<c>FluentOption</c>);
    /// the compiler named only the ones with a two-way binding. This reads the files, so a module, theme or template added
    /// later joins it with no edit here.
    /// </remarks>
    public sealed class FluentComponentTagTests
    {
        private static readonly Regex Tag = new(@"<(Fluent[A-Z][A-Za-z0-9]*)[\s/>]", RegexOptions.Compiled);

        internal static IReadOnlySet<string> ShippedComponents() =>
            typeof(Microsoft.FluentUI.AspNetCore.Components.FluentButton).Assembly.GetExportedTypes()
                .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => type.Name.Split('`')[0])
                .ToHashSet(StringComparer.Ordinal);

        internal static IReadOnlyList<(string File, int Line, string Name)> Tags(string root)
        {
            var found = new List<(string, int, string)>();
            foreach (var folder in new[] { "src", "examples" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    var parts = relative.Split(Path.DirectorySeparatorChar);
                    if (parts.Contains("bin") || parts.Contains("obj"))
                    {
                        continue;
                    }

                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        foreach (Match match in Tag.Matches(lines[i]))
                        {
                            found.Add((relative, i + 1, match.Groups[1].Value));
                        }
                    }
                }
            }

            return found;
        }

        [Fact]
        public void Every_Fluent_tag_names_a_component_FluentUI_ships()
        {
            var shipped = ShippedComponents();
            var tags = Tags(RepositoryRoot());
            Assert.True(tags.Count >= 50, $"Only {tags.Count} <Fluent...> tags found; the scan is not reading the Razor files.");

            var unknown = tags.Where(tag => !shipped.Contains(tag.Name)).Select(tag => $"{tag.File}({tag.Line}): <{tag.Name}>").ToList();
            Assert.True(unknown.Count == 0,
                "These tags name no FluentUI component, so Razor renders them as empty HTML elements:\n" + string.Join("\n", unknown));
        }

        [Fact]
        public void The_scan_tells_a_removed_component_from_a_shipped_one()
        {
            // Both directions of the instrument: the names FluentUI 5 dropped are not in its set, the ones Stomme uses are.
            var shipped = ShippedComponents();
            foreach (var removed in new[] { "FluentTextField", "FluentNumberField", "FluentSearch", "FluentDesignTheme" })
            {
                Assert.DoesNotContain(removed, shipped);
            }

            foreach (var used in new[] { "FluentTextInput", "FluentNumberInput", "FluentSelect", "FluentOption", "FluentButton", "FluentToastProvider" })
            {
                Assert.Contains(used, shipped);
            }

            Assert.Equal("FluentTextField", Tag.Match("  <FluentTextField id=\"x\" />").Groups[1].Value);
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
