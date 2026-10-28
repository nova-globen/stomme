using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// A filter for finding pipelines to trigger.
/// </summary>
/// <param name="StepTypeName">The step type name to trigger pipelines for.</param>
/// <param name="BookmarkPayload">The bookmark payload to trigger pipelines for.</param>
/// <param name="Options">The options to use when triggering pipelines.</param>
[Obsolete("This type is obsolete. Use the new CreateClientAsync methods of IPipelineRuntime instead.")]
public record PipelinesFilter(string StepTypeName, object BookmarkPayload, TriggerPipelinesOptions Options);