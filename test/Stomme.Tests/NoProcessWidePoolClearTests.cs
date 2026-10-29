#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>ClearAllPools()</c> is banned in the test tree, because it is PROCESS-WIDE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a gate rather than a sentence in a document because the sentence already existed and did not
    /// hold. Roughly thirty fixtures called <c>SqliteConnection.ClearAllPools()</c> at teardown, each locally
    /// reasonable, and together they meant that finishing one test class dropped the pooled connections of
    /// every class still running — in a suite whose classes run in parallel collections. Measured: a probe
    /// calling it every 25 ms during a definition PUT produced 3 non-200 in 60, a flake with no bug behind it
    /// and nothing in the failure naming its cause.
    /// </para>
    /// <para>
    /// The replacement is always targeted: <see cref="ScratchSqlite.Release"/> for one database,
    /// <see cref="ScratchSqlite.ReleaseInstall"/> for a scratch install, or a direct
    /// <c>SqliteConnection.ClearPool(connection)</c> in the two test projects that do not reference this one.
    /// Whichever is used, the connection string must be the one the code under test actually used — the pool is
    /// keyed by the RAW string, measured in <see cref="ScratchSqliteReleaseTests"/>.
    /// </para>
    /// <para>
    /// The same ban covers <c>NpgsqlConnection.ClearAllPools()</c>: it is the same hazard with a different
    /// provider, and the PostgreSQL fixtures already clear their own pool by connection string.
    /// </para>
    /// <para>
    /// <b>There is no allowlist, deliberately.</b> Every caller had a local justification and the harm was
    /// entirely in the aggregate, so an allowlist would re-create the problem one entry at a time. If a case
    /// genuinely cannot name its own connection string, that is a design question about the fixture.
    /// </para>
    /// </remarks>
    public class NoProcessWidePoolClearTests
    {
        private readonly ITestOutputHelper _output;

        public NoProcessWidePoolClearTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void No_test_source_file_clears_every_connection_pool()
        {
            var violations = TestSourceFiles()
                .SelectMany(file => Scan(File.ReadAllText(file), RepoRelative(file)))
                .ToList();

            foreach (var violation in violations)
            {
                _output.WriteLine("[violation] " + violation);
            }

            Assert.True(violations.Count == 0,
                "ClearAllPools() is process-wide: it drops the pooled connections of every test class running " +
                "in parallel, not just this fixture's. Release the pool you own instead - ScratchSqlite.Release " +
                "(one database), ScratchSqlite.ReleaseInstall (a scratch install), or ClearPool(connection) - " +
                "and pass the connection string the code actually used. Found:" + Environment.NewLine +
                string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void The_scanner_works_in_both_directions()
        {
            // The house rule: an instrument is validated on input whose answer is known, 0 on clean AND >0 on
            // dirty, before it is trusted. Two of this repository's wrong conclusions came from scanners that
            // were only ever run against the case they were expected to pass.
            const string sqlite = "class C { void M() { SqliteConnection.ClearAllPools(); } }";
            const string qualified = "class C { void M() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } }";
            const string npgsql = "class C { void M() { NpgsqlConnection.ClearAllPools(); } }";
            const string targeted = "class C { void M() { SqliteConnection.ClearPool(connection); } }";
            const string prose = "class C { /* ClearAllPools() is process-wide - never call it */ void M() { } }";
            const string named = "class C { void ClearAllPools() { } }";

            Assert.Single(Scan(sqlite, "planted/Sqlite.cs"));
            Assert.Single(Scan(qualified, "planted/Qualified.cs"));
            Assert.Single(Scan(npgsql, "planted/Npgsql.cs"));
            Assert.Empty(Scan(targeted, "planted/Targeted.cs"));
            Assert.Empty(Scan(prose, "planted/Prose.cs"));
            Assert.Empty(Scan(named, "planted/Declaration.cs"));

            _output.WriteLine("[scanner] fires on a ClearAllPools CALL, qualified or not, SQLite or Npgsql; " +
                              "silent on ClearPool, on a comment, and on a method merely named that");
        }

        /// <summary>A CALL to <c>…ClearAllPools()</c> — not a comment saying so, not a method declared with that name.</summary>
        private static IEnumerable<string> Scan(string source, string file)
        {
            foreach (var invocation in CSharpSyntaxTree.ParseText(source).GetRoot()
                         .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => null,
                };

                if (name != "ClearAllPools")
                {
                    continue;
                }

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return $"{file}({line}): ClearAllPools() clears EVERY pool in the process, not just this fixture's";
            }
        }

        // ---- file discovery (the shape the other source gates use) -----------------------------------------

        private static IEnumerable<string> TestSourceFiles()
        {
            var test = Path.Combine(RepoRoot(), "test");

            return Directory.EnumerateFiles(test, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsUnderDirectory(file, "obj"))
                .Where(file => !IsUnderDirectory(file, "bin"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsUnderDirectory(string file, string segment)
            => RepoRelative(file).Split('/').Contains(segment, StringComparer.OrdinalIgnoreCase);

        private static string RepoRelative(string file)
        {
            var root = RepoRoot();
            var full = Path.GetFullPath(file);

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : full.Replace('\\', '/');
        }

        private static string? _repoRoot;

        private static string RepoRoot()
        {
            if (_repoRoot is not null)
            {
                return _repoRoot;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return _repoRoot = directory!.FullName;
        }
    }
}
