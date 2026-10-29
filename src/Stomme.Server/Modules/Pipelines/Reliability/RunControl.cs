#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Reliability
{
    /// <summary>
    /// The runs a person asked to stop. Stop cancels what is stored, but a run in the middle of a burst of work holds
    /// its state in memory: it went on to its end and wrote that state back over the cancel (n8n's Stop stops a busy run). Its id
    /// is kept here a while, and <see cref="RunControlMiddleware"/> cancels the run before its next step. And the runs this
    /// process knows - dispatched from here, or working here - with their site's tenant, so a run in its FIRST burst, which the
    /// engine stores only when the burst ends, can be stopped by the person who started it. One per process: a run's burst of
    /// work happens on one node, and what nothing asks about is let go after <see cref="KeptFor"/>.
    /// </summary>
    public sealed class PipelineRunStops
    {
        public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _asked = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, (string Tenant, DateTimeOffset Seen)> _known = new(StringComparer.Ordinal);

        public void Ask(string runId, DateTimeOffset now)
        {
            foreach (var stale in _asked.Where(entry => now - entry.Value > KeptFor).Select(entry => entry.Key).ToList())
            {
                _asked.TryRemove(stale, out _);
            }

            _asked[runId] = now;
        }

        /// <summary>Whether the run was asked to stop - taken once: the run is stopped, and the ask is gone.</summary>
        public bool Take(string runId) => _asked.TryRemove(runId, out _);

        /// <summary>A run dispatched from this process, or one of whose steps it is about to run, for the site's tenant.</summary>
        public void Seen(string runId, string? tenant, DateTimeOffset now)
        {
            if (_known.Count > 1000)
            {
                foreach (var stale in _known.Where(entry => now - entry.Value.Seen > KeptFor).Select(entry => entry.Key).ToList())
                {
                    _known.TryRemove(stale, out _);
                }
            }

            _known[runId] = (tenant ?? string.Empty, now);
        }

        /// <summary>Whether this process dispatched or worked on the run lately, for the same site's tenant.</summary>
        public bool Knows(string runId, string? tenant, DateTimeOffset now) =>
            _known.TryGetValue(runId, out var known) && known.Tenant == (tenant ?? string.Empty) && now - known.Seen <= KeptFor;

        private readonly ConcurrentDictionary<string, DateTimeOffset> _stored = new(StringComparer.Ordinal);

        /// <summary>A run the engine has stored: from then on the store says whether it is there (<see cref="MarkRunStored"/>).</summary>
        public void Stored(string runId, DateTimeOffset now)
        {
            if (_stored.Count > 1000)
            {
                foreach (var stale in _stored.Where(entry => now - entry.Value > KeptFor).Select(entry => entry.Key).ToList())
                {
                    _stored.TryRemove(stale, out _);
                }
            }

            _stored[runId] = now;
        }

        /// <summary>
        /// Whether the run is one this process started, or works on, for the site and the engine has not stored yet - "not yet"
        /// (it is stored when its first burst of work ends), where a run stored and since deleted is none.
        /// </summary>
        public bool IsStarting(string runId, string? tenant, DateTimeOffset now) => Knows(runId, tenant, now) && !_stored.ContainsKey(runId);
    }

    /// <summary>
    /// A run the engine has stored is the store's to answer for from then on: deleted after, it is gone (404), not "not
    /// yet" (204). The engine says so after every save of a run's state.
    /// </summary>
    public sealed class MarkRunStored(PipelineRunStops stops, Stomme.Engine.Common.ISystemClock clock)
        : Stomme.Engine.Mediator.Contracts.INotificationHandler<Stomme.Engine.Pipelines.Management.Notifications.PipelineInstanceSaved>
    {
        public Task HandleAsync(Stomme.Engine.Pipelines.Management.Notifications.PipelineInstanceSaved notification, System.Threading.CancellationToken cancellationToken)
        {
            stops.Stored(notification.PipelineInstance.Id, clock.UtcNow);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Before each step of a run: a run a person asked to stop (<see cref="PipelineRunStops"/>), or one busy longer than
    /// its pipeline's time limit (n8n's "Timeout Workflow", <see cref="PipelineRunTimeout"/>), is cancelled - the step does not
    /// start, nor does any after it, and the run's log says why. "Busy" is one burst of work: it begins with the first step this
    /// process runs for the run (the engine makes a fresh execution context for each burst), so the time a run waits, stored -
    /// for a time, a decision, a call - is not counted. The step that is running when the time is up finishes first.
    /// </summary>
    public sealed class RunControlMiddleware(StepMiddlewareDelegate next) : IStepExecutionMiddleware
    {
        private const string BurstStartedKey = "stomme:burstStartedAt";

        private const string TimeoutKey = "stomme:timeoutMinutes";

        public async ValueTask InvokeAsync(StepExecutionContext context)
        {
            var run = context.PipelineExecutionContext;
            if (run.SubStatus == PipelineSubStatus.Cancelled)
            {
                // Cancelled while this burst still has steps scheduled: none of them starts.
                context.TransitionTo(StepStatus.Canceled);
                return;
            }

            var now = run.SystemClock.UtcNow;
            if (!run.TransientProperties.ContainsKey(BurstStartedKey))
            {
                run.TransientProperties[BurstStartedKey] = now;
                run.TransientProperties[TimeoutKey] = PipelineGraphCodec.TimeoutMinutes(run.Pipeline.Root.Metadata) ?? 0;
            }

            var stops = context.GetService<PipelineRunStops>();
            stops?.Seen(run.Id, context.GetService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>()?.TenantId, now);
            string? reason = null;
            if (stops?.Take(run.Id) == true)
            {
                reason = "The run was stopped while it worked, by a person.";
            }
            else if (run.TransientProperties[TimeoutKey] is int limit && limit > 0
                && run.TransientProperties[BurstStartedKey] is DateTimeOffset started && now - started > TimeSpan.FromMinutes(limit))
            {
                reason = PipelineRunTimeout.Reason(limit);
            }

            if (reason is null)
            {
                await next(context);
                return;
            }

            context.AddExecutionLogEntry("Stopped", reason);
            run.Cancel();
            context.TransitionTo(StepStatus.Canceled);
        }

        /// <summary>The engine's step pipeline gains the run control, first inside its exception handling - before a retry.</summary>
        public static void UseRunControl(Stomme.Engine.Features.Services.IModule module) =>
            module.Configure<EngineFeature>(engine => engine.ConfigureStepExecutionPipeline += builder =>
                builder.Insert(1, next => context => new RunControlMiddleware(next).InvokeAsync(context)));
    }
}
