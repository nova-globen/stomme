#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-132: the server has ONE outbound connect guard (<c>OutboundConnectGuard</c>). The webhook channel once carried a
    /// line-for-line copy of its deny set and connect step, and a copy is a guard that stops being fixed: the next range
    /// added to one would not reach the other. This fails the build on a second connect callback or a second deny set
    /// anywhere under <c>src/</c>.
    /// </summary>
    public sealed class OneOutboundGuardTests
    {
        private const string Guard = "OutboundConnectGuard.cs";

        // A SocketsHttpHandler's connect step being ASSIGNED (`ConnectCallback = ...`), and an address deny test being
        // DEFINED (`bool IsDenied(IPAddress ...`). Using the guard's own members elsewhere is the point, and matches neither.
        private static readonly Regex ConnectStep = new(@"\bConnectCallback\s*=(?!=)", RegexOptions.Compiled);
        private static readonly Regex DenySet = new(@"\bbool\s+IsDenied\s*\(\s*IPAddress\b", RegexOptions.Compiled);

        [Fact]
        public void No_file_but_the_guard_assigns_a_connect_step_or_defines_a_deny_set()
        {
            var offenders = Offenders(SourceFiles());
            Assert.True(offenders.Length == 0, "A second outbound guard exists - use OutboundConnectGuard and pass an allowlist through " +
                "OutboundConnectGuard.AllowedHostsOption instead: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_scan_sees_the_guard_itself_so_it_is_not_blind()
        {
            // Both directions: the one file that SHOULD match does, on both patterns.
            var guard = Assert.Single(SourceFiles(), file => Path.GetFileName(file) == Guard);
            var text = System.IO.File.ReadAllText(guard);
            Assert.Matches(ConnectStep, text);
            Assert.Matches(DenySet, text);

            // And a copy of it, anywhere else, would be named.
            Assert.Equal(new[] { "Copy.cs" }, Offenders(new[] { ("Copy.cs", text) }));
        }

        private static string[] Offenders(string[] files) =>
            Offenders(files.Select(file => (file, System.IO.File.ReadAllText(file))).ToArray());

        private static string[] Offenders((string Path, string Text)[] files) =>
            files.Where(file => Path.GetFileName(file.Path) != Guard || file.Path == "Copy.cs")
                .Where(file => ConnectStep.IsMatch(file.Text) || DenySet.IsMatch(file.Text))
                .Select(file => file.Path)
                .ToArray();

        private static string[] SourceFiles()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Directory.GetFiles(Path.Combine(directory!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();
        }
    }
}
