namespace Stomme.Engine.Pipelines.Management.Models
{
    /// <summary>
    /// Represents a request to save a pipeline definition.
    /// </summary>
    public class SavePipelineDefinitionRequest
    {
        /// <summary>
        /// The pipeline definition to save.
        /// </summary>
        public PipelineDefinitionModel Model { get; set; } = null!;

        /// <summary>
        /// Whether the pipeline definition should be published.
        /// </summary>
        public bool? Publish { get; set; }
    }
}
