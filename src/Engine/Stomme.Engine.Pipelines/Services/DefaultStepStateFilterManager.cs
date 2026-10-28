namespace Stomme.Engine.Pipelines;

public class DefaultStepStateFilterManager(IEnumerable<IStepStateFilter> filters) : IStepStateFilterManager
{
    public async Task<string> RunFiltersAsync(StepStateFilterContext context)
    {
        foreach (var filter in filters)
        {
            var result = await filter.ExecuteAsync(context).ConfigureAwait(false);
            
            if (result.IsFiltered)
                return result.FilteredValue!;
        }

        return context.Value.ToString();
    }
}