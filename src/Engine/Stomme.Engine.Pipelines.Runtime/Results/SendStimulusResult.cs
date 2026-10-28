using Stomme.Engine.Pipelines.Runtime.Messages;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Results;

/// <summary>
/// Represents the result of sending a stimulus the engine.
/// </summary>
[UsedImplicitly]
public record SendStimulusResult(ICollection<RunPipelineInstanceResponse> PipelineInstanceResponses);