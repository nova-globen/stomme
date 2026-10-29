using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Install ONCE per provider-and-settings, then hand every case its OWN pristine copy of that install.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a copy and not a shared install.</b> Measured 2026-09-22: on RavenDB the INSTALL is the whole cost of a
    /// driven test - compose 0.15 s, install 40-60 s, start 1 s - while a boot over an already-installed database is
    /// 0.2 s (every save waits for its indexes, <c>RavenFrameworkStoreOptions.WaitForNonStaleResults</c>, on purpose).
    /// The slow tests are NOT several cases in one class re-installing - they are one case per class per provider,
    /// spread over some sixty classes - so a class fixture saves almost nothing, and a fixture shared ACROSS classes
    /// would let sixty classes see each other's users, roles and rows. A copy keeps what made the suites trustworthy:
    /// each case still owns a database nobody else has written to.
    /// </para>
    /// <para>
    /// <b>What a clone is.</b> The template is a real <see cref="GateDrive.InstallAsync(ScratchGateInstall, ITestOutputHelper)"/>
    /// run, stopped before the hosted services start. A clone copies its content root (settings the installer wrote
    /// back, the SQLite sidecars, the site folders) and its database (a file copy, <c>CREATE DATABASE ... TEMPLATE</c>,
    /// or a RavenDB smuggler export straight into a new database), then rewrites the two things that name the
    /// template - its content root and its database - in the copied settings. The case then calls
    /// <c>GateDrive.InstallAsync</c> exactly as before, which now BOOTS an installed site, as a deployed host does on
    /// every start but its first.
    /// </para>
    /// <para>
    /// <b>What must NOT use it - decided per class:</b> anything about the install itself (the wizard, seeding
    /// contracts, "a stock install has none"), the two-boot upgrade suites, and a case whose <c>configure</c> hook
    /// swaps a service the INSTALL would have used (a clock stamps rows). Those keep
    /// <c>new ScratchGateInstall(...)</c>. The install path stays covered by them and by the render guard.
    /// </para>
    /// <para>
    /// <b>A clone that did not copy must go RED, not slow.</b> An empty database would simply be installed afresh by
    /// the boot and every case would pass - so <see cref="GateDrive"/> refuses to boot a clone whose store has no
    /// alias BEFORE the installer runs. <c>GateInstallTemplateTests</c> pins that, and that two clones do not see
    /// each other's writes.
    /// </para>
    /// </remarks>
    internal static class GateInstallTemplates
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<GateInstallTemplate>>> Templates = new();
        private static readonly ConcurrentBag<GateInstallTemplate> Installed = new();
        private static int _exitHooked;

        /// <summary>How many real installs this process has paid for - what <c>GateInstallTemplateTests</c> counts.</summary>
        public static int TemplateInstalls => _templateInstalls;

        private static int _templateInstalls;

        /// <summary>
        /// <c>STOMME_TEST_FRESH_INSTALLS=1</c> makes every clone a real install again. Two uses: the same-day A/B that
        /// says what the templates are worth (an absolute time means little here - the shared RavenDB's speed varies
        /// threefold between days), and the first question to ask of a red case: does it fail on a fresh install too?
        /// </summary>
        public static bool FreshInstallsForced => Environment.GetEnvironmentVariable("STOMME_TEST_FRESH_INSTALLS") == "1";

        public static Task<ScratchGateInstall> CloneAsync(IGateStore store, string defaultAlias, ITestOutputHelper output) =>
            CloneAsync(store, defaultAlias, output, colocateModuleAggregates: false, extraSettings: null);

        public static async Task<ScratchGateInstall> CloneAsync(
            IGateStore store, string defaultAlias, ITestOutputHelper output, bool colocateModuleAggregates, JsonObject? extraSettings)
        {
            // SQL Server has no template clone (copying a database needs a BACKUP file on the server's own disk): it installs.
            if (FreshInstallsForced || store is SqlServerGateStore)
            {
                return new ScratchGateInstall(store, defaultAlias, colocateModuleAggregates, extraSettings);
            }

            var key = string.Join("|", store.Name, defaultAlias, colocateModuleAggregates, extraSettings?.ToJsonString() ?? "");
            var pending = Templates.GetOrAdd(key, _ => new Lazy<Task<GateInstallTemplate>>(
                () => InstallTemplateAsync(store, defaultAlias, output, colocateModuleAggregates, extraSettings)));
            GateInstallTemplate template;
            try
            {
                template = await pending.Value;
            }
            catch
            {
                // A template that failed to install (the RavenDB Raft timeout, say) must fail THIS case only. Left in
                // the cache it would fail every later case that shares its key, from one transient error.
                Templates.TryRemove(new KeyValuePair<string, Lazy<Task<GateInstallTemplate>>>(key, pending));
                throw;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            var clone = await ScratchGateInstall.CloneOfAsync(store, template);
            output.WriteLine($"[{store.Name}][timing] clone of template {template.Scratch.Store.DatabaseToken} {timer.ElapsedMilliseconds} ms{(store is RavenGateStore ? " (" + RavenGateStore.LastCloneTiming + ")" : "")}");
            return clone;
        }

        private static async Task<GateInstallTemplate> InstallTemplateAsync(
            IGateStore store, string defaultAlias, ITestOutputHelper output, bool colocateModuleAggregates, JsonObject? extraSettings)
        {
            if (System.Threading.Interlocked.Exchange(ref _exitHooked, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
            }

            var templateStore = store.NewTemplate();
            await templateStore.SweepStaleTemplatesAsync();

            var scratch = new ScratchGateInstall(templateStore, defaultAlias, colocateModuleAggregates, extraSettings);
            try
            {
                // start: false - the template is what the INSTALLER produced and nothing else. Whatever the hosted
                // services do on a first start, they do in the clone, where the case can see it.
                var app = await GateDrive.InstallAsync(scratch, output, configure: null, start: false);
                await GateDrive.ShutdownAsync(app);
                System.Threading.Interlocked.Increment(ref _templateInstalls);
            }
            catch
            {
                scratch.Dispose();
                throw;
            }

            // The host is gone; release what it pooled so the files are closed (and the WAL folded in) before the
            // first copy, and hand the process-global override path back - the template is not "the current install".
            ScratchSqlite.ReleaseInstall(scratch.ContentRoot);
            await templateStore.QuiesceAsync();
            scratch.ReleaseEnvironment();
            var template = new GateInstallTemplate(scratch);
            Installed.Add(template);
            return template;
        }

        private static void DisposeAll()
        {
            foreach (var template in Installed)
            {
                try
                {
                    template.Scratch.Dispose();
                }
                catch
                {
                    // best effort - the next run's sweep removes a leftover template
                }
            }
        }

        internal static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
            }
        }

        /// <summary>
        /// Points the copied settings at the clone: the template's content root and database token must not survive
        /// anywhere in them, or the clone would quietly read and write the TEMPLATE - which every later clone is
        /// made from.
        /// </summary>
        internal static void RewriteSettings(string contentRoot, GateInstallTemplate template, IGateStore store)
        {
            var templateStore = template.Scratch.Store;
            foreach (var fileName in new[] { "appsettings.override.json", "appsettings.json" })
            {
                var path = Path.Combine(contentRoot, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                foreach (var (from, to) in new List<(string, string)>
                {
                    (JsonEscape(template.Scratch.ContentRoot), JsonEscape(contentRoot)),
                    (template.Scratch.ContentRoot, contentRoot),
                    (templateStore.DatabaseToken, store.DatabaseToken),
                })
                {
                    if (!string.IsNullOrEmpty(from))
                    {
                        text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (text.Contains(Path.GetFileName(template.Scratch.ContentRoot), StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(templateStore.DatabaseToken) && text.Contains(templateStore.DatabaseToken, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"[{store.Name}] {fileName} of a cloned install still names its template after the rewrite - the clone would share the template's store.");
                }

                File.WriteAllText(path, text);
            }
        }

        private static string JsonEscape(string value) => value.Replace("\\", "\\\\");
    }

    /// <summary>One finished install, kept for the life of the test process and only ever read.</summary>
    internal sealed record GateInstallTemplate(ScratchGateInstall Scratch);
}
