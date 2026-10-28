using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Marks the start of a flowchart.
/// </summary>
[Step("Stomme", "Flow", "A milestone step that marks the start of a flowchart.", Kind = StepKind.Action)]
[PublicAPI]
public class Start : CodeStep, IStartNode
{
    /// <inheritdoc />
    public Start([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
}