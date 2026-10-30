using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// What a screen reader announces has a name (BL-267, BL-268, reported by axe on the website). An image says what it is
    /// (<c>alt</c>, empty for a decorative one) - the Login page's help icons had none. A control whose only label is an icon -
    /// a <c>&lt;label for&gt;</c> holding no text, as the scriptless menu toggle's - carries its own <c>aria-label</c> or
    /// <c>aria-labelledby</c>: an <c>aria-label</c> on the LABEL names the label, not the control, and the checkbox was
    /// announced with no name on every statically rendered page. This gate reads every page, control and theme.
    /// </summary>
    public sealed class AccessibleNameGateTests
    {
        private static readonly Regex Image = new(@"<img\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The HTML label only (lower case): the Label component (<Label ResourceKey=...></Label>) takes its text from a resource.
        private static readonly Regex IconOnlyLabel = new(@"<label\b[^>]*\bfor=""(?<id>[^""@]+)""[^>]*>(?<body>.*?)</label>", RegexOptions.Compiled | RegexOptions.Singleline);

        [Fact]
        public void Every_image_has_alt_and_every_icon_labelled_control_has_a_name()
        {
            var root = RepositoryRoot();
            var folders = new[] { Path.Combine("src", "Stomme.Client"), Path.Combine("src", "Stomme.Server", "Components"), Path.Combine("src", "Stomme.Server", "Modules") };
            var scanned = 0;
            var offenders = new List<string>();
            foreach (var file in folders.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.razor", SearchOption.AllDirectories)))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                scanned++;
                offenders.AddRange(Findings(File.ReadAllText(file)).Select(finding => $"{Path.GetRelativePath(root, file)}: {finding}"));
            }

            Assert.True(scanned > 200, $"only {scanned} files scanned - a folder moved?");
            Assert.True(offenders.Count == 0, "Something a screen reader announces has no name:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Theory]
        [InlineData("<img src=\"images/help.png\" />", 1)]
        [InlineData("<img src=\"images/help.png\" alt=\"@HelpText\" />", 0)]
        [InlineData("<img src=\"divider.png\" alt=\"\" />", 0)]
        [InlineData("<input type=\"checkbox\" id=\"MenuToggle\" /><label for=\"MenuToggle\" aria-label=\"Toggle\"><span class=\"icon\"></span></label>", 1)]
        [InlineData("<input type=\"checkbox\" id=\"MenuToggle\" aria-label=\"Toggle Navigation\" /><label for=\"MenuToggle\"><span class=\"icon\"></span></label>", 0)]
        [InlineData("<button id=\"MenuToggle\" aria-label=\"Toggle\"></button><input type=\"checkbox\" id=\"MenuToggle\" /><label for=\"MenuToggle\"><span></span></label>", 1)]
        [InlineData("<input id=\"remember\" type=\"checkbox\" /><label for=\"remember\">Remember me</label>", 0)]
        [InlineData("<label for=\"name\">@Localizer[\"Name\"]</label><input id=\"name\" />", 0)]
        [InlineData("<Label For=\"email\" ResourceKey=\"Email\"></Label><input id=\"email\" />", 0)]
        public void The_rules_find_what_they_should_and_nothing_else(string markup, int expected) =>
            Assert.Equal(expected, Findings(markup).Count);

        private static List<string> Findings(string razor)
        {
            var findings = Image.Matches(razor)
                .Where(image => !Regex.IsMatch(image.Value, @"\balt=", RegexOptions.IgnoreCase))
                .Select(image => $"an image with no alt: {image.Value}")
                .ToList();

            foreach (Match label in IconOnlyLabel.Matches(razor))
            {
                // A label holding no text (tags and whitespace only; Razor expressions count as text).
                if (Regex.Replace(label.Groups["body"].Value, "<[^>]+>", string.Empty).Trim().Length > 0)
                {
                    continue;
                }

                // Every control with that id: a component renders one per branch (an interactive button, a scriptless checkbox).
                var id = Regex.Escape(label.Groups["id"].Value);
                foreach (Match control in Regex.Matches(razor, @"<(input|select|textarea|button)\b[^>]*\bid=""" + id + @"""[^>]*>", RegexOptions.IgnoreCase))
                {
                    if (!Regex.IsMatch(control.Value, @"\baria-label(ledby)?=", RegexOptions.IgnoreCase))
                    {
                        findings.Add($"a control whose label is only an icon, with no aria-label: {control.Value}");
                    }
                }
            }

            return findings;
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
