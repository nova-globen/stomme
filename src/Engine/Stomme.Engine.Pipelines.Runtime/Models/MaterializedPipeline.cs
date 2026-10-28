using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a pipeline definition and its pipeline.
/// </summary>
/// <param name="Pipeline">The pipeline materialized from its pipeline definition.</param>
/// <param name="ProviderName">The name of the provider that provided the pipeline definition.</param>
/// <param name="MaterializerName">The name of the materializer that materialized the pipeline.</param>
/// <param name="MaterializerContext">The context of the materializer that materialized the pipeline.</param>
/// <param name="OriginalSource">The original source representation (JSON, EngineScript, YAML, etc.). When present, enables symmetric round-trip preservation without re-serialization.</param>
public record MaterializedPipeline(Pipeline Pipeline, string ProviderName, string MaterializerName, object? MaterializerContext = null, string? OriginalSource = null);