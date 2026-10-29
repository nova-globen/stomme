using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-19 — the MAUI hybrid shell is in its own solution, so <c>dotnet test Stomme.slnx</c> never
    /// compiled it and it rotted silently: measured 2026-08-03, <c>dotnet build Stomme.Maui.slnx</c> had
    /// been failing on NETSDK1147 (the root <c>Directory.Build.props</c>' singular
    /// <c>TargetFramework=net10.0</c> leaked into the csproj, and UseMaui + a plain net10.0 TFM demands
    /// every MAUI workload, tizen included) — a written "also build the MAUI solution" rule enforced by
    /// nobody. Rules here are tests: this fact BUILDS the MAUI solution inside the commit gate.
    /// </summary>
    /// <remarks>
    /// Requires the <c>maui-windows</c> workload (installed on the gate machine 2026-08-03). Placed in the
    /// serial render-tests collection so the build's CPU load never overlaps the timing-sensitive
    /// install/render tests. CI is unaffected: <c>ci.yml</c> runs only the three database-free suites and
    /// the render-guard filter, never this fact — the LOCAL gate is where MAUI rot gets caught, which is
    /// exactly where the BL-19 decision aimed.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MauiSolutionBuildGateTests
    {
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(8);

        [WindowsFact("the MAUI solution targets net10.0-windows, which builds only on Windows")]
        public async Task The_Maui_solution_builds()
        {
            var root = FindRepoRoot();

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build Stomme.Maui.slnx --nologo",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            var output = new StringBuilder();
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };

            Assert.True(process.Start(), "dotnet build did not start");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(BuildTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                Assert.Fail($"dotnet build Stomme.Maui.slnx did not finish within {BuildTimeout.TotalMinutes:0} minutes.");
            }

            string tail;
            lock (output)
            {
                var text = output.ToString();
                tail = text.Length > 4000 ? text.Substring(text.Length - 4000) : text;
            }
            Assert.True(process.ExitCode == 0,
                $"dotnet build Stomme.Maui.slnx failed (exit {process.ExitCode}). Output tail:\n{tail}");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stomme.Maui.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
