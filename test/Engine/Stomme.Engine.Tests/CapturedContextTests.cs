using System.Collections.Concurrent;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Tests;

/// <summary>
/// A pipeline run started on a single-threaded
/// <see cref="SynchronizationContext"/> - a Blazor circuit's dispatcher, a renderer - must never post its
/// continuations back to it. Upstream's engine awaited without <c>ConfigureAwait(false)</c> (its Fody weaver was
/// configured to change nothing), so a step that truly suspends brought the rest of the run back onto the
/// caller's pump: measured 1 post on the vendored tree before the fix, 0 after it. The fix is
/// <c>ConfigureAwait(false)</c> on every await in <c>src/Engine</c>, written by <c>scripts/vendor-engine.ps1</c>
/// and held by CA2007 as an error; this is the behavioural half of that gate.
/// </summary>
public class CapturedContextTests
{
    [Fact]
    public async Task A_run_started_on_a_single_threaded_context_posts_nothing_back_to_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResultSink>();
        services.AddEngine();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();

        using var pump = new CountingPump();
        var run = await pump.StartAsync(() => runner.RunAsync(new SuspendingStep()));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
        Assert.Equal(SuspendingStep.Written, provider.GetRequiredService<ResultSink>().Value);
        // The step's own await is configured; every post would be the ENGINE resuming on the caller's pump.
        Assert.Equal(0, pump.Posts);
    }

    // The instrument, checked on its own: a bare await started on the pump DOES come back to it. Without this, a
    // pump that never received posts (a broken context, a start that ran elsewhere) would report 0 and the test
    // above would be green while proving nothing.
    [Fact]
    public async Task The_pump_counts_a_continuation_that_captures_it()
    {
        using var pump = new CountingPump();
        var run = await pump.StartAsync(async () =>
        {
            await Task.Delay(20);
            return SynchronizationContext.Current is CountingPump;
        });

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, pump.Posts);
    }

    /// <summary>
    /// A single-threaded <see cref="SynchronizationContext"/> with its own thread, counting every
    /// <see cref="Post"/> and <see cref="Send"/>. The start itself is queued without counting.
    /// </summary>
    private sealed class CountingPump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _posts;

        public CountingPump()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            }) { IsBackground = true, Name = "counting-pump" };
            _thread.Start();
        }

        public int Posts => Volatile.Read(ref _posts);

        public Task<Task<T>> StartAsync<T>(Func<Task<T>> start)
        {
            var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    started.SetResult(start());
                }
                catch (Exception e)
                {
                    started.SetException(e);
                }
            });
            return started.Task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            _queue.Add(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            _queue.Add(() =>
            {
                d(state);
                done.Set();
            });
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
            _queue.Dispose();
        }
    }
}

/// <summary>A step that truly suspends - its await completes on another thread - and then writes.</summary>
[Step("Stomme", "Test", "Suspends on a timer, then writes into the test sink.")]
public class SuspendingStep : CodeStep
{
    public const string Written = "stomme-engine-suspended-and-resumed";

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        await Task.Delay(50).ConfigureAwait(false);
        context.GetRequiredService<ResultSink>().Value = Written;
    }
}
