using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Results;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Contains extension methods for the <see cref="IStimulusSender"/> interface.
/// </summary>
public static class PipelineBrokerExtensions
{
    /// <summary>
    /// Delivers a stimulus to a step. This could result in new pipeline instances as well as existing pipeline instances being resumed.
    /// </summary>
    [Obsolete("Use SendAsync(string name, object stimulus, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default) instead.")]
    public static Task<SendStimulusResult> SendAsync<TStep>(this IStimulusSender stimulusSender, object stimulus, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        var name = StepTypeNameHelper.GenerateTypeName<TStep>();
        return stimulusSender.SendAsync(name, stimulus, metadata, cancellationToken);
    }
}