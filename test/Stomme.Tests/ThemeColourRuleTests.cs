using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The rule: "buttons must follow the colors defined in their theme and nothing must overide
    /// that. if no color is defined then they must follow the default."
    /// </summary>
    /// <remarks>
    /// A theme's colours are its <c>--db-*</c> tokens; <c>app.css</c>'s <c>:root</c> holds the defaults. Found overriding them on
    /// the day: FluentUI 5 deriving its own shade from a colour handed to it, a copy of the colour hard-coded in the theme's
    /// markup, <c>var(--db-accent, #5B63D3)</c> fallbacks in module stylesheets, and the external themes keeping their colour in
    /// private variables Stomme's own buttons never read. These read the source, so a new stylesheet joins them unedited.
    /// </remarks>
    public sealed class ThemeColourRuleTests
    {
        private static readonly Regex HardFallback = new(@"var\(\s*--db-[a-z0-9-]+\s*,\s*(#[0-9a-fA-F]{3,8}|rgba?\(|hsla?\()", RegexOptions.Compiled);

        [Fact]
        public void No_stylesheet_or_markup_gives_a_theme_token_a_colour_of_its_own()
        {
            var root = RepositoryRoot();
            var offenders = new List<string>();
            var scanned = 0;
            foreach (var folder in new[] { "src", "examples" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories)
                             .Where(file => file.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
                {
                    var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                    if (parts.Contains("bin") || parts.Contains("obj") || parts.Contains("texteditors"))
                    {
                        continue; // build output, and the vendored editor stylesheets, which know nothing of the tokens
                    }

                    scanned++;
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (HardFallback.IsMatch(lines[i]))
                        {
                            offenders.Add($"{Path.GetRelativePath(root, file)}({i + 1}): {lines[i].Trim()}");
                        }
                    }
                }
            }

            Assert.True(scanned >= 50, $"Only {scanned} stylesheets and Razor files scanned; the scan is not reading the tree.");
            Assert.True(offenders.Count == 0,
                "A fallback colour inside var(--db-...) is a colour no theme defined - app.css's :root always defines the token:\n" + string.Join("\n", offenders));
        }

        [Theory]
        [InlineData("src", "Stomme.Server", "wwwroot", "css", "app.css")]
        [InlineData("src", "Stomme.Maui", "wwwroot", "css", "app.css")]
        public void FluentUI_takes_its_brand_colours_from_the_theme(params string[] path)
        {
            var css = File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(path).ToArray()));
            foreach (var binding in new[]
                     {
                         "--colorBrandBackground: var(--db-accent)",
                         "--colorNeutralForegroundOnBrand: var(--db-on-accent)",
                         "--colorCompoundBrandBackground: var(--db-accent)",
                         "--colorBrandForegroundLink: var(--db-accent)",
                     })
            {
                Assert.Contains(binding, css);
            }
        }

        [Theory]
        [InlineData("Stomme.Theme.Aurora")]
        [InlineData("Stomme.Theme.Slate")]
        public void An_external_theme_hands_its_colour_to_the_shared_controls(string theme)
        {
            var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "examples", "Themes", theme, theme, "wwwroot", "Theme.css"));
            Assert.Matches(@":root\s*\{[^}]*--db-accent:\s*#[0-9A-Fa-f]{6};[^}]*--db-on-accent:", css);
        }

        [Fact]
        public void Every_theme_token_a_stylesheet_or_page_reads_is_defined()
        {
            // Measured: --db-danger (fourteen error messages), --db-surface, --db-surface-2, --db-bg-elev-1 and
            // --db-shadow-lg were read but defined nowhere, so each element always showed the fallback written beside it - a
            // colour no theme defined. A token is defined by a stylesheet or set inline on an element (the dashboard grid's).
            var root = RepositoryRoot();
            var definition = new Regex(@"(--db-[a-z0-9-]+)\s*:", RegexOptions.Compiled);
            var reference = new Regex(@"var\(\s*(--db-[a-z0-9-]+)", RegexOptions.Compiled);
            var defined = new HashSet<string>(StringComparer.Ordinal);
            var read = new List<(string File, string Token)>();
            foreach (var folder in new[] { "src", "examples" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories)
                             .Where(file => file.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
                {
                    var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                    if (parts.Contains("bin") || parts.Contains("obj"))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    foreach (Match match in definition.Matches(text))
                    {
                        defined.Add(match.Groups[1].Value);
                    }

                    foreach (Match match in reference.Matches(text))
                    {
                        read.Add((Path.GetRelativePath(root, file), match.Groups[1].Value));
                    }
                }
            }

            Assert.Contains("--db-accent", defined);
            var undefined = read.Where(item => !defined.Contains(item.Token)).Select(item => $"{item.File}: {item.Token}").Distinct().ToList();
            Assert.True(undefined.Count == 0, "Theme tokens read but defined nowhere:\n" + string.Join("\n", undefined));
        }

        [Fact]
        public void The_scan_catches_a_fallback_colour_and_passes_a_bare_token()
        {
            Assert.Matches(HardFallback, "border-color: var(--db-accent, #5B63D3);");
            Assert.Matches(HardFallback, "background: var(--db-surface-2, rgba(91,99,211,0.08));");
            Assert.DoesNotMatch(HardFallback, "border-color: var(--db-accent);");
            Assert.DoesNotMatch(HardFallback, "gap: var(--db-gap, 1rem);");
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
