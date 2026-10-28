using System.Runtime.CompilerServices;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Signals;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Break out of a loop.
/// </summary>
[Step("Stomme", "Looping", "Break out of a loop.")]
[PublicAPI]
public class Break : CodeStep, ITerminalNode
{
    /// <inheritdoc />
    public Break([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        // Send a signal to the parent scope to break out of the loop.
        await context.SendSignalAsync(new BreakSignal()).ConfigureAwait(false);
    }
}