using Stomme.Engine.Common;
using Stomme.Engine.Common.Codecs;
using Stomme.Engine.Pipelines.LogPersistence;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Extensions;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultStepExecutionMapper(
    ISafeSerializer safeSerializer,
    IPayloadSerializer payloadSerializer,
    ICompressionCodecResolver compressionCodecResolver,
    IOptions<ManagementOptions> options) : IStepExecutionMapper
{
    /// <inheritdoc />
    public async Task<StepExecutionRecord> MapAsync(StepExecutionContext source)
    {
        var outputs = source.GetOutputs();
        var inputs = source.GetInputs();
        var persistenceMap = source.GetLogPersistenceModeMap();
        var persistableInputs = GetPersistableInputOutput(inputs, persistenceMap.Inputs);
        var persistableOutputs = GetPersistableInputOutput(outputs, persistenceMap.Outputs);
        var persistableProperties = GetPersistableDictionary(source.Properties!, persistenceMap.InternalState);
        var persistableJournalData = GetPersistableDictionary(source.JournalData!, persistenceMap.InternalState);
        var cancellationToken = source.CancellationToken;

        var record = new StepExecutionRecord
        {
            Id = source.Id,
            StepId = source.Step.Id,
            StepNodeId = source.NodeId,
            PipelineInstanceId = source.PipelineExecutionContext.Id,
            StepType = source.Step.Type,
            StepName = source.Step.Name,
            StepState = persistableInputs,
            Outputs = persistableOutputs,
            Properties = persistableProperties!,
            Metadata = new Dictionary<string, object>(source.Metadata),
            Payload = persistableJournalData!,
            Exception = ExceptionState.FromException(source.Exception),
            StepTypeVersion = source.Step.Version,
            StartedAt = source.StartedAt,
            HasBookmarks = source.Bookmarks.Any(),
            Status = source.Status,
            AggregateFaultCount = source.AggregateFaultCount,
            CompletedAt = source.CompletedAt,
            SchedulingStepExecutionId = source.SchedulingStepExecutionId,
            SchedulingStepId = source.SchedulingStepId,
            SchedulingPipelineInstanceId = source.SchedulingPipelineInstanceId,
            CallStackDepth = source.CallStackDepth
        };

        record = record.SanitizeLogMessage();
        var compressionAlgorithm = options.Value.CompressionAlgorithm ?? nameof(None);
        var serializedStepState = record.StepState?.Count > 0 ? safeSerializer.Serialize(record.StepState) : null;
        var compressedSerializedStepState = serializedStepState != null ? await compressionCodecResolver.Resolve(compressionAlgorithm).CompressAsync(serializedStepState, cancellationToken).ConfigureAwait(false) : null;
        var serializedProperties = record.Properties != null ? payloadSerializer.Serialize(record.Properties) : null;
        var serializedMetadata = record.Metadata != null ? payloadSerializer.Serialize(record.Metadata) : null;
        record.SerializedSnapshot = new()
        {
            Id = record.Id,
            TenantId = record.TenantId,
            PipelineInstanceId = record.PipelineInstanceId,
            StepId = record.StepId,
            StepNodeId = record.StepNodeId,
            StepType = record.StepType,
            StepTypeVersion = record.StepTypeVersion,
            StepName = record.StepName,
            StartedAt = record.StartedAt,
            HasBookmarks = record.HasBookmarks,
            Status = record.Status,
            AggregateFaultCount = record.AggregateFaultCount,
            CompletedAt = record.CompletedAt,
            SerializedStepState = compressedSerializedStepState,
            SerializedStepStateCompressionAlgorithm = compressionAlgorithm,
            SerializedOutputs = record.Outputs?.Any() == true ? safeSerializer.Serialize(record.Outputs) : null,
            SerializedProperties = serializedProperties,
            SerializedMetadata = serializedMetadata,
            SerializedException = record.Exception != null ? payloadSerializer.Serialize(record.Exception) : null,
            SerializedPayload = record.Payload?.Any() == true ? payloadSerializer.Serialize(record.Payload) : null
        };
        
        return record;
    }

    private IDictionary<string, object?> GetPersistableInputOutput(IDictionary<string, object> state, IDictionary<string, LogPersistenceMode> map, bool deepCopy = false)
    {
        var result = new Dictionary<string, object?>();
        foreach (var stateEntry in state)
        {
            var mode = map.TryGetValue(stateEntry.Key, out var value) ? value : LogPersistenceMode.Include;
            if (mode == LogPersistenceMode.Include)
                result.Add(stateEntry.Key, stateEntry.Value);
        }

        return result;
    }

    private IDictionary<string, object?>? GetPersistableDictionary(IDictionary<string, object?> dictionary, LogPersistenceMode mode)
    {
        return mode == LogPersistenceMode.Include ? new Dictionary<string, object?>(dictionary) : null;
    }
}