using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// An admin module may not use a CSS class that no ALWAYS-LOADED stylesheet defines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shared component layer (<c>--db-*</c>) ships in <c>Stomme.Server/wwwroot/css/app.css</c>, which is
    /// linked on every request. A theme's <c>Theme.css</c> is linked ONLY while that theme is active, and
    /// Bootstrap arrives ONLY under <c>BlazorTheme</c> (the host's <c>lib/bootstrap</c>, which this index deliberately leaves out). So an admin module that borrows a Bootstrap
    /// utility, or invents a class nobody defined, renders **unstyled on the default theme** — silently. The
    /// page still renders; it just looks wrong, and only on themes the author was not looking at. That is
    /// exactly how an earlier breadcrumb defect and this sweep's eleven classes survived unnoticed.
    /// </para>
    /// <para>
    /// The first run of the equivalent script over-reported, because it did not index the per-module
    /// <c>Module.css</c> files served from the Server wwwroot — a scan is only as honest as the sheets it
    /// indexes, so those are included here too. The companion script is
    /// <c>scripts/scan-unstyled-classes.ps1</c>.
    /// </para>
    /// <para>
    /// Razor-interpolated class attributes (containing <c>@</c>) are skipped: their effective class set is
    /// undecidable from source, and guessing would make this gate lie in the direction that matters least.
    /// </para>
    /// </remarks>
    public class AdminMarkupIsStyledTests
    {
        private readonly ITestOutputHelper _output;

        public AdminMarkupIsStyledTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Every_literal_class_in_an_admin_module_is_defined_in_an_always_loaded_stylesheet()
        {
            var root = FindRepoRoot();
            var defined = LoadDefinedClasses(root);

            // Instrument self-test, BOTH directions. A gate that cannot fail is not evidence.
            Assert.True(defined.Contains("db-btn"),
                "Self-test failed (clean direction): 'db-btn' missing from the CSS index — the index is broken.");
            Assert.False(defined.Contains("zzz-sentinel-never-defined"),
                "Self-test failed (dirty direction): a sentinel class matched — the index over-matches.");

            var usage = CollectAdminClassUsage(root);
            Assert.True(usage.Count > 40,
                $"Only {usage.Count} classes discovered — the admin module scan found too little to be trusted.");

            var unstyled = usage.Keys.Where(c => !defined.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
            foreach (var c in unstyled)
            {
                _output.WriteLine($"UNSTYLED {c} -> {string.Join(", ", usage[c])}");
            }

            Assert.True(unstyled.Count == 0,
                "Admin markup uses classes that no always-loaded stylesheet defines, so they render unstyled " +
                "on the default theme:\n  " +
                string.Join("\n  ", unstyled.Select(c => $"{c}  ({string.Join(", ", usage[c])})")) +
                "\nDefine them in app.css, or drop them from the markup if a defined sibling already styles the element.");
        }

        private static HashSet<string> LoadDefinedClasses(string root)
        {
            var css = File.ReadAllText(Path.Combine(root, "src", "Stomme.Server", "wwwroot", "css", "app.css"));

            // Per-module stylesheets are loaded whenever their module is on the page, so they count as defined.
            var modulesDir = Path.Combine(root, "src", "Stomme.Server", "wwwroot", "Modules");
            if (Directory.Exists(modulesDir))
            {
                foreach (var file in Directory.EnumerateFiles(modulesDir, "*.css", SearchOption.AllDirectories))
                {
                    css += "\n" + File.ReadAllText(file);
                }
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(css, @"\.(-?[_a-zA-Z][_a-zA-Z0-9-]*)"))
            {
                set.Add(m.Groups[1].Value);
            }
            return set;
        }

        private static SortedDictionary<string, List<string>> CollectAdminClassUsage(string root)
        {
            var adminRoot = Path.Combine(root, "src", "Stomme.Client", "Modules", "Admin");
            var usage = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(adminRoot, "*.razor", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var where = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file);

                foreach (Match m in Regex.Matches(text, "class\\s*=\\s*\"([^\"]*)\""))
                {
                    var value = m.Groups[1].Value;
                    if (value.Contains('@'))
                    {
                        continue; // interpolated — effective classes are undecidable from source
                    }

                    foreach (var token in value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!usage.TryGetValue(token, out var list))
                        {
                            list = new List<string>();
                            usage[token] = list;
                        }
                        if (!list.Contains(where))
                        {
                            list.Add(where);
                        }
                    }
                }
            }

            return usage;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stomme.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
