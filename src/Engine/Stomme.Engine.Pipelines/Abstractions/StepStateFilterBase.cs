using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

[UsedImplicitly]
public abstract class StepStateFilterBase : IStepStateFilter
{
    protected virtual Task<StepStateFilterResult> OnExecuteAsync(StepStateFilterContext context)
    {
        var result = OnExecute(context);
        return Task.FromResult(result);
    }

    protected virtual StepStateFilterResult OnExecute(StepStateFilterContext context)
    {
        return Pass();
    }
    
    protected StepStateFilterResult Pass() => StepStateFilterResult.Pass();
    protected StepStateFilterResult Filtered(string filteredValue) => StepStateFilterResult.Filtered(filteredValue);

    Task<StepStateFilterResult> IStepStateFilter.ExecuteAsync(StepStateFilterContext context)
    {
        return OnExecuteAsync(context);
    }
}