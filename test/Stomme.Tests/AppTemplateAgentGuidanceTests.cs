using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-239 - the agent guidance an app made from the template carries (its AGENTS.md, CLAUDE.md, docs/ and
    /// .claude/skills/) is held to what the packages give it. The reader is an AI agent with no Stomme source, so a
    /// broken link, a namespace no package exports, a skill its tool cannot load or a pointer into this repository sends
    /// it down a dead end - silently, since nothing compiles a markdown file. Each rule here is a check that would
    /// otherwise be a sentence.
    /// </summary>
    public sealed class AppTemplateAgentGuidanceTests(ITestOutputHelper output)
    {
        private static readonly Regex Link = new(@"\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);

        // A fenced block, its body as group 1 (text normalised to \n first).
        private static readonly Regex Fence = new(@"^[ \t]*```[^\n]*\n(.*?)^[ \t]*```[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

        private static readonly Regex UsingLine = new(@"^\s*@?using\s+(?:static\s+)?(Stomme(?:\.\w+)+)\s*;?\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        // What only this repository knows: a backlog id, a decision number, a path into its source or tests.
        private static readonly Regex Internal = new(@"\bBL-\d+\b|\bD\d{2,3}\b|\b(?:src|test)/Stomme\.|\bexamples/Modules/", RegexOptions.Compiled);

        private static readonly Regex Blocking = new(@"\.Result\b|\.Wait\(\)|\.GetAwaiter\(\)\.GetResult\(\)", RegexOptions.Compiled);

        [Fact]
        public void The_template_carries_its_agent_guidance()
        {
            var root = TemplateRoot();
            Assert.True(File.Exists(Path.Combine(root, "AGENTS.md")), "the template has no AGENTS.md");
            Assert.True(File.Exists(Path.Combine(root, "CLAUDE.md")), "the template has no CLAUDE.md");
            Assert.True(File.Exists(Path.Combine(root, "docs", "README.md")), "the template has no docs/README.md");

            // Floors, not counts: a count kept by hand is a list wearing a disguise.
            Assert.True(Skills(root).Count >= 30, $"only {Skills(root).Count} skills under .claude/skills");
            Assert.True(Docs(root).Count(path => path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) >= 35, "too few reference docs under docs/");
        }

        [Fact]
        public void Every_skill_names_itself_and_says_when_to_use_it()
        {
            var problems = new List<string>();
            foreach (var skill in Skills(TemplateRoot()))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(skill)!);
                var text = File.ReadAllText(skill).Replace("\r\n", "\n", StringComparison.Ordinal);
                var match = Regex.Match(text, @"\A---\n(.*?)\n---\n", RegexOptions.Singleline);
                if (!match.Success)
                {
                    problems.Add($"{folder}: no frontmatter at the top");
                    continue;
                }

                var name = Regex.Match(match.Groups[1].Value, @"^name:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
                var description = Regex.Match(match.Groups[1].Value, @"^description:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
                if (name != folder)
                {
                    problems.Add($"{folder}: name '{name}' is not its folder's");
                }

                if (!description.TrimStart('"', '\'').StartsWith("Use when", StringComparison.Ordinal))
                {
                    problems.Add($"{folder}: its description does not start 'Use when'");
                }

                if (!folder.StartsWith("stomme-", StringComparison.Ordinal))
                {
                    problems.Add($"{folder}: a skill of the framework's is named stomme-...");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_relative_link_reaches_a_file()
        {
            var root = TemplateRoot();
            var problems = new List<string>();
            foreach (var file in Docs(root))
            {
                foreach (Match link in Link.Matches(OutsideCode(File.ReadAllText(file))))
                {
                    var target = link.Groups[1].Value;
                    if (target.StartsWith('#') || Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase))
                    {
                        continue; // an anchor in the same file, or a URL
                    }

                    var path = Uri.UnescapeDataString(target.Split('#')[0]);
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{Relative(root, file)}: '{target}' leaves the app");
                    }
                    else if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    {
                        problems.Add($"{Relative(root, file)}: '{target}' reaches nothing");
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_Stomme_namespace_a_sample_uses_is_one_the_packages_export()
        {
            var exported = ExportedNamespaces();
            output.WriteLine($"{exported.Count} Stomme namespaces exported by the packages' assemblies");
            Assert.Contains("Stomme.Shared", exported); // the instrument sees the packages at all

            var root = TemplateRoot();
            var problems = new List<string>();
            foreach (var file in Docs(root))
            {
                foreach (var block in CodeBlocks(File.ReadAllText(file)))
                {
                    foreach (Match line in UsingLine.Matches(block))
                    {
                        var name = line.Groups[1].Value;
                        // The app's own namespaces (the template engine renames them) and those a sample declares itself.
                        if (name.StartsWith("Stomme.Application", StringComparison.Ordinal) || Regex.IsMatch(block, @"namespace\s+" + Regex.Escape(name) + @"\b"))
                        {
                            continue;
                        }

                        if (!exported.Contains(name))
                        {
                            problems.Add($"{Relative(root, file)}: 'using {name}' - no package exports a public type in it");
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void The_guidance_names_nothing_only_this_repository_knows_and_no_sample_blocks_on_async()
        {
            var root = TemplateRoot();
            var problems = new List<string>();
            foreach (var file in Docs(root))
            {
                var text = File.ReadAllText(file);
                foreach (Match hit in Internal.Matches(text))
                {
                    problems.Add($"{Relative(root, file)}: '{hit.Value}' is this repository's, not the app's");
                }

                foreach (var block in CodeBlocks(text))
                {
                    foreach (Match hit in Blocking.Matches(block))
                    {
                        problems.Add($"{Relative(root, file)}: a sample blocks on async code ('{hit.Value}')");
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        // The six core skills are generated from scripts/app-guidance/templates and the canonical Notes module, which was
        // built, run and driven in an app - edited by hand, their samples drift from the code that works.
        [Fact]
        public void The_core_skills_are_their_templates_with_the_canonical_modules_code()
        {
            var guidance = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(TemplateRoot())))!, "scripts", "app-guidance");
            var templates = Directory.GetFiles(Path.Combine(guidance, "templates"), "*.md");
            Assert.True(templates.Length >= 6, $"only {templates.Length} core-skill templates");
            var drifted = new List<string>();
            foreach (var template in templates)
            {
                var text = Regex.Replace(File.ReadAllText(template).Replace("\r\n", "\n", StringComparison.Ordinal), @"\{\{FILE:([^}|]+)(?:\|(\d+))?\}\}", placeholder =>
                {
                    var pad = new string(' ', placeholder.Groups[2].Success ? int.Parse(placeholder.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
                    var body = File.ReadAllText(Path.Combine(guidance, "canonical", placeholder.Groups[1].Value)).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
                    return string.Join("\n", body.Split('\n').Select(line => line.Length > 0 ? pad + line : string.Empty));
                });
                var skill = Path.Combine(TemplateRoot(), ".claude", "skills", Path.GetFileNameWithoutExtension(template), "SKILL.md");
                if (!File.Exists(skill) || File.ReadAllText(skill).Replace("\r\n", "\n", StringComparison.Ordinal) != text)
                {
                    drifted.Add(Path.GetFileNameWithoutExtension(template));
                }
            }

            Assert.True(drifted.Count == 0, $"drifted from their templates (run pwsh scripts/app-guidance/generate-core-skills.ps1 after changing a template or the canonical module): {string.Join(", ", drifted)}");
        }

        private static List<string> Skills(string root) =>
            Directory.Exists(Path.Combine(root, ".claude", "skills"))
                ? Directory.GetFiles(Path.Combine(root, ".claude", "skills"), "SKILL.md", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToList()
                : new List<string>();

        // Every markdown file of the guidance: AGENTS.md, CLAUDE.md, docs/ and the skills - not the template's README,
        // which is the package's own description.
        private static List<string> Docs(string root)
        {
            var files = new List<string>();
            foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
            {
                if (File.Exists(Path.Combine(root, name)))
                {
                    files.Add(Path.Combine(root, name));
                }
            }

            foreach (var folder in new[] { "docs", Path.Combine(".claude", "skills") })
            {
                if (Directory.Exists(Path.Combine(root, folder)))
                {
                    files.AddRange(Directory.GetFiles(Path.Combine(root, folder), "*.md", SearchOption.AllDirectories));
                }
            }

            return files.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> CodeBlocks(string text) =>
            Fence.Matches(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Select(block => block.Groups[1].Value);

        private static string OutsideCode(string text) =>
            Regex.Replace(Fence.Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty), "`[^`\n]*`", string.Empty);

        // The Stomme namespaces holding a public type, across Stomme.Server and every Stomme assembly it references -
        // what an app reaches through its one package reference.
        private static HashSet<string> ExportedNamespaces()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<Assembly>(new[] { typeof(Stomme.Server.Program).Assembly, typeof(Stomme.Shared.Constants).Assembly });
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var assembly = pending.Dequeue();
                if (!seen.Add(assembly.GetName().Name!))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetExportedTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type is not null).ToArray()!;
                }

                foreach (var ns in types.Select(type => type.Namespace).Where(ns => ns is not null && ns.StartsWith("Stomme", StringComparison.Ordinal)))
                {
                    namespaces.Add(ns!);
                }

                foreach (var reference in assembly.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("Stomme", StringComparison.Ordinal)))
                {
                    try
                    {
                        pending.Enqueue(Assembly.Load(reference));
                    }
                    catch (FileNotFoundException)
                    {
                        // not shipped beside the test - not something an app gets either
                    }
                }
            }

            return namespaces;
        }

        private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

        private static string TemplateRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stomme.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "Stomme.Application") + Path.DirectorySeparatorChar;
        }
    }
}
