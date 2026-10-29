#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Metering.Application;
using Stomme.Persistence.Abstractions;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Two folds of one quota on one host never overlap, whichever of them holds the cross-node lock. Found by a
    /// RavenDB gate run that failed with a quota concurrency conflict: a recorder that found the distributed lock held
    /// took the in-process gate INSTEAD and folded beside the recorder holding the lock.
    /// </summary>
    public class QuotaFoldExclusionTests
    {
        /// <summary>A lock that grants one holder at a time and answers null (busy) to everyone else - as the real ones do.</summary>
        private sealed class NonBlockingLock : IDistributedLock
        {
            private int _held;

            public int Denied;

            public Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
            {
                if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
                {
                    Interlocked.Increment(ref Denied);
                    return Task.FromResult<ILockHandle?>(null);
                }

                return Task.FromResult<ILockHandle?>(new Handle(this));
            }

            private sealed class Handle(NonBlockingLock owner) : ILockHandle
            {
                public string Key => "metering";

                public ValueTask DisposeAsync()
                {
                    Interlocked.Exchange(ref owner._held, 0);
                    return ValueTask.CompletedTask;
                }
            }
        }

        [Fact]
        public async Task Two_folds_of_one_quota_never_run_at_once_on_one_host()
        {
            var distributed = new NonBlockingLock();
            var inside = 0;
            var maximum = 0;

            async Task<int> Fold()
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maximum, now);
                await Task.Delay(30);
                Interlocked.Decrement(ref inside);
                return now;
            }

            // Each recorder builds its own QuotaFold (the gate is scoped), as UsageRecorder does per fold.
            await Task.WhenAll(Enumerable(8, () => new QuotaFold(distributed).HoldAsync(1, "tests.exclusion", Fold)));

            Assert.Equal(1, maximum);
        }

        [Fact]
        public async Task A_lock_held_elsewhere_is_waited_for_and_the_fold_still_runs()
        {
            var distributed = new NonBlockingLock();
            await using (await distributed.AcquireAsync("metering:1:tests.elsewhere", TimeSpan.FromSeconds(30)))
            {
                // Held by "another node" for the whole wait: the fold runs under the local gate alone, after the wait.
                var started = DateTime.UtcNow;
                var result = await new QuotaFold(distributed).HoldAsync(1, "tests.elsewhere", () => Task.FromResult(42));

                Assert.Equal(42, result);
                Assert.True(DateTime.UtcNow - started >= QuotaFold.DistributedWait - TimeSpan.FromMilliseconds(100), "the fold did not wait for the cross-node lock");
                Assert.True(distributed.Denied > 1, "the lock was asked once and given up on");
            }
        }

        private static Task<int>[] Enumerable(int count, Func<Task<int>> start)
        {
            var tasks = new Task<int>[count];
            for (var i = 0; i < count; i++)
            {
                tasks[i] = Task.Run(start);
            }

            return tasks;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
