using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines;

public class PipelineIndexingContext
{
    public PipelineIndexingContext(Pipeline pipeline, CancellationToken cancellationToken)
    {
        Pipeline = pipeline;
        CancellationToken = cancellationToken;
    }

    public Pipeline Pipeline { get; }
    public IDictionary<ITrigger, MemoryRegister> Registers { get; } = new Dictionary<ITrigger, MemoryRegister>();
    public CancellationToken CancellationToken { get; }

    public MemoryRegister GetOrCreateRegister(ITrigger step)
    {
        if (!Registers.TryGetValue(step, out var register))
        {
            register = Pipeline.CreateRegister();
            Registers[step] = register;
        }

        return register;
    }
}