#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What the startup lock actually promises — and the class of defect that made it promise less
    /// than it looked like.
    /// </summary>
    public class DistributedLockManagerTests
    {
        private readonly ITestOutputHelper _output;

        public DistributedLockManagerTests(ITestOutputHelper output) => _output = output;

        private static IDistributedCache NewCache() =>
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        private static DistributedLockManager NewManager(IDistributedCache cache, bool scaleOut) =>
            new(cache, new StubConfig(scaleOut));

        [Fact]
        public async Task On_a_single_node_it_grants_at_once_and_takes_no_jitter()
        {
            // The overwhelmingly common deployment. There is nothing to coordinate with, so granting is right -
            // and the grant happens BEFORE the jitter, so an install pays nothing for a lock it does not need.
            var manager = NewManager(NewCache(), scaleOut: false);

            var clock = Stopwatch.StartNew();
            Assert.True(await manager.TryAcquireLockAsync("StartUp", "true", TimeSpan.FromMinutes(1)));
            Assert.True(await manager.TryAcquireLockAsync("StartUp", "true", TimeSpan.FromMinutes(1)));
            clock.Stop();

            _output.WriteLine($"[single node] two acquires in {clock.ElapsedMilliseconds} ms");

            // Both granted, and neither waited: the jitter is only for instances that might collide.
            Assert.True(clock.Elapsed < DistributedLockManager.MaximumJitter,
                "a single-node acquire paid the jitter it does not need");
        }

        [Fact]
        public async Task With_scale_out_a_held_lock_is_refused_and_a_released_one_is_grantable_again()
        {
            var cache = NewCache();
            var manager = NewManager(cache, scaleOut: true);

            Assert.True(await manager.TryAcquireLockAsync("StartUp", "true", TimeSpan.FromMinutes(1)));
            Assert.False(await manager.TryAcquireLockAsync("StartUp", "true", TimeSpan.FromMinutes(1)));

            await manager.ReleaseLockAsync("StartUp");
            Assert.True(await manager.TryAcquireLockAsync("StartUp", "true", TimeSpan.FromMinutes(1)));
            _output.WriteLine("[scale-out] acquire / refuse / release / acquire");
        }

        [Fact]
        public async Task Release_asks_the_same_question_acquire_does()
        {
            // Release used to test ONLY the connection string while acquire tested the flag as well, so with the
            // string present and the flag off a release removed a key no acquire had ever written. Both now
            // agree, so a single-node release is a no-op rather than a write against a cache nobody is using.
            var cache = NewCache();
            await cache.SetStringAsync("StartUp", "planted-by-somebody-else");

            await NewManager(cache, scaleOut: false).ReleaseLockAsync("StartUp");

            Assert.Equal("planted-by-somebody-else", await cache.GetStringAsync("StartUp"));
            _output.WriteLine("[single node] release left the cache alone");
        }

        // ---- the CLASS of defect, not just this instance ----------------------------------------------------

        [Fact]
        public void No_source_file_discards_a_Task_Delay()
        {
            // The original defect was `Task.Delay(randomDelay);` as a bare statement - a Task created, never
            // awaited, and immediately dropped. It compiles, it reads exactly like a pause, and it does nothing
            // at all. Nothing in the repository caught it, so this does, for every file rather than the one.
            var violations = SourceFiles()
                .SelectMany(file => Scan(File.ReadAllText(file), RepoRelative(file)))
                .ToList();

            foreach (var violation in violations)
            {
                _output.WriteLine("[violation] " + violation);
            }

            Assert.True(violations.Count == 0,
                "A discarded Task.Delay does nothing - await it, or assign it deliberately if the wait really is " +
                "meant to happen later. Found:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void The_discarded_delay_scanner_works_in_both_directions()
        {
            // The rule this repository pays for: an instrument is validated on input whose answer is known.
            const string dirty = "class C { void M() { Task.Delay(500); } }";
            const string awaited = "class C { async Task M() { await Task.Delay(500); } }";
            const string assigned = "class C { void M() { var wait = Task.Delay(500); } }";
            const string discarded = "class C { void M() { _ = Task.Delay(500); } }";

            Assert.Single(Scan(dirty, "planted/Dirty.cs"));
            Assert.Empty(Scan(awaited, "planted/Awaited.cs"));
            Assert.Empty(Scan(assigned, "planted/Assigned.cs"));
            Assert.Empty(Scan(discarded, "planted/Discarded.cs"));

            _output.WriteLine("[scanner] fires on a bare Task.Delay; silent when awaited, assigned or explicitly discarded");
        }

        /// <summary>A <c>Task.Delay(...)</c> that is its own statement: created, never awaited, dropped.</summary>
        private static IEnumerable<string> Scan(string source, string file)
        {
            foreach (var statement in CSharpSyntaxTree.ParseText(source).GetRoot()
                         .DescendantNodes().OfType<ExpressionStatementSyntax>())
            {
                if (statement.Expression is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Delay",
                            Expression: IdentifierNameSyntax { Identifier.ValueText: "Task" }
                        }
                    })
                {
                    var line = statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return $"{file}({line}): Task.Delay is created and dropped - it does not wait";
                }
            }
        }

        // ---- file discovery (the shape the other source gates use) -----------------------------------------

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

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return _repoRoot = directory!.FullName;
        }

        /// <summary>Answers only the two settings the lock manager reads; everything else is not its business.</summary>
        private sealed class StubConfig : IConfigManager
        {
            private readonly bool _scaleOut;

            public StubConfig(bool scaleOut) => _scaleOut = scaleOut;

            public T GetSetting<T>(string settingKey, T defaultValue) =>
                settingKey == "Caching:ScaleOut" && typeof(T) == typeof(string)
                    ? (T)(object)(_scaleOut ? "true" : "false")
                    : defaultValue;

            // The connection string is present in BOTH cases on purpose: it is the FLAG that must decide, and
            // release used to look only at this.
            public string GetConnectionString(string name) => "a-distributed-cache";

            public string GetDataDirectory() => throw new NotSupportedException();
            public IConfigurationSection GetSection(string sectionKey) => throw new NotSupportedException();
            public T GetSetting<T>(string sectionKey, string settingKey, T defaultValue) => throw new NotSupportedException();
            public Dictionary<string, string> GetSettings(string sectionKey) => throw new NotSupportedException();
            public void AddOrUpdateSetting<T>(string key, T value, bool reload) => throw new NotSupportedException();
            public void AddOrUpdateSetting<T>(string file, string key, T value, bool reload) => throw new NotSupportedException();
            public void RemoveSetting(string key, bool reload) => throw new NotSupportedException();
            public void RemoveSetting(string file, string key, bool reload) => throw new NotSupportedException();
            public void Reload() => throw new NotSupportedException();
            public string GetWritableSettingsPath() => throw new NotSupportedException();
            public string GetConnectionString() => throw new NotSupportedException();
            public string GetDatabaseType() => throw new NotSupportedException();
            public bool IsInstalled() => throw new NotSupportedException();
            public string GetInstallationId() => throw new NotSupportedException();
            public string EnsureInstallationId() => throw new NotSupportedException();
        }
    }
}
