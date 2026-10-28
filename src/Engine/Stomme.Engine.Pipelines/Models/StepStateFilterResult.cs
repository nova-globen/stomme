using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines;

[UsedImplicitly]
public class StepStateFilterResult
{
    public string? FilteredValue { get; set; }
    public bool IsFiltered { get; set; }
    
    public static StepStateFilterResult Filtered(string filteredValue) => new() { IsFiltered = true, FilteredValue = filteredValue };
    public static StepStateFilterResult Pass() => new() { IsFiltered = false };
}