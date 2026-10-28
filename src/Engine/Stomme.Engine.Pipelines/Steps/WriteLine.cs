using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
///  Write a line of text to the console.
/// </summary>
[Step("Stomme", "Console", "Write a line of text to the console.")]
public class WriteLine : CodeStep
{
    /// <inheritdoc />
    [JsonConstructor]
    private WriteLine(string? source = null, int? line = null) : base(source, line)
    {
    }

    /// <inheritdoc />
    public WriteLine(string text, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(new Literal<string>(text), source, line)
    {
    }

    /// <inheritdoc />
    public WriteLine(Func<string> text, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : this(Expression.DelegateExpression(text), source, line)
    {
    }

    /// <inheritdoc />
    public WriteLine(Func<ExpressionExecutionContext, string?> text, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) 
        : this(Expression.DelegateExpression(text), source, line)
    {
    }

    /// <inheritdoc />
    public WriteLine(Variable<string> variable, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line) => Text = new Input<string>(variable);

    /// <inheritdoc />
    public WriteLine(Literal<string> literal, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line) => Text = new Input<string>(literal);

    /// <inheritdoc />
    public WriteLine(Expression expression, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line) => Text = new Input<string>(expression, new MemoryBlockReference());

    /// <inheritdoc />
    public WriteLine(Input<string> text, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : this(source, line) => Text = text;
        
    /// <summary>
    /// The text to write.
    /// </summary>
    [Description("The text to write.")]
    public Input<string> Text { get; set; } = null!;

    /// <inheritdoc />
    protected override void Execute(StepExecutionContext context)
    {
        var text = context.Get(Text);
        var provider = context.GetService<IStandardOutStreamProvider>() ?? new StandardOutStreamProvider(System.Console.Out);
        var textWriter = provider.GetTextWriter();
        textWriter.WriteLine(text);
    }
}