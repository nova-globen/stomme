using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Parameters;

/// <summary>
/// Options for resuming pipelines.
/// </summary>
[Obsolete("This type is obsolete. Use the new CreateClientAsync methods of IPipelineRuntime instead.")]
public class ResumePipelineRuntimeParams
{
    public string? CorrelationId { get; set; }
    public string? BookmarkId { get; set; }
    public StepHandle? StepHandle { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public CancellationToken CancellationToken { get; set; }
}