namespace Stomme.Engine.Pipelines;

public interface IStepStateFilter
{
    Task<StepStateFilterResult> ExecuteAsync(StepStateFilterContext context);
}