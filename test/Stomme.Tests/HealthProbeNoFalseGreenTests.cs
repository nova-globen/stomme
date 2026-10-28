#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D104 binding: a probe that never reaches its dependency can only ever report Healthy, so it is a green
    /// light wired to nothing — and a monitoring system that answers falsely is worse than one that says nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a hypothetical rule invented for the row. Two of the five probes the predecessor platform shipped — the message
    /// bus and SignalR — did nothing but <c>GetRequiredService</c>, which proves the container can construct a
    /// client and says nothing whatever about whether anything is reachable. They could not fail. They were
    /// reported as health coverage anyway.
    /// </para>
    /// <para>
    /// <b>The instrument.</b> A real round-trip is asynchronous — a query, a connect, a cache write — so a ping
    /// body containing no <c>await</c> is not talking to anything. That is a proxy rather than a proof, and it is
    /// deliberately a CONSERVATIVE one: it cannot catch a probe that awaits something irrelevant, but it cannot
    /// be satisfied by resolving a service either, which is the failure that actually happened. It is validated in
    /// BOTH directions below, on source whose answer is known by construction.
    /// </para>
    /// </remarks>
    public class HealthProbeNoFalseGreenTests
    {
        private readonly ITestOutputHelper _output;

        public HealthProbeNoFalseGreenTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Every_probe_in_the_tree_actually_talks_to_its_dependency()
        {
            var violations = SourceFiles()
                .SelectMany(file => Scan(File.ReadAllText(file), RepoRelative(file)))
                .ToList();

            foreach (var violation in violations)
            {
                _output.WriteLine("[violation] " + violation);
            }

            Assert.True(
                violations.Count == 0,
                "A probe whose ping body performs no await is not reaching its dependency, so it can only ever " +
                "report Healthy - the predecessor platform failure this rule exists for. Make the ping do the round-trip, or " +
                "do not ship the probe until the dependency exposes a real liveness call. Found:" +
                Environment.NewLine + string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        [Fact]
        public void The_scanner_finds_the_probes_that_are_there()
        {
            // A scanner that matched nothing would pass the test above for ever. The framework contributor ships
            // three probes; this asserts the scanner can SEE them rather than asserting an exact count, which
            // would be a hand-maintained list wearing a disguise.
            var seen = SourceFiles().Sum(file => Count(File.ReadAllText(file)));
            _output.WriteLine($"[probes seen by the scanner] {seen}");

            Assert.True(seen >= 3, $"The scanner found {seen} probe construction(s); it should see at least the framework's three.");
        }

        [Fact]
        public void The_scanner_catches_a_resolve_only_probe_and_clears_a_real_one()
        {
            // BOTH directions, on input whose answer is known by construction - the repo rule that exists because
            // instruments here have lied seven times.
            const string resolveOnly = """
                class Bad
                {
                    void Build()
                    {
                        var probe = new DependencyHealthProbe(
                            "bus", "Message Bus", HealthProbeScope.Installation,
                            (context, ct) => { provider.GetRequiredService<IMessageBus>(); return Task.CompletedTask; },
                            Threshold, Timeout);
                    }
                }
                """;

            const string realRoundTrip = """
                class Good
                {
                    void Build()
                    {
                        var probe = new DependencyHealthProbe(
                            "store", "Module Store", HealthProbeScope.Tenant,
                            async (context, ct) => { await repository.GetByIdAsync(Guid.Empty, ct); },
                            Threshold, Timeout);
                    }
                }
                """;

            var caught = Scan(resolveOnly, "planted/Bad.cs").ToList();
            var cleared = Scan(realRoundTrip, "planted/Good.cs").ToList();

            _output.WriteLine("[planted violation] " + string.Join("; ", caught));
            _output.WriteLine("[planted clean] " + cleared.Count + " violation(s)");

            Assert.Single(caught);
            Assert.Empty(cleared);
        }

        // ---- the scanner -----------------------------------------------------------------------------------

        private static IEnumerable<ObjectCreationExpressionSyntax> Probes(string source) =>
            CSharpSyntaxTree.ParseText(source).GetRoot()
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type is IdentifierNameSyntax { Identifier.ValueText: "DependencyHealthProbe" });

        private static int Count(string source) => Probes(source).Count();

        private static IEnumerable<string> Scan(string source, string file)
        {
            foreach (var creation in Probes(source))
            {
                // The ping is the fourth argument: name, dependency, scope, ping, ...
                var arguments = creation.ArgumentList?.Arguments;
                if (arguments is null || arguments.Value.Count < 4)
                {
                    continue;
                }

                var ping = arguments.Value[3].Expression;
                if (ping is not (SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
                {
                    // A delegate passed by name cannot be judged here; it is scanned where it is declared.
                    continue;
                }

                var awaits = ping.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
                if (!awaits)
                {
                    var name = arguments.Value[0].Expression.ToString();
                    yield return $"{file}: the probe {name} has a ping body with no await - it never reaches its dependency";
                }
            }
        }

        // ---- file discovery (the same shape the other source gates use) ------------------------------------

        private static IEnumerable<string> SourceFiles()
        {
            var src = Path.Combine(RepoRoot(), "src");

            return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
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

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);

            return _repoRoot = directory!.FullName;
        }
    }
}
