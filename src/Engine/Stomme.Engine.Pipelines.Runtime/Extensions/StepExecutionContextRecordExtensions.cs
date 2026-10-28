using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static class StepExecutionContextRecordExtensions
{
    private const string StepExecutionRecordKey = "CapturedStepExecutionRecord";

    /// <summary>
    /// Captures the step execution record for the provided <see cref="StepExecutionContext"/> and stores it in the context's transient properties.
    /// </summary>
    public static async Task CaptureStepExecutionRecordAsync(this StepExecutionContext context)
    {
        var mapper = context.GetRequiredService<IStepExecutionMapper>();
        var record = await mapper.MapAsync(context).ConfigureAwait(false);
        context.TransientProperties[StepExecutionRecordKey] = record;
    }

    /// <summary>
    /// Retrieves the captured step execution record from the transient properties of the provided <see cref="StepExecutionContext"/>.
    /// If the record is not found, it maps and returns a new step execution record using the <see cref="IStepExecutionMapper"/> service.
    /// </summary>
    public static async Task<StepExecutionRecord> GetOrMapCapturedStepExecutionRecordAsync(this StepExecutionContext context)
    {
        // If the record is already captured in the transient properties, return it, as it will contain the serialized state of the step execution at the time of capture, rather than the current state.
        // This is useful for scenarios where the step execution state may change after the record is captured, such as referenced pipeline variables.
        if (context.TransientProperties.TryGetValue(StepExecutionRecordKey, out var capturedRecord))
            return (StepExecutionRecord)capturedRecord;

        // If the record is not captured, map a new step execution record using the mapper.
        var mapper = context.GetRequiredService<IStepExecutionMapper>();
        var record = await mapper.MapAsync(context).ConfigureAwait(false);

        return record;
    }
}