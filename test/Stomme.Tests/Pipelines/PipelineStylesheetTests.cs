using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the Pipelines module's stylesheet is linked only by a page that DECLARES it (a component's <c>Resources</c>), not
    /// whenever the module is on the page. Found driving run reviews on a scratch host (2026-10-07): the runs list, the pipelines
    /// list and a run's page used its classes (the tag chips, run data, the reviews) but only the editor declared it, so each of
    /// them opened directly rendered its chips as plain buttons run together - styled only when the editor had been opened first
    /// in the same session. <see cref="AdminMarkupIsStyledTests"/> counts every module stylesheet as defined, so it cannot see
    /// this; this gate does: a Pipelines page using a class the stylesheet defines declares the stylesheet.
    /// </summary>
    public sealed class PipelineStylesheetTests
    {
        private const string Stylesheet = "Modules/Pipelines/pipelines-editor.css";

        [Fact]
        public void Every_pipelines_page_using_the_module_stylesheet_declares_it()
        {
            var root = FindRepoRoot();
            var defined = ModuleClasses(File.ReadAllText(Path.Combine(root, "src", "Stomme.Server", "wwwroot", "Modules", "Pipelines", "pipelines-editor.css")));

            // The instrument, both directions: a page using a class without declaring the sheet is caught, one declaring it is not.
            Assert.Contains("db-pipeline-runs-data", defined);
            Assert.NotEmpty(Undeclared("@inherits ModuleBase\n<div class=\"db-pipeline-runs-data\"></div>", defined));
            Assert.Empty(Undeclared($"@inherits ModuleBase\n<div class=\"db-pipeline-runs-data\"></div>\nUrl = \"{Stylesheet}\"", defined));
            Assert.Empty(Undeclared("<div class=\"db-pipeline-runs-data\"></div>", defined)); // a part of a page, not a page

            var pages = Directory.EnumerateFiles(Path.Combine(root, "src", "Stomme.Client", "Modules", "Pipelines"), "*.razor")
                .ToDictionary(Path.GetFileName, File.ReadAllText);
            var styled = pages.Where(page => Used(page.Value, defined).Count > 0 && IsPage(page.Value)).Select(page => page.Key).ToList();
            Assert.True(styled.Count >= 4, $"Only {styled.Count} pages use the stylesheet - the scan found too little to be trusted.");

            var missing = pages.Select(page => (page.Key, Classes: Undeclared(page.Value, defined))).Where(page => page.Classes.Count > 0).ToList();
            Assert.True(missing.Count == 0,
                "Pipelines pages use the module stylesheet's classes without declaring it, so opened directly they render unstyled:\n  " +
                string.Join("\n  ", missing.Select(page => $"{page.Key}: {string.Join(", ", page.Classes)}")) +
                $"\nDeclare it in the page's Resources: new Resource {{ ResourceType = ResourceType.Stylesheet, Url = \"{Stylesheet}\" }}.");
        }

        // BL-229 - found driving the right-click menu (2026-10-08): the diagram's box, overflow:hidden by the library, was scrolled
        // 313px sideways by the browser revealing a focused button at the canvas's edge, and from then on every step and note was
        // drawn 313px from where the pointer put it - a note added by the menu landed far from the click. The module's sheet
        // clips the box instead, which no scrolling gets past.
        [Fact]
        public void The_canvas_box_is_clipped_so_nothing_scrolls_it_out_of_step_with_the_pointer()
        {
            var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Stomme.Server", "wwwroot", "Modules", "Pipelines", "pipelines-editor.css"));
            Assert.Matches(@"\.db-pipeline-editor__canvas \.diagram-canvas \{[^}]*overflow:\s*clip", css);
        }

        // The classes the module's own sheet defines (its db-pipeline… names; the shared db-btn and db-input are app.css's).
        private static HashSet<string> ModuleClasses(string css) =>
            Regex.Matches(css, @"\.(db-pipelines?[_a-zA-Z0-9-]*)").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        private static bool IsPage(string razor) => razor.Contains("@inherits ModuleBase", StringComparison.Ordinal);

        private static List<string> Used(string razor, HashSet<string> defined) =>
            defined.Where(name => Regex.IsMatch(razor, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])")).OrderBy(name => name, StringComparer.Ordinal).ToList();

        private static List<string> Undeclared(string razor, HashSet<string> defined) =>
            IsPage(razor) && !razor.Contains($"Url = \"{Stylesheet}\"", StringComparison.Ordinal) ? Used(razor, defined) : new List<string>();

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
