using Stomme.Engine.Mediator.Middleware.Command.Contracts;

namespace Stomme.Engine.Mediator.Middleware.Command;

/// <inheritdoc />
public class CommandPipeline : ICommandPipeline
{
    private readonly CommandPipelineBuilder _builder;
    private CommandMiddlewareDelegate _executionPipeline = null!;

    /// <summary>
    /// Constructor.
    /// </summary>
    public CommandPipeline(IServiceProvider serviceProvider)
    {
        _builder = new(serviceProvider);
        Setup(x => x.UseCommandInvoker().UseCommandLogging());
    }

    /// <inheritdoc />
    public CommandMiddlewareDelegate Pipeline => _executionPipeline;

    /// <inheritdoc />
    public CommandMiddlewareDelegate Setup(Action<ICommandPipelineBuilder>? setup = null)
    {
        setup?.Invoke(_builder);
        _executionPipeline = _builder.Build();
        return _executionPipeline;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(CommandContext context) => await Pipeline(context).ConfigureAwait(false);
}