#nullable enable
using System;
using System.IO;
using Stomme.TestInfrastructure;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-156: the start-of-run sweep removes earlier runs' leftovers - and nothing else. Driven against a folder of its
    /// own with controlled timestamps, never the real <c>%TEMP%</c> or <c>bin</c>, which the sweep itself owns at runtime.
    /// </summary>
    public sealed class TestScratchSweepTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "sweep-probe-" + Guid.NewGuid().ToString("N"));

        public TestScratchSweepTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void Stale_scratch_goes_and_recent_scratch_the_gate_logs_and_everything_else_stay()
        {
            var staleFolder = Folder("stomme-authz-gate-0123", Now.AddDays(-2));
            var staleFile = File("stomme-metering-conformance-0123.db", Now.AddHours(-7));
            var recentFolder = Folder("stomme-authz-gate-4567", Now.AddHours(-1));        // this gate's first test host
            var gateLogs = Folder("stomme-gate", Now.AddDays(-3));                         // KEPT, however old
            var lockLog = File("stomme-test-lock.log", Now.AddDays(-3));                   // KEPT, however old
            var someoneElses = Folder("otherapp-scratch", Now.AddDays(-9));                  // not ours
            var server = Folder("stomme-mongodb-test", Now.AddDays(-2));                   // KEPT: a throwaway server's password
            var serverToken = Folder("stomme-render-test-4567", Now.AddDays(-2));         // a run's scratch, its token after

            Assert.Equal(3, TestScratchSweep.Scratch(_root, Now));

            Assert.False(Directory.Exists(staleFolder));
            Assert.False(System.IO.File.Exists(staleFile));
            Assert.True(Directory.Exists(recentFolder));
            Assert.True(Directory.Exists(gateLogs));
            Assert.True(System.IO.File.Exists(lockLog));
            Assert.True(Directory.Exists(someoneElses));
            Assert.True(Directory.Exists(server));
            Assert.False(Directory.Exists(serverToken));
        }

        [Fact]
        public void Topology_cache_files_go_and_nothing_else_in_the_folder_does()
        {
            var cache = File("stomme_gate_tmpl_abc.F9519F25862C6E95.raven-database-topology", Now);
            var assembly = File("Stomme.Tests.dll", Now);
            var nested = Path.Combine(Folder("runtimes", Now), "x.raven-database-topology");
            System.IO.File.WriteAllText(nested, "{}");

            Assert.Equal(1, TestScratchSweep.Topology(_root));

            Assert.False(System.IO.File.Exists(cache));
            Assert.True(System.IO.File.Exists(assembly));
            Assert.True(System.IO.File.Exists(nested));        // top level only: the client writes there, nowhere else
        }

        private string Folder(string name, DateTime lastWriteUtc)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            System.IO.File.WriteAllText(Path.Combine(path, "content.txt"), "x");
            Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        private string File(string name, DateTime lastWriteUtc)
        {
            var path = Path.Combine(_root, name);
            System.IO.File.WriteAllText(path, "x");
            System.IO.File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
