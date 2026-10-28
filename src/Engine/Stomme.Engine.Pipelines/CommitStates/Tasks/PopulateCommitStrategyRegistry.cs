using Stomme.Engine.Common;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.CommitStates.Tasks;

[UsedImplicitly]
public class PopulateCommitStrategyRegistry(ICommitStrategyRegistry registry, IOptions<CommitStateOptions> options) : IStartupTask
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var strategy in options.Value.PipelineCommitStrategies.Values) registry.RegisterStrategy(strategy);
        foreach (var strategy in options.Value.StepCommitStrategies.Values) registry.RegisterStrategy(strategy);
        return Task.CompletedTask;
    }
}