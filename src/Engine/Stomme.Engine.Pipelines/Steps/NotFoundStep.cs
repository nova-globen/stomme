using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// This step is instantiated in case a pipeline references a step type that could not be found.
/// </summary>
[Browsable(false)]
[Step("Stomme", "System", "A placeholder step that will be used in case a pipeline definition references a step type that cannot be found.")]
[PublicAPI]
public class NotFoundStep : CodeStep
{
    /// <inheritdoc />
    public NotFoundStep([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public NotFoundStep(string missingTypeName, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line)
    {
        MissingTypeName = missingTypeName;
    }
    
    /// <summary>
    /// The type name of the missing step type.
    /// </summary>
    public string MissingTypeName { get; set; } = null!;
    
    /// <summary>
    /// The version of the missing step type.
    /// </summary>
    public int MissingTypeVersion { get; set; }

    /// <summary>
    /// The original step JSON.
    /// </summary>
    public string OriginalStepJson { get; set; } = null!;

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        throw new StepNotFoundException(MissingTypeName, MissingTypeVersion);
    }
}