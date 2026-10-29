using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// A delete asks first. The framework's own admin pages delete through <c>ActionDialog</c>, which asks before it acts;
    /// twelve buttons across the module pages and the file manager deleted on ONE click - a backup snapshot and its archive, a
    /// pipeline with its versions, a connection with its stored credentials, a licence key (found in BL-94's click-through,
    /// 2026-10-01, for Formats, Connections and Datasets, and swept 2026-10-03). This gate reads every page and control: a
    /// button whose click handler deletes is refused, by name and file.
    /// </summary>
    public sealed class DeleteConfirmationGateTests
    {
        // A <button> whose @onclick calls something named Delete... - the one-click shape this gate refuses.
        internal static readonly Regex OneClickDelete = new(@"<button\b[^>]*@onclick=""[^""]*\bDelete\w*\s*\(?[^""]*""[^>]*>", RegexOptions.Compiled);

        // The confirm button of a confirmation the page already shows, or an edit that deletes nothing stored and Undo takes back -
        // not a one-click delete. Each entry says why; one that no longer matches fails its own guard below.
        private static readonly (string File, string Handler, string Why)[] Confirmed =
        {
            (Path.Combine("src", "Stomme.Client", "Themes", "Controls", "Theme", "ControlPanelInteractive.razor"), "DeletePage", "the Delete button of its own db-admin-modal, opened by ConfirmDelete (Confirm.Page.Delete)"),
            (Path.Combine("src", "Stomme.Client", "Modules", "Pipelines", "Editor.razor"), "DeleteSelection", "takes the selected steps off the canvas: nothing is stored until Save, and Undo (Ctrl+Z) brings them back - the canvas's Delete key, n8n's too (BL-229 increment 3)"),
            (Path.Combine("src", "Stomme.Client", "Modules", "Pipelines", "Canvas", "StepNode.razor"), "Node.Delete", "the button over a step takes that step off the canvas through the same DeleteSelection: nothing is stored until Save, and Undo brings it back - n8n's bin over a node (BL-229)"),
        };

        [Fact]
        public void No_page_or_control_deletes_on_one_click()
        {
            var root = RepositoryRoot();
            var offenders = new[] { Path.Combine("src", "Stomme.Client"), "examples" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .SelectMany(file => OneClickDelete.Matches(File.ReadAllText(file)).Select(match => (File: Path.GetRelativePath(root, file), match.Value)))
                .ToList();
            var stale = Confirmed.Where(entry => !offenders.Any(found => found.File == entry.File && found.Value.Contains(entry.Handler, StringComparison.Ordinal))).ToList();
            offenders = offenders.Where(found => !Confirmed.Any(entry => found.File == entry.File && found.Value.Contains(entry.Handler, StringComparison.Ordinal))).ToList();

            Assert.True(stale.Count == 0, "A confirmed delete the gate allows is gone - remove its entry: " + string.Join("; ", stale.Select(entry => $"{entry.File} {entry.Handler}")));

            Assert.True(offenders.Count == 0, "A delete on one click - put it behind an ActionDialog:" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Select(found => $"{found.File}: {found.Value}")));
        }

        [Theory]
        [InlineData("<button type=\"button\" class=\"db-btn\" @onclick=\"() => DeleteAsync(run)\" disabled=\"@_busy\">Delete</button>", true)]
        [InlineData("<button type=\"button\" class=\"db-btn db-btn-danger\" @onclick=\"DeleteFile\">x</button>", true)]
        [InlineData("<button type=\"button\" class=\"db-btn\" @onclick=\"@(() => EditAsync(item.Id))\">Edit</button>", false)]
        [InlineData("<ActionDialog Header=\"Delete Run\" Message=\"m\" Action=\"Delete\" OnClick=\"@(async () => { await DeleteAsync(run); StateHasChanged(); })\" />", false)]
        public void The_scanner_finds_a_one_click_delete_and_nothing_else(string markup, bool flagged) =>
            Assert.Equal(flagged, OneClickDelete.IsMatch(markup));

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
