using Stomme.Engine.Pipelines;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IStep"/>.
/// </summary>
public static class StepPropertyExtensions
{
    private static readonly string[] CanStartPipelinePropertyName = ["canStartPipeline", "CanStartPipeline"];
    private static readonly string[] RunAsynchronouslyPropertyName = ["runAsynchronously", "RunAsynchronously"];
    private static readonly string[] SourcePropertyName = ["source", "Source"];
    private static readonly string[] CommitStrategyName = ["commitStrategyName", "CommitStrategyName"];

    extension(IStep step)
    {
        /// <summary>
        /// Gets a flag indicating whether this step can be used for starting a pipeline.
        /// Usually used for triggers, but also used to disambiguate between two or more starting steps and no starting step was specified.
        /// </summary>
        public bool GetCanStartPipeline() => step.CustomProperties.GetValueOrDefault(CanStartPipelinePropertyName, () => false);

        /// <summary>
        /// Sets a flag indicating whether this step can be used for starting a pipeline.
        /// </summary>
        public void SetCanStartPipeline(bool value) => step.CustomProperties[CanStartPipelinePropertyName[0]] = value;

        /// <summary>
        /// Gets a flag indicating if this step should execute synchronously or asynchronously.
        /// By default, steps with an <see cref="StepKind"/> of <see cref="Action"/>, <see cref="Task"/> or <see cref="Trigger"/>
        /// will execute synchronously, while steps of the <see cref="StepKind.Job"/> kind will execute asynchronously.
        /// </summary>
        public bool? GetRunAsynchronously()
        {
            return step.CustomProperties.GetValueOrDefault<bool?>(RunAsynchronouslyPropertyName, defaultValueFactory: () => null);
        }

        /// <summary>
        /// Sets a flag indicating if this step should execute synchronously or asynchronously.
        /// By default, steps with an <see cref="StepKind"/> of <see cref="Action"/>, <see cref="Task"/> or <see cref="Trigger"/>
        /// will execute synchronously, while steps of the <see cref="StepKind.Job"/> kind will execute asynchronously.
        /// </summary>
        public void SetRunAsynchronously(bool? value) => step.CustomProperties[RunAsynchronouslyPropertyName[0]] = value ?? false;

        /// <summary>
        /// Gets the source file and line number where this step was instantiated, if any.
        /// </summary>
        public string? GetSource() => step.CustomProperties.GetValueOrDefault(SourcePropertyName, () => default(string?));

        /// <summary>
        /// Sets the source file and line number where this step was instantiated, if any.
        /// </summary>
        public void SetSource(string value) => step.CustomProperties[SourcePropertyName[0]] = value;

        /// <summary>
        /// Gets the commit state behavior for the specified step.
        /// </summary>
        public string? GetCommitStrategy() => step.CustomProperties.GetValueOrDefault<string?>(CommitStrategyName, () => null);
    }

    /// <summary>
    /// Sets the commit state behavior for the specified step.
    /// </summary>
    public static TStep SetCommitStrategy<TStep>(this TStep step, string? name) where TStep : IStep
    {
        if (string.IsNullOrWhiteSpace(name))
            step.CustomProperties.Remove(CommitStrategyName[0]);
        else
            step.CustomProperties[CommitStrategyName[0]] = name;
        return step;
    }

    extension(IStep step)
    {
        /// <summary>
        /// Sets the source file and line number where this step was instantiated, if any.
        /// </summary>
        public void SetSource(string? sourceFile, int? lineNumber)
        {
            if (sourceFile == null || lineNumber == null)
                return;

            var source = $"{Path.GetFileName(sourceFile)}:{lineNumber}";
            step.SetSource(source);
        }

        /// <summary>
        /// Gets the display text for the specified step.
        /// </summary>
        public string? GetDisplayText() => step.Metadata.TryGetValue("displayText", out var value) ? value.ToString() : null;

        /// <summary>
        /// Sets the display text for the specified step.
        /// </summary>
        public void SetDisplayText(string value) => step.Metadata["displayText"] = value;

        /// <summary>
        /// Gets the description for the specified step.
        /// </summary>
        public string? GetDescription() => step.Metadata.TryGetValue("description", out var value) ? value.ToString() : null;

        /// <summary>
        /// Sets the description for the specified step.
        /// </summary>
        public void SetDescription(string value) => step.Metadata["description"] = value;
    }
}