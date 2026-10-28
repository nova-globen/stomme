namespace Stomme.Engine.Pipelines.Models;

public class BackgroundExecutionResult
{
    public ICollection<BackgroundExecutionOutcome> Outcomes { get; set; } = new List<BackgroundExecutionOutcome>();
    public ICollection<PipelineExecutionLogEntry> ExecutionLog { get; set; } = new List<PipelineExecutionLogEntry>();
    public IDictionary<string, object?> JournalData { get; } = new Dictionary<string, object?>();
}