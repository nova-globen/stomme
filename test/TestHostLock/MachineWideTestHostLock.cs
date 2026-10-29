// Compiled into every test project by the root Directory.Build.targets (the machine-wide test-host throttle).
#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Stomme.TestInfrastructure
{
    /// <summary>
    /// Serialises test hosts across the whole machine: the first test to run in a process takes the named mutex
    /// and holds it until the process exits (the OS releases an exited owner's mutex, so a killed run never
    /// wedges the queue). Set STOMME_TEST_PARALLEL=1 to opt out.
    /// </summary>
    internal static class MachineWideTestHostLock
    {
        private const string MutexName = @"Global\Stomme.TestHost";
        private const string ProcessFlag = "Stomme.TestHostLock.Held";

        internal static Mutex? Held { get; private set; }

        [ModuleInitializer]
        internal static void Acquire()
        {
            // CI jobs run in isolated containers, so a machine-wide queue buys nothing there.
            if (Environment.GetEnvironmentVariable("STOMME_TEST_PARALLEL") == "1"
                || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || AppContext.GetData(ProcessFlag) is not null)
            {
                return;
            }

            AppContext.SetData(ProcessFlag, true);
            var mutex = new Mutex(false, MutexName);
            var waited = Stopwatch.StartNew();
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromMinutes(120)))
                {
                    Console.Error.WriteLine($"[test-lock] gave up waiting after {waited.Elapsed}; running unserialised (pid {Environment.ProcessId}).");
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous owner exited without releasing; ownership is ours.
            }

            Held = mutex;
            var line = $"{DateTime.UtcNow:O} acquired after {waited.Elapsed} pid {Environment.ProcessId} {typeof(MachineWideTestHostLock).Assembly.GetName().Name} {Environment.CurrentDirectory}";
            Console.Error.WriteLine($"[test-lock] {line}");
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-test-lock.log"), line + Environment.NewLine);
            }
            catch (System.IO.IOException)
            {
                // The log is diagnostic only.
            }

            // Now that no other test host can be live, remove what earlier runs left behind.
            var topology = TestScratchSweep.Topology(AppContext.BaseDirectory);
            var scratch = TestScratchSweep.Scratch(System.IO.Path.GetTempPath(), DateTime.UtcNow);
            if (topology + scratch > 0)
            {
                Console.Error.WriteLine($"[test-lock] swept {topology} topology-cache file(s) and {scratch} stale scratch entr(ies).");
            }

            // And the test databases earlier runs left on this machine's own RavenDB - for the same reason, only now.
            var databases = RavenTestDatabaseSweep.Sweep(Environment.GetEnvironmentVariable("RAVENDB_TEST_URL"));
            if (databases > 0)
            {
                var swept = $"{DateTime.UtcNow:O} swept {databases} leftover test database(s) from the local RavenDB, pid {Environment.ProcessId}";
                Console.Error.WriteLine($"[test-lock] {swept}");
                try
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-test-lock.log"), swept + Environment.NewLine);
                }
                catch (System.IO.IOException)
                {
                    // The log is diagnostic only.
                }
            }
        }
    }
}
