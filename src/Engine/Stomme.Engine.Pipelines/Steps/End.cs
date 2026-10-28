using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Marks the end of a flowchart, causing the flowchart to complete.
/// </summary>
[Step("Stomme", "Flow", "A milestone step that marks the end of a flowchart.", Kind = StepKind.Action)]
[PublicAPI]
public class End : CodeStep, ITerminalNode
{
    /// <inheritdoc />
    public End([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
}