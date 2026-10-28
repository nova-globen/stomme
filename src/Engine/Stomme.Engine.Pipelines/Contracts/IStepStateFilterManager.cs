namespace Stomme.Engine.Pipelines;

public interface IStepStateFilterManager
{
    Task<string> RunFiltersAsync(StepStateFilterContext context);
}