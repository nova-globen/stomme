#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>Uri.TryCreate(value, UriKind.Absolute, ...)</c> is banned in the product source: on Linux it accepts a bare path.
    /// </summary>
    /// <remarks>
    /// On Linux and macOS .NET reads <c>/relative/only</c> as the absolute <c>file:///relative/only</c>; Windows refuses it.
    /// The product is hosted on Linux, and its first Linux test run found an absolute-URL field accepting a path
    /// (<c>ConnectorContractTests</c>). Response <c>Location</c> headers and <c>UriKind.RelativeOrAbsolute</c> are NOT
    /// affected (measured in the .NET 10 SDK container). <see cref="Utilities.TryCreateAbsoluteUri"/> is the call; this
    /// gate keeps the bare one from coming back. Its one allowed site is the helper itself.
    /// </remarks>
    public class AbsoluteUriGateTests
    {
        private const string Helper = "src/Stomme.Shared/Shared/Utilities.cs";

        private readonly ITestOutputHelper _output;

        public AbsoluteUriGateTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("/relative/only", false)]
        [InlineData("/", false)]
        [InlineData("relative/only", false)]
        [InlineData("https://example.com/path", true)]
        [InlineData("http://localhost:5080/path", true)]
        [InlineData("amqp://broker:5672", true)]
        [InlineData("file:///var/data/x", true)]
        public void The_helper_refuses_a_bare_path_on_every_platform(string value, bool absolute)
        {
            Assert.Equal(absolute, Utilities.TryCreateAbsoluteUri(value, out var uri));
            Assert.Equal(absolute, uri is not null);
        }

        [Fact]
        public void No_product_source_file_calls_the_bare_absolute_TryCreate()
        {
            var violations = ProductSourceFiles()
                .Where(file => RepoRelative(file) != Helper)
                .SelectMany(file => Scan(File.ReadAllText(file), RepoRelative(file)))
                .ToList();
            foreach (var violation in violations)
            {
                _output.WriteLine("[violation] " + violation);
            }

            Assert.True(violations.Count == 0,
                "Uri.TryCreate(value, UriKind.Absolute, ...) accepts a bare path on Linux (as file:///...). Call " +
                "Stomme.Shared.Utilities.TryCreateAbsoluteUri instead. Found:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void The_scanner_works_in_both_directions()
        {
            Assert.Single(Scan("class C { bool M(string s) => Uri.TryCreate(s, UriKind.Absolute, out _); }", "planted/Bare.cs"));
            Assert.Single(Scan("class C { bool M(string s) => System.Uri.TryCreate(s, System.UriKind.Absolute, out var u); }", "planted/Qualified.cs"));
            Assert.Empty(Scan("class C { bool M(string s) => Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out _); }", "planted/Either.cs"));
            Assert.Empty(Scan("class C { bool M(string s) => Utilities.TryCreateAbsoluteUri(s, out _); }", "planted/Helper.cs"));
            Assert.Empty(Scan("class C { /* Uri.TryCreate(s, UriKind.Absolute, out _) */ }", "planted/Comment.cs"));
            // And the real tree: the helper's own call is the one that must be found, then allowed.
            Assert.Single(Scan(File.ReadAllText(Path.Combine(RepoRoot(), Helper)), Helper));
        }

        private static IEnumerable<string> Scan(string source, string file)
        {
            foreach (var invocation in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryCreate" } member
                    || member.Expression.ToString() is not ("Uri" or "System.Uri" or "global::System.Uri"))
                {
                    continue;
                }
                if (invocation.ArgumentList.Arguments.Any(argument => argument.Expression.ToString().EndsWith("UriKind.Absolute", StringComparison.Ordinal)))
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return $"{file}({line}): {invocation}";
                }
            }
        }

        private static IEnumerable<string> ProductSourceFiles() =>
            new[] { "*.cs", "*.razor" }
                .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), pattern, SearchOption.AllDirectories))
                .Where(file => !RepoRelative(file).Split('/').Any(segment => segment is "obj" or "bin"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

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
