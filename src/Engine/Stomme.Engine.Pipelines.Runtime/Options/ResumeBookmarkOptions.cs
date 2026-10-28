using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Options for resuming a pipeline.
/// </summary>
[UsedImplicitly]
public class ResumeBookmarkOptions
{
    /// <summary>
    /// The input to provide to the pipeline.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }
    
    /// <summary>
    /// The properties to provide to the pipeline.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }
}