using Stomme.Engine.Pipelines.Runtime.Results;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a broker for delivering stimuli to steps in a pipeline.
/// </summary>
public interface IStimulusSender
{
    /// <summary>
    /// Delivers a stimulus to a step. This could result in new pipeline instances as well as existing pipeline instances being resumed.
    /// </summary>
    Task<SendStimulusResult> SendAsync(string stepTypeName, object stimulus, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a stimulus to a step. This could result in new pipeline instances as well as existing pipeline instances being resumed.
    /// </summary>
    Task<SendStimulusResult> SendAsync(string stimulusHash, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default);
}