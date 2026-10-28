using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Tests.Helpers;

/// <summary>
/// Provides helper methods for creating test data in Pipeline Management unit tests.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Creates a pipeline definition with the specified parameters.
    /// </summary>
    public static PipelineDefinition CreatePipelineDefinition(string definitionId, string materializerName)
    {
        return new PipelineDefinition
        {
            DefinitionId = definitionId,
            MaterializerName = materializerName,
            Version = 1
        };
    }

    /// <summary>
    /// Creates a pipeline graph from a pipeline, ensuring proper initialization.
    /// </summary>
    public static PipelineGraph CreatePipelineGraph(Pipeline pipeline)
    {
        // Ensure the pipeline has a proper root step with an ID
        var rootStep = new Sequence { Id = "Root" };
        pipeline.Root = rootStep;

        // Create a StepNode with the root step
        var rootNode = new StepNode(rootStep, "Root");

        // Create and return the PipelineGraph
        return new PipelineGraph(pipeline, rootNode, new[] { rootNode });
    }
}
