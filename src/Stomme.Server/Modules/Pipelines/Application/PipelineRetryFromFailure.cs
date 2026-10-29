#nullable enable
using System;
using System.Linq;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.State;
using EngineGraph = Stomme.Engine.Pipelines.Models.PipelineGraph;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// n8n's retry "from the node with the error": a failed run goes on as a new run from the step that failed, on the
    /// pipeline's published version - the steps that had finished are not run again (an e-mail is not sent twice, a card not
    /// charged twice), what they saved is kept, and the failed step runs anew with its inputs read again. The engine resumes a
    /// stored state at the work it holds scheduled, so the earlier run's state is copied under the new run's id, each failed
    /// step scheduled again in the context it failed in, and the run marked going - refused, saying why, where that cannot be
    /// the run the author means.
    /// </summary>
    public static class PipelineRetryFromFailure
    {
        /// <summary>The property naming the run a retry goes on from - shown on the retry's page, linking back.</summary>
        public const string RetryOfProperty = "Stomme.RetryOf";

        /// <summary>
        /// The earlier run's state - a copy the caller owns - made the new run's: under <paramref name="runId"/>, on
        /// <paramref name="version"/>, going again at each step that failed. Null, with the reason, when it cannot be: the run did
        /// not fail, it kept no failed step, it was also waiting, or a step it was in is not in the version any more.
        /// </summary>
        public static PipelineState? Prepare(PipelineState copy, string runId, EngineGraph version, DateTimeOffset now, Func<string> newId, out string? refusal)
        {
            refusal = null;
            if (copy.SubStatus != PipelineSubStatus.Faulted)
            {
                refusal = "Only a run that failed goes on from the step that failed - run it again instead.";
                return null;
            }

            var failed = copy.StepExecutionContexts.Where(context => context.Status == StepStatus.Faulted).ToList();
            if (failed.Count == 0)
            {
                refusal = "The run kept no step that failed - retry it from the start.";
                return null;
            }

            // A branch still waiting when another failed would wait on a bookmark of the earlier run's: from the start instead.
            if (copy.Bookmarks.Count > 0)
            {
                refusal = "The run was also waiting when it failed - retry it from the start.";
                return null;
            }

            var gone = copy.StepExecutionContexts.FirstOrDefault(context => version.FindNodeById(context.ScheduledStepNodeId) is null);
            if (gone is not null)
            {
                refusal = failed.Contains(gone)
                    ? "The step that failed is not in the published version any more - retry the run from the start."
                    : "A step the run was in is not in the published version any more - retry the run from the start.";
                return null;
            }

            // The new run's contexts are its own: the engine keeps a record of each step's pass under its context's id, so the
            // earlier ids would write the new run's passes over the earlier run's (measured driving it: its timeline moved). And
            // they begin now, in the new run.
            var renamed = copy.StepExecutionContexts.ToDictionary(context => context.Id, _ => newId(), StringComparer.Ordinal);
            foreach (var context in copy.StepExecutionContexts)
            {
                context.Id = renamed[context.Id];
                context.ParentContextId = context.ParentContextId is { } parent && renamed.TryGetValue(parent, out var renamedParent) ? renamedParent : context.ParentContextId;
                context.StartedAt = now;
                context.CompletedAt = null;
            }

            copy.CompletionCallbacks = copy.CompletionCallbacks
                .Select(callback => new CompletionCallbackState(renamed.TryGetValue(callback.OwnerInstanceId, out var owner) ? owner : callback.OwnerInstanceId, callback.ChildNodeId, callback.MethodName, callback.Tag))
                .ToList();
            foreach (var work in copy.ScheduledSteps)
            {
                work.OwnerContextId = work.OwnerContextId is { } owner && renamed.TryGetValue(owner, out var renamedOwner) ? renamedOwner : work.OwnerContextId;
                work.ExistingStepExecutionContextId = work.ExistingStepExecutionContextId is { } existing && renamed.TryGetValue(existing, out var renamedExisting) ? renamedExisting : work.ExistingStepExecutionContextId;
            }

            var byId = copy.StepExecutionContexts.ToDictionary(context => context.Id, StringComparer.Ordinal);
            foreach (var context in failed)
            {
                // The fault counted on the step and every step it is in; the engine counts it again if it fails again.
                var count = context.FaultCount;
                context.FaultCount = 0;
                context.IsExecuting = false;
                for (var parentId = context.ParentContextId; parentId is not null && byId.TryGetValue(parentId, out var parent); parentId = parent.ParentContextId)
                {
                    parent.FaultCount = Math.Max(0, parent.FaultCount - count);
                }

                copy.ScheduledSteps.Add(new StepWorkItemState
                {
                    StepNodeId = context.ScheduledStepNodeId,
                    OwnerContextId = context.ParentContextId,
                    ExistingStepExecutionContextId = context.Id,
                });
            }

            var earlierId = copy.Id;
            copy.Id = runId;
            copy.DefinitionVersionId = version.Pipeline.Identity.Id;
            copy.DefinitionVersion = version.Pipeline.Identity.Version;
            copy.ParentPipelineInstanceId = null; // as a retry from the start: nothing waits on it
            copy.Status = PipelineStatus.Running;
            copy.SubStatus = PipelineSubStatus.Executing;
            copy.IsExecuting = false;
            copy.Incidents.Clear();
            copy.FinishedAt = null;
            copy.CreatedAt = now;
            copy.UpdatedAt = now;
            copy.Properties[RetryOfProperty] = earlierId;
            return copy;
        }
    }
}
