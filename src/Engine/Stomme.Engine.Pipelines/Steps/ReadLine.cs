using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Read a line of text from the console.
/// </summary>
[Step("Stomme", "Console", "Read a line of text from the console.")]
public class ReadLine : CodeStep<string>
{
    /// <inheritdoc />
    public ReadLine([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public ReadLine(MemoryBlockReference output, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(output, source, line)
    {
    }

    /// <inheritdoc />
    public ReadLine(Output<string>? output, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(output, source, line)
    {
    }

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var provider = context.GetService<IStandardInStreamProvider>() ?? new StandardInStreamProvider(Console.In);
        var reader = provider.GetTextReader();
        var text = reader.ReadLine()!;
        context.Set(Result, text);
    }
}