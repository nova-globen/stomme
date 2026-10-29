#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// What a run was started with, kept on the run (n8n's execution data). The engine persists only the inputs a
    /// pipeline DECLARES, and this editor's pipelines declare none - so a webhook's body, a schedule's fire time, a value given
    /// to Run and the failure a handler is told of were gone once the run was stored (measured: a stored run's state held no
    /// input at all), and neither a run's page nor a retry could have them. When a run starts, its input is copied into the
    /// run's properties, which the engine persists with its state. What a step received is already in its execution record,
    /// so this keeps nothing a run did not already store; a step test, which stores nothing, is left alone.
    /// </summary>
    public sealed class KeepRunInput : INotificationHandler<PipelineStarted>
    {
        public const string PropertyKey = "stomme:input";

        public Task HandleAsync(PipelineStarted notification, CancellationToken cancellationToken)
        {
            var context = notification.PipelineExecutionContext;
            if (context.Input is { Count: > 0 } input && !StepTestCommitGate.IsStepTest(context))
            {
                context.Properties[PropertyKey] = new Dictionary<string, object>(input);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// A stored run's input: what was kept when it started (as the engine reads it back - a dictionary, or JSON), over what
        /// the engine itself persisted of its declared inputs.
        /// </summary>
        public static IDictionary<string, object> Of(PipelineState? state)
        {
            var input = new Dictionary<string, object>(state?.Input ?? new Dictionary<string, object>());
            if (state?.Properties is { } properties && properties.TryGetValue(PropertyKey, out var kept))
            {
                var pairs = kept switch
                {
                    IDictionary<string, object> dictionary => dictionary.Select(pair => (pair.Key, pair.Value)),
                    JsonElement { ValueKind: JsonValueKind.Object } element => element.EnumerateObject().Select(property => (property.Name, (object)property.Value.Clone())),
                    _ => Enumerable.Empty<(string, object)>(),
                };
                foreach (var (key, value) in pairs)
                {
                    input[key] = value;
                }
            }

            return input;
        }

        /// <summary>The property a run carries once what it was started with is no longer kept.</summary>
        public const string NotKeptKey = "stomme:input-not-kept";

        /// <summary>
        /// A run whose pipeline does not keep its runs' data (<see cref="Stomme.Models.Pipelines.PipelineStepDataKeeping"/>):
        /// what it was started with and what it returned emptied, and - when it was started with something - marked, so its page
        /// says so and it is not run again without it. The steps' contexts the engine keeps to go on from a failed step stay. False
        /// when there was nothing to empty.
        /// </summary>
        public static bool Empty(PipelineState state)
        {
            var started = Of(state).Count > 0;
            if (!started && state.Output is not { Count: > 0 })
            {
                return false;
            }

            state.Input = new Dictionary<string, object>();
            state.Output = new Dictionary<string, object>();
            state.Properties.Remove(PropertyKey);
            if (started)
            {
                state.Properties[NotKeptKey] = true;
            }

            return true;
        }

        /// <summary>Whether what the run was started with was emptied - read back as a store returns it, a bool or JSON.</summary>
        public static bool WasEmptied(PipelineState? state) =>
            state?.Properties is { } properties && properties.TryGetValue(NotKeptKey, out var value) && value switch
            {
                bool flag => flag,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                string text => bool.TryParse(text, out var parsed) && parsed,
                _ => false,
            };

        /// <summary>Why a run whose input was emptied is not run again, or gone on with.</summary>
        public const string NotKeptRefusal = "What this run was started with was not kept - the pipeline is set not to keep its runs' data - so it cannot be run again with it. Run the pipeline instead.";
    }
}
