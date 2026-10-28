using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-225 (found on UAT, 2026-10-05): in the pipeline editor, typing a Log step's message and then clicking another step
    /// on the canvas saved the text to the step clicked. A field committed on <c>change</c>, which the browser fires on blur -
    /// after the canvas's mousedown had already selected the other step and re-rendered the inspector for it, and with an
    /// unchanged rendered value Blazor leaves the typed text in the box. Every text field of the inspector bound to the
    /// selected step commits on <c>input</c> instead; a checkbox, a radio button or a select commits on change, which fires as it
    /// is chosen (a radio joined on 2026-10-08 with a sticky note's colours: its change fires on the click itself, no blur after).
    /// </summary>
    public sealed class PipelineInspectorCommitTests
    {
        private static readonly Regex Field = new(@"<(input|textarea)\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        [Fact]
        public void Every_text_field_bound_to_the_selected_step_commits_on_input()
        {
            var editor = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules", "Pipelines", "Editor.razor"));
            var fields = Field.Matches(editor)
                .Select(match => match.Value)
                .Where(tag => tag.Contains("_selected", StringComparison.Ordinal) && !tag.Contains("type=\"checkbox\"", StringComparison.Ordinal) && !tag.Contains("type=\"radio\"", StringComparison.Ordinal))
                .ToList();

            Assert.True(fields.Count >= 3, $"Expected the step name, the multi-line and the single-line input fields; found {fields.Count}.");
            var onChange = fields.Where(tag => !tag.Contains("@oninput=", StringComparison.Ordinal) || tag.Contains("@onchange=", StringComparison.Ordinal)).ToList();
            Assert.True(onChange.Count == 0, "These inspector fields commit on change, so a click on another step writes into it: " + string.Join(" | ", onChange));
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
