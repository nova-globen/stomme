#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// What an agent building on Stomme knows is the app template - its AGENTS.md, CLAUDE.md, README, docs/ and
    /// .claude/skills/ - and the template ships to everyone on nuget.org. It names no old product, no forge or registry host,
    /// no host of the infrastructure and no broker command. The release dry run checks the same in every packed file;
    /// this catches it at the commit.
    /// </summary>
    public class AgentAssetNamesTests
    {
        /// <summary>
        /// The infrastructure host's address, written once as a plain literal: the public snapshot rewrites that literal to a
        /// placeholder, and the rule and its sample are both built from it, so they stay in step there and the public tree
        /// names the address in no form.
        /// </summary>
        private const string InfraAddress = "192.168.10.20";

        /// <summary>The same rules as the release dry run's names guard (scripts/release-dry-run.ps1).</summary>
        internal static readonly (string Name, Regex Pattern)[] Forbidden =
        {
            ("an old product name", new Regex("d[i]da[ ._-]?(core|bit|deck|bin)", RegexOptions.IgnoreCase)),
            ("a forge or registry host", new Regex(@"forgejo|(git|store)\.d[i]da|dl-internal", RegexOptions.IgnoreCase)),
            ("an infrastructure host", new Regex(@"(runner|docker|store)0\d|ros[t]am|" + Regex.Escape(InfraAddress), RegexOptions.IgnoreCase)),
            ("a broker command", new Regex(@"(^|[^a-z0-9])ng (exec|api|run|ops|actions|put-profile) ", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        };

        [Fact]
        public void The_app_template_names_no_old_product_and_no_infrastructure()
        {
            var template = Path.Combine(RepoRoot(), "src", "Stomme.Application");
            var findings = new List<string>();
            var scanned = 0;
            foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(template, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules") || relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;
                findings.AddRange(Findings(File.ReadAllText(file)).Select(finding => $"{relative}: {finding}"));
            }

            Assert.True(scanned > 50, $"only {scanned} files scanned - the template folder moved?");
            Assert.True(findings.Count == 0, "The app template names what it must not:\n" + string.Join("\n", findings));
        }

        [Theory]
        [InlineData("Restore from https://git.di" + "dabit.com/api/packages/x/index.json", 2)]
        [InlineData("using Di" + "daCore.Shared;", 1)]
        [InlineData("run ng ex" + "ec a-profile -- dotnet test", 1)]
        [InlineData("a host at {address}:5433", 1)]
        [InlineData("built on run" + "ner01, then " + "ROS" + "TAM", 1)]
        [InlineData("Stomme on nuget.org, a site at example.com/shop, authorizing API endpoints", 0)]
        [InlineData("the private network 192.168.0.0/16", 0)]
        public void The_rules_find_what_they_should_and_nothing_else(string text, int expected)
        {
            Assert.Equal(expected, Findings(text.Replace("{address}", InfraAddress)).Count);
        }

        internal static List<string> Findings(string text) =>
            Forbidden.Select(rule => (rule.Name, Match: rule.Pattern.Match(text)))
                .Where(hit => hit.Match.Success)
                .Select(hit => $"{hit.Name} ('{hit.Match.Value.Trim()}')")
                .ToList();

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return directory!.FullName;
        }
    }
}
