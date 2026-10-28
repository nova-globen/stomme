using System.Runtime.CompilerServices;
using Stomme.Engine.Pipelines.Attributes;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Schedule a step for each item in parallel.
/// </summary>
[Step("Stomme", "Looping", "Schedule a step for each item in parallel.")]
public class ParallelForEach : ParallelForEach<object>
{
    /// <inheritdoc />
    public ParallelForEach([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }
}