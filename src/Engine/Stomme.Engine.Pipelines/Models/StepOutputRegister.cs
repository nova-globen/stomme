namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Stores step output.
/// </summary>
public class StepOutputRegister
{
    private readonly Dictionary<string, List<StepOutputRecord>> _recordsByStepIdAndOutputName = new();
    private readonly Dictionary<string, StepOutputRecord> _recordsByStepInstanceIdAndOutputName = new();

    /// <summary>
    /// The default output name.
    /// </summary>
    public const string DefaultOutputName = "Result";

    /// <summary>
    /// Records a step's output.
    /// </summary>
    /// <param name="stepExecutionContext">The step execution context.</param>
    /// <param name="outputValue">The output value.</param>
    public void Record(StepExecutionContext stepExecutionContext, object? outputValue)
    {
        Record(stepExecutionContext, null, outputValue);
    }

    /// <summary>
    /// Records a step's output.
    /// </summary>
    /// <param name="stepExecutionContext">The step execution context.</param>
    /// <param name="outputName">The name of the output. Defaults to "Result"</param>
    /// <param name="outputValue">The output value.</param>
    public void Record(StepExecutionContext stepExecutionContext, string? outputName, object? outputValue)
    {
        var stepId = stepExecutionContext.Step.Id;
        var stepInstanceId = stepExecutionContext.Id;
        var containerId = stepExecutionContext.ParentStepExecutionContext?.Id ?? stepExecutionContext.PipelineExecutionContext.Id;

        outputName ??= DefaultOutputName;

        // Inspect the output descriptor to see if the specified output name matches any PropertyInfo's name.
        // If so, use that descriptor's name instead.
        var outputDescriptor = stepExecutionContext.StepDescriptor.Outputs.FirstOrDefault(x => x.PropertyInfo?.Name == outputName);

        if (outputDescriptor != null)
            outputName = outputDescriptor.Name;

        var record = new StepOutputRecord(containerId, stepId, stepInstanceId, outputName, outputValue);

        _recordsByStepInstanceIdAndOutputName[CreateStepInstanceIdLookupKey(stepInstanceId, outputName)] = record;

        var scopedRecordsKey = CreateStepIdLookupKey(stepId, outputName);

        if (!_recordsByStepIdAndOutputName.TryGetValue(scopedRecordsKey, out var scopedRecords))
        {
            scopedRecords = new();
            _recordsByStepIdAndOutputName[scopedRecordsKey] = scopedRecords;
        }

        scopedRecords.Add(record);
    }

    /// <summary>
    ///  Finds all output records for the specified step ID and output name.
    /// </summary>
    public IEnumerable<StepOutputRecord> FindMany(string stepId, string? outputName = null)
    {
        var key = CreateStepIdLookupKey(stepId, outputName);
        return _recordsByStepIdAndOutputName.TryGetValue(key, out var records) ? records : Enumerable.Empty<StepOutputRecord>();
    }

    /// <summary>
    /// Gets the output value for the specified step ID.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="outputName">Name of the output.</param>
    /// <returns>The output value.</returns>
    public object? FindOutputByStepId(string stepId, string? outputName = null)
    {
        var key = CreateStepIdLookupKey(stepId, outputName);
        return !_recordsByStepIdAndOutputName.TryGetValue(key, out var records)
            ? null
            : records.LastOrDefault()?.Value; // Always return the last value.
    }

    /// <summary>
    /// Gets the output value for the specified step instance ID.
    /// </summary>
    /// <param name="stepInstanceId">The step instance ID.</param>
    /// <param name="outputName"></param>
    /// <returns>The output value.</returns>
    public object? FindOutputByStepInstanceId(string stepInstanceId, string? outputName = null)
    {
        var key = CreateStepInstanceIdLookupKey(stepInstanceId, outputName);
        return !_recordsByStepInstanceIdAndOutputName.TryGetValue(key, out var record)
            ? null
            : record.Value;
    }

    private string CreateStepIdLookupKey(string stepId, string? outputName) => $"{stepId}:{outputName ?? DefaultOutputName}";
    private string CreateStepInstanceIdLookupKey(string stepInstanceId, string? outputName) => $"{stepInstanceId}:{outputName ?? DefaultOutputName}";
}