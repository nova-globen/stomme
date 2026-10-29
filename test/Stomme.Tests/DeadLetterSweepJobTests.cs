using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Infrastructure;
using Stomme.Infrastructure.EventBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// <see cref="DeadLetterSweepJob"/> ages dead letters by the time they became visible
    /// in the dead-letter queue (<see cref="DeadLetterMessage.VisibleUtc"/>), NOT by
    /// <see cref="DeadLetterMessage.EnqueuedUtc"/>, which the RavenDB queue fills from the Rebus SentTime
    /// header — the ORIGINAL send time. Aging by that send time would discard a message sent months ago
    /// and dead-lettered an hour ago was discarded on the next sweep; the first two tests pin that it is not.
    /// </summary>
    /// <remarks>
    /// These drive the job's <see cref="DeadLetterSweepJob.ExecuteJobAsync"/> over a scripted in-memory
    /// port. The real RavenDB queue is covered only by <c>RavenDeadLetterSweepLiveTests</c>
    /// (Stomme.Persistence.Raven.Tests); on every other transport the sweep is a stated no-op.
    /// The retention window is pinned by two cases: a message visible before the window is discarded, aged by
    /// VisibleUtc (<see cref="A_message_visible_before_the_retention_window_is_discarded"/>), and a window that is not
    /// positive sweeps nothing, the window being the <c>RetentionDays</c> config key
    /// (<see cref="A_non_positive_retention_sweeps_nothing"/>).
    /// </remarks>
    public class DeadLetterSweepJobTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

        [Fact]
        public async Task An_old_sent_time_with_a_fresh_visible_utc_survives()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("sent-long-ago", "T", "boom", now.AddDays(-90), NoHeaders, now.AddDays(-1)));

            var notes = await RunAsync(queue);

            Assert.Empty(queue.Discarded);
            Assert.Contains("0 discarded", notes);
        }

        [Fact]
        public async Task A_retried_then_re_dead_lettered_message_survives()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("retried", "T", "boom", now.AddDays(-120), NoHeaders, now.AddDays(-60)));

            // An admin retries it (back to the input queue) and it fails again: the transport sends it to the
            // error queue anew, so its visible time is the re-dead-letter time; its SentTime is unchanged.
            Assert.True(await queue.RetryAsync("retried"));
            queue.Messages.Add(new DeadLetterMessage("retried", "T", "boom again", now.AddDays(-120), NoHeaders, now.AddHours(-2)));

            await RunAsync(queue);

            Assert.Empty(queue.Discarded);
            Assert.Single(queue.Messages);
        }

        [Fact]
        public async Task A_message_visible_before_the_retention_window_is_discarded()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("old", "T", null, now.AddDays(-10), NoHeaders, now.AddDays(-10)));
            queue.Messages.Add(new DeadLetterMessage("fresh", "T", null, now.AddDays(-3), NoHeaders, now.AddDays(-3)));
            queue.Messages.Add(new DeadLetterMessage("unknown-visible-time", "T", null, now.AddDays(-365), NoHeaders));

            var notes = await RunAsync(queue, retentionDays: "7");

            Assert.Equal(new[] { "old" }, queue.Discarded);
            Assert.Contains("1 discarded", notes);
            Assert.Contains(queue.Messages, m => m.Id == "unknown-visible-time");
        }

        [Fact]
        public async Task A_non_positive_retention_sweeps_nothing()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("old", "T", null, now.AddDays(-365), NoHeaders, now.AddDays(-365)));

            var notes = await RunAsync(queue, retentionDays: "0");

            Assert.Empty(queue.Discarded);
            Assert.Equal(0, queue.ListCalls);
            Assert.Contains("RetentionDays", notes);
        }

        [Fact]
        public async Task Disabled_by_config_is_a_no_op()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("old", "T", null, now.AddDays(-365), NoHeaders, now.AddDays(-365)));

            var notes = await RunAsync(queue, enabled: null);
            var explicitOff = await RunAsync(queue, enabled: "false");

            Assert.Empty(queue.Discarded);
            Assert.Equal(0, queue.ListCalls);
            Assert.Contains("disabled", notes);
            Assert.Contains("disabled", explicitOff);
        }

        [Fact]
        public async Task No_dead_letter_queue_registered_is_a_no_op_not_a_failed_tick()
        {
            // Messaging:Enabled=false registers NO IDeadLetterQueue at all — GetRequiredService would throw every tick.
            using var provider = BuildProvider(queues: Array.Empty<IDeadLetterQueue>(), enabled: "true", retentionDays: null);
            var job = new DeadLetterSweepJob(provider.GetRequiredService<IServiceScopeFactory>());

            var notes = await job.ExecuteJobAsync(provider);

            Assert.Contains("no-op", notes);
        }

        [Fact]
        public async Task Null_queue_is_a_no_op()
        {
            var nullQueue = new NullDeadLetterQueue();
            using var provider = BuildProvider(new IDeadLetterQueue[] { nullQueue }, enabled: "true", retentionDays: null);
            var job = new DeadLetterSweepJob(provider.GetRequiredService<IServiceScopeFactory>());

            var notes = await job.ExecuteJobAsync(provider);

            Assert.Contains("no-op", notes);
        }

        [Fact]
        public async Task At_most_1000_are_examined_per_run()
        {
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            for (var i = 0; i < 1500; i++)
            {
                queue.Messages.Add(new DeadLetterMessage("m" + i, "T", null, now.AddDays(-400), NoHeaders, now.AddDays(-400)));
            }

            var notes = await RunAsync(queue);

            Assert.Equal(new[] { 1000 }, queue.RequestedMax);
            Assert.Equal(1000, queue.Discarded.Count);
            Assert.Equal(500, queue.Messages.Count);
            Assert.Contains("1000 examined", notes);
        }

        [Fact]
        public async Task Running_twice_in_one_tick_is_idempotent()
        {
            // HostedServiceBase runs ExecuteJobAsync once PER TENANT in one tick; the queue is installation-wide.
            var now = DateTimeOffset.UtcNow;
            var queue = new ScriptedQueue();
            queue.Messages.Add(new DeadLetterMessage("old", "T", null, now.AddDays(-90), NoHeaders, now.AddDays(-90)));
            queue.Messages.Add(new DeadLetterMessage("fresh", "T", null, now.AddDays(-90), NoHeaders, now.AddDays(-1)));

            using var provider = BuildProvider(new IDeadLetterQueue[] { queue }, enabled: "true", retentionDays: null);
            var job = new DeadLetterSweepJob(provider.GetRequiredService<IServiceScopeFactory>());

            var first = await job.ExecuteJobAsync(provider);
            var second = await job.ExecuteJobAsync(provider);

            Assert.Equal(new[] { "old" }, queue.Discarded);
            Assert.Contains("1 discarded", first);
            Assert.Contains("0 discarded", second);
            Assert.Equal(new[] { "fresh" }, queue.Messages.Select(m => m.Id));
        }

        [Fact]
        public void The_job_row_is_disabled_by_default_and_runs_daily()
        {
            using var provider = BuildProvider(Array.Empty<IDeadLetterQueue>(), enabled: null, retentionDays: null);
            var job = new DeadLetterSweepJob(provider.GetRequiredService<IServiceScopeFactory>());

            Assert.Equal("Dead Letter Sweep Job", job.Name);
            Assert.Equal("d", job.Frequency);
            Assert.Equal(1, job.Interval);
            Assert.False(job.IsEnabled);
        }

        private static async Task<string> RunAsync(ScriptedQueue queue, string? enabled = "true", string? retentionDays = null)
        {
            using var provider = BuildProvider(new IDeadLetterQueue[] { queue }, enabled, retentionDays);
            var job = new DeadLetterSweepJob(provider.GetRequiredService<IServiceScopeFactory>());
            return await job.ExecuteJobAsync(provider);
        }

        private static ServiceProvider BuildProvider(IEnumerable<IDeadLetterQueue> queues, string? enabled, string? retentionDays)
        {
            var values = new Dictionary<string, string?>();
            if (enabled != null)
            {
                values["Operations:DeadLetterSweep:Enabled"] = enabled;
            }
            if (retentionDays != null)
            {
                values["Operations:DeadLetterSweep:RetentionDays"] = retentionDays;
            }

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            foreach (var queue in queues)
            {
                services.AddSingleton(queue);
            }
            return services.BuildServiceProvider();
        }

        private sealed class ScriptedQueue : IDeadLetterQueue
        {
            public List<DeadLetterMessage> Messages { get; } = new();
            public List<string> Discarded { get; } = new();
            public List<int> RequestedMax { get; } = new();
            public int ListCalls => RequestedMax.Count;

            public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default)
            {
                RequestedMax.Add(max);
                // The RavenDB queue's contract: ordered by visible time, clamped to 1..1000.
                return Task.FromResult<IReadOnlyList<DeadLetterMessage>>(
                    Messages.OrderBy(m => m.VisibleUtc ?? DateTimeOffset.MaxValue).Take(Math.Clamp(max, 1, 1000)).ToList());
            }

            public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Messages.Count);

            public Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default) =>
                Task.FromResult(Messages.RemoveAll(m => m.Id == id) > 0);

            public Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default)
            {
                var found = Messages.RemoveAll(m => m.Id == id) > 0;
                if (found)
                {
                    Discarded.Add(id);
                }
                return Task.FromResult(found);
            }
        }
    }
}