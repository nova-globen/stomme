using Stomme.Engine.Pipelines.Steps;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for <see cref="Pipeline"/>.
/// </summary>  
public static class PipelineExtensions
{
    extension(Pipeline pipeline)
    {
        /// <summary>
        /// Returns a boolean indicating whether the pipeline was created with modern tooling.
        /// </summary>
        public bool CreatedWithModernTooling() => pipeline.PipelineMetadata.ToolVersion?.Major >= 3;

        /// <summary>
        /// Executes the specified action depending on whether the pipeline was created with modern tooling or not.
        /// </summary>
        public void WhenCreatedWithModernTooling(Action modernToolingAction, Action legacyToolingAction)
        {
            if (pipeline.CreatedWithModernTooling())
                modernToolingAction();
            else
                legacyToolingAction();
        }
    }
}