#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Stomme.Help;
using Stomme.Infrastructure;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// Every literal <c>Label HelpTopic="…"</c> in the tree names a topic an in-tree contributor SHIPS.
    /// </summary>
    /// <remarks>
    /// A label naming an anchor nobody contributes renders as a plain label - silently, which is the correct runtime
    /// answer and the wrong build answer: in THIS repository it is a typo or a forgotten topic. An <c>@</c>-interpolated
    /// value is undecidable from source and skipped. The scanner is validated both ways - a planted
    /// <c>zzz-never-contributed</c> is reported - and it has a floor, so a scanner that finds nothing fails.
    /// </remarks>
    public class HelpAnchorCoverageTests
    {
        private static readonly Regex HelpTopicAttribute = new("HelpTopic\\s*=\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);

        private readonly ITestOutputHelper _output;

        public HelpAnchorCoverageTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Every_literal_HelpTopic_names_a_topic_an_in_tree_contributor_ships()
        {
            var uses = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.razor", SearchOption.AllDirectories)
                .SelectMany(file => Uses(File.ReadAllText(file)).Select(anchor => (File: Path.GetFileName(file), Anchor: anchor)))
                .ToList();

            _output.WriteLine(string.Join("\n", uses.Select(use => $"{use.File}: {use.Anchor}")));

            // The floor: Site Settings' render-mode label is the first use. A scanner that finds none is a broken scanner.
            Assert.Contains(uses, use => use.Anchor == "stomme:site.rendermode");

            var missing = Missing(uses.Select(use => use.Anchor));
            Assert.True(missing.Count == 0,
                "a Label names a help anchor that no in-tree contributor ships, so it would silently render as a plain label:\n" +
                string.Join("\n", uses.Where(use => missing.Contains(use.Anchor)).Select(use => $"{use.File}: {use.Anchor}")));
        }

        [Fact]
        public void The_scanner_REPORTS_a_planted_anchor_and_SKIPS_an_interpolated_one()
        {
            var planted = Uses("<Label For=\"x\" HelpTopic=\"zzz-never-contributed\">X</Label>\n<Label HelpTopic=\"@Anchor\">Y</Label>").ToList();
            Assert.Equal(new[] { "zzz-never-contributed" }, planted);
            Assert.Equal(new[] { "zzz-never-contributed" }, Missing(planted));

            // Normalized the way a lookup is: casing and padding do not make a shipped anchor "missing".
            Assert.Empty(Missing(Uses("<Label HelpTopic=\" Stomme:Site.RenderMode \">Z</Label>")));
        }

        /// <summary>Literal <c>HelpTopic</c> values in a Razor source, normalized; an interpolated one is skipped.</summary>
        private static IEnumerable<string> Uses(string razor) =>
            HelpTopicAttribute.Matches(razor)
                .Select(match => match.Groups[1].Value)
                .Where(value => !value.Contains('@'))
                .Select(value => HelpKeys.TryNormalizeAnchor(value, out var anchor) ? anchor : value);

        /// <summary>The anchors that no in-tree contributor ships in ANY locale.</summary>
        private static List<string> Missing(IEnumerable<string> anchors)
        {
            var registry = new HelpTopicContributorRegistry(ConventionContributorDiscovery.Discover<IHelpTopicContributor>()
                .Select(type => (IHelpTopicContributor)Activator.CreateInstance(type)!));
            var shipped = registry.Topics.Select(topic => topic.Contribution.Anchor).ToHashSet(StringComparer.Ordinal);
            return anchors.Where(anchor => !shipped.Contains(anchor)).Distinct(StringComparer.Ordinal).ToList();
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
