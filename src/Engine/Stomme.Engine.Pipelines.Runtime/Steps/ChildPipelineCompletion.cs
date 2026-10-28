using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Stomme hand fix (BL-229, 2026-10-08): what a step waiting for a child pipeline (Execute or Dispatch pipeline) is resumed
/// with when the child finishes after a pause - the child's result under keys of its own. The child's output used to be handed
/// over AS the parent's input: the waiting step then read the whole input, mixed with the parent's own, as its result, and
/// Execute pipeline's typed result took no field from it - a parent whose child had paused read nothing of what the child gave
/// (measured through the engine) - while the child's output keys showed in the parent's <c>$input</c>. Keys starting "__" are
/// the engine's own, which <c>$input</c> leaves out.
/// </summary>
internal static class ChildPipelineCompletion
{
    internal const string InstanceIdKey = "__ChildPipelineInstanceId";
    internal const string SubStatusKey = "__ChildPipelineSubStatus";
    internal const string OutputKey = "__ChildPipelineOutput";
    internal const string ReasonKey = "__ChildPipelineReason";

    /// <summary>The resume's input: the finished child's id, how it finished, its output and why it failed, if it did.</summary>
    internal static IDictionary<string, object> Input(PipelineState child) => new Dictionary<string, object>
    {
        [InstanceIdKey] = child.Id,
        [SubStatusKey] = child.SubStatus.ToString(),
        [OutputKey] = child.Output,
        [ReasonKey] = child.Incidents.FirstOrDefault()?.Message ?? string.Empty,
    };

    /// <summary>Why the child failed - its first incident's message - or null.</summary>
    internal static string? ReasonOf(IDictionary<string, object> input) =>
        input.TryGetValue(ReasonKey, out var reason) && reason?.ToString() is { Length: > 0 } text ? text : null;

    /// <summary>The child's output, as the resume's input carries it - a dictionary in memory, its JSON once stored.</summary>
    internal static IDictionary<string, object> OutputOf(IDictionary<string, object> input) =>
        input.TryGetValue(OutputKey, out var output) && output.ConvertTo<IDictionary<string, object>>() is { } read
            ? read
            : new Dictionary<string, object>();

    /// <summary>The child's result as an immediate finish gives it (<see cref="ExecutePipelineResult"/>).</summary>
    internal static ExecutePipelineResult ResultOf(IDictionary<string, object> input) => new()
    {
        PipelineInstanceId = input.TryGetValue(InstanceIdKey, out var id) ? id?.ToString() ?? string.Empty : string.Empty,
        Status = PipelineStatus.Finished,
        SubStatus = input.TryGetValue(SubStatusKey, out var subStatus) && Enum.TryParse<PipelineSubStatus>(subStatus?.ToString(), out var parsed) ? parsed : PipelineSubStatus.Finished,
        Output = OutputOf(input),
    };
}
