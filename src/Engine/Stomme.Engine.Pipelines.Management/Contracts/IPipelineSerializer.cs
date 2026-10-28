using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Serializes and deserializes pipelines.
/// </summary>
public interface IPipelineSerializer
{
    /// <summary>
    /// Serializes the specified pipeline.
    /// </summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <returns>A string representing the serialized pipeline.</returns>
    string Serialize(Pipeline pipeline);
    
    /// <summary>
    /// Serializes the specified pipeline.
    /// </summary>
    /// <param name="serializedPipeline">The data representing the pipeline.</param>
    /// <returns>A deserialized pipeline.</returns>
    Pipeline Deserialize(string serializedPipeline);
}