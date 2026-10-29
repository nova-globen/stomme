#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// No in-tree chart spec puts a <c>"$column"</c> token inside ECharts' <c>encode</c>, which a manual walk of the demo board found was drawing nothing. The
    /// renderer replaces a token with the column's VALUES, and <c>encode</c> takes dimension NAMES - so the demo board's "Sales by
    /// region" series held no data and drew nothing but an axis, on the Dashboards page and on the landing page alike, while every
    /// suite was green (none looks at the drawn bars). The columns belong in a <c>dataset</c> (or a series' <c>data</c>), and
    /// <c>encode</c> names the dataset's dimensions. A scan of the source, validated both ways.
    /// </summary>
    public sealed class ChartSpecTokenPlacementTests
    {
        private static readonly Regex TokenInEncode = new(@"""encode""\s*:\s*\{[^{}]*""\$", RegexOptions.CultureInvariant);

        [Fact]
        public void No_in_tree_chart_spec_puts_a_column_token_inside_encode()
        {
            var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => TokenInEncode.IsMatch(File.ReadAllText(path)))
                .ToList();

            Assert.True(offenders.Count == 0, "A chart spec puts a \"$column\" token inside encode: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_scan_catches_the_shape_it_exists_for_and_passes_the_idiom()
        {
            // The demo board's first spec, verbatim: dirty.
            Assert.Matches(TokenInEncode, """{ "series": [ { "type": "bar", "encode": { "x": "$region", "y": "$amount" } } ] }""");
            // The idiom: a dataset holds the tokens, encode names its dimensions - clean.
            Assert.DoesNotMatch(TokenInEncode, """{ "dataset": { "source": { "region": "$region" } }, "series": [ { "type": "bar", "encode": { "x": "region" } } ] }""");
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        }
    }
}
