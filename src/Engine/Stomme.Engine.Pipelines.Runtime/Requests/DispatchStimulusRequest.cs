namespace Stomme.Engine.Pipelines.Runtime.Requests;

public class DispatchStimulusRequest
{
    public string? StepTypeName { get; set; }
    public object? Stimulus { get; set; }
    public string? StimulusHash { get; set; }
    public StimulusMetadata? Metadata { get; set; }
}