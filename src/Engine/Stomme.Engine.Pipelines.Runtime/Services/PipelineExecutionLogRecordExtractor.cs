using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineExecutionLogRecordExtractor(IIdentityGenerator identityGenerator) : ILogRecordExtractor<PipelineExecutionLogRecord>
{
    /// <inheritdoc />
    public Task<IEnumerable<PipelineExecutionLogRecord>> ExtractLogRecordsAsync(PipelineExecutionContext context)
    {
        var records = context.ExecutionLog.Select(x => new PipelineExecutionLogRecord
        {
            Id = identityGenerator.GenerateId(),
            StepInstanceId = x.StepInstanceId,
            ParentStepInstanceId = x.ParentStepInstanceId,
            StepNodeId = x.NodeId,
            StepId = x.StepId,
            StepType = x.StepType,
            StepTypeVersion = x.StepTypeVersion,
            StepName = x.StepName,
            Message = x.Message,
            EventName = x.EventName,
            PipelineDefinitionId = context.Pipeline.Identity.DefinitionId,
            PipelineDefinitionVersionId = context.Pipeline.Identity.Id,
            PipelineInstanceId = context.Id,
            PipelineVersion = context.Pipeline.Version,
            Source = x.Source,
            Payload = x.Payload,
            Timestamp = x.Timestamp,
            Sequence = x.Sequence
        });
        
        return Task.FromResult(records);
    }
}