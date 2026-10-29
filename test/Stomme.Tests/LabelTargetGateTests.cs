using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-22's depth-2 pass - a field's label names the field. <c>&lt;Label For="x"&gt;</c> renders a
    /// <c>&lt;label for="x"&gt;</c>, which tells a screen reader what the field is and focuses it when clicked - only when an
    /// element with that id exists. Thirty-eight did not, in twenty-three pages and controls: inputs with no id, ids misspelt
    /// (<c>softfield</c>, <c>SettingValue</c>), two selects sharing one id so the second label named the first field, and
    /// components whose input took no id. This gate reads every page and control: a label's target is an <c>id</c> in the
    /// same file, or the <c>Id</c> of a component there (FileManager, AutoComplete and InputList put it on their element).
    /// </summary>
    public sealed class LabelTargetGateTests
    {
        private static readonly Regex LabelFor = new(@"<Label\b[^>]*\bFor=""(?<id>[^""@]+)""", RegexOptions.Compiled);

        [Fact]
        public void Every_label_names_an_element_in_its_file()
        {
            var root = RepositoryRoot();
            var offenders = new[] { Path.Combine("src", "Stomme.Client"), "examples" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .SelectMany(file => Dangling(File.ReadAllText(file)).Select(id => $"{Path.GetRelativePath(root, file)}: <Label For=\"{id}\">"))
                .ToList();

            Assert.True(offenders.Count == 0, "A label whose For names no element in its file - give the field that id:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Theory]
        [InlineData("<Label For=\"name\">Name:</Label><input id=\"name\" class=\"db-input\" />", 0)]
        [InlineData("<Label For=\"logo\">Logo:</Label><FileManager Id=\"logo\" FileId=\"@_logo\" />", 0)]
        [InlineData("<Label For=\"settingValue\">Value:</Label><input id=\"SettingValue\" />", 1)]
        [InlineData("<Label For=\"sortfield\">Sort:</Label><select id=\"softfield\"></select>", 1)]
        [InlineData("<Label For=\"email\">Email:</Label><input type=\"text\" class=\"db-input\" />", 1)]
        [InlineData("<Label For=\"@_dynamic\">Dynamic:</Label>", 0)]
        public void The_scanner_finds_a_label_that_names_nothing_and_only_that(string markup, int dangling) =>
            Assert.Equal(dangling, Dangling(markup).Count());

        private static System.Collections.Generic.IEnumerable<string> Dangling(string razor) =>
            LabelFor.Matches(razor)
                .Select(match => match.Groups["id"].Value)
                .Where(id => !Regex.IsMatch(razor, @"\b[iI]d=""" + Regex.Escape(id) + @""""));

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
