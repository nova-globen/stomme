#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Modules.Pipelines.Persistence.Raven
{
    /// <summary>
    /// The RavenDB shape of an engine entity (BL-95 increment (a), D111): every scalar the engine's filters test
    /// is a plain field with the entity's own name, so the hand-written query translation reads like the filter
    /// it mirrors; every non-scalar member is the SAME text the relational columns hold, produced by
    /// <see cref="EngineEntitySerializer"/>. Timestamps are UTC <see cref="DateTime"/>s: RavenDB orders and
    /// compares them as ISO text, and one format on both sides of a comparison is what makes that sound.
    /// </summary>
    internal abstract class EngineDocument
    {
        public string Id { get; set; } = null!;

        public string TenantId { get; set; } = string.Empty;

        protected static DateTime Utc(DateTimeOffset value) => value.UtcDateTime;

        protected static DateTime? Utc(DateTimeOffset? value) => value?.UtcDateTime;

        protected static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

        protected static DateTimeOffset? Offset(DateTime? value) => value is null ? null : Offset(value.Value);
    }

    internal sealed class PipelineDefinitionDocument : EngineDocument
    {
        public string DefinitionId { get; set; } = null!;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? ToolVersion { get; set; }
        public bool? UsableAsStep { get; set; }
        public string? ProviderName { get; set; }
        public string MaterializerName { get; set; } = null!;
        public string? MaterializerContext { get; set; }
        public string? StringData { get; set; }
        public string? OriginalSource { get; set; }
        public byte[]? BinaryData { get; set; }
        public bool IsReadonly { get; set; }
        public bool IsSystem { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
        public bool IsLatest { get; set; }
        public bool IsPublished { get; set; }
        public string? Data { get; set; }

        public static PipelineDefinitionDocument From(PipelineDefinition d, EngineEntitySerializer serializer) => new()
        {
            Id = d.Id,
            TenantId = d.TenantId ?? string.Empty,
            DefinitionId = d.DefinitionId,
            Name = d.Name,
            Description = d.Description,
            ToolVersion = d.ToolVersion?.ToString(),
            UsableAsStep = d.Options.UsableAsStep,
            ProviderName = d.ProviderName,
            MaterializerName = d.MaterializerName,
            MaterializerContext = d.MaterializerContext,
            StringData = d.StringData,
            OriginalSource = d.OriginalSource,
            BinaryData = d.BinaryData,
            IsReadonly = d.IsReadonly,
            IsSystem = d.IsSystem,
            CreatedAt = Utc(d.CreatedAt),
            Version = d.Version,
            IsLatest = d.IsLatest,
            IsPublished = d.IsPublished,
            Data = serializer.DefinitionDataOf(d, includeOptions: true),
        };

        public PipelineDefinition ToEntity(EngineEntitySerializer serializer)
        {
            var d = new PipelineDefinition
            {
                Id = Id,
                TenantId = TenantId,
                DefinitionId = DefinitionId,
                Name = Name,
                Description = Description,
                ToolVersion = ToolVersion is null ? null : System.Version.Parse(ToolVersion),
                ProviderName = ProviderName,
                MaterializerName = MaterializerName,
                MaterializerContext = MaterializerContext,
                StringData = StringData,
                OriginalSource = OriginalSource,
                BinaryData = BinaryData,
                IsReadonly = IsReadonly,
                IsSystem = IsSystem,
                CreatedAt = Offset(CreatedAt),
                Version = Version,
                IsLatest = IsLatest,
                IsPublished = IsPublished,
            };
            serializer.ApplyDefinitionData(d, Data);
            return d;
        }
    }

    internal sealed class PipelineInstanceDocument : EngineDocument
    {
        public string DefinitionId { get; set; } = null!;
        public string DefinitionVersionId { get; set; } = null!;
        public int Version { get; set; }
        public string? ParentPipelineInstanceId { get; set; }
        public PipelineStatus Status { get; set; }
        public PipelineSubStatus SubStatus { get; set; }
        public bool IsExecuting { get; set; }
        public string? CorrelationId { get; set; }
        public string? Name { get; set; }
        public int IncidentCount { get; set; }
        public bool IsSystem { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Data { get; set; }

        public static PipelineInstanceDocument From(PipelineInstance i, EngineEntitySerializer serializer) => new()
        {
            Id = i.Id,
            TenantId = i.TenantId ?? string.Empty,
            DefinitionId = i.DefinitionId,
            DefinitionVersionId = i.DefinitionVersionId,
            Version = i.Version,
            ParentPipelineInstanceId = i.ParentPipelineInstanceId,
            Status = i.Status,
            SubStatus = i.SubStatus,
            IsExecuting = i.IsExecuting,
            CorrelationId = i.CorrelationId,
            Name = i.Name,
            IncidentCount = i.IncidentCount,
            IsSystem = i.IsSystem,
            CreatedAt = Utc(i.CreatedAt),
            UpdatedAt = Utc(i.UpdatedAt),
            FinishedAt = Utc(i.FinishedAt),
            Data = serializer.StateOf(i),
        };

        /// <param name="withState">False for a summary, which never reads the state and should not pay for it.</param>
        public PipelineInstance ToEntity(EngineEntitySerializer serializer, bool withState = true) => new()
        {
            Id = Id,
            TenantId = TenantId,
            DefinitionId = DefinitionId,
            DefinitionVersionId = DefinitionVersionId,
            Version = Version,
            ParentPipelineInstanceId = ParentPipelineInstanceId,
            Status = Status,
            SubStatus = SubStatus,
            IsExecuting = IsExecuting,
            CorrelationId = CorrelationId,
            Name = Name,
            IncidentCount = IncidentCount,
            IsSystem = IsSystem,
            CreatedAt = Offset(CreatedAt),
            UpdatedAt = Offset(UpdatedAt),
            FinishedAt = Offset(FinishedAt),
            PipelineState = withState ? serializer.StateFrom(Data) : new PipelineState(),
        };
    }

    internal sealed class BookmarkDocument : EngineDocument
    {
        public string? Name { get; set; }
        public string Hash { get; set; } = null!;
        public string PipelineInstanceId { get; set; } = null!;
        public string? StepInstanceId { get; set; }
        public string? CorrelationId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Data { get; set; }
        public string? Metadata { get; set; }

        public static BookmarkDocument From(StoredBookmark b, EngineEntitySerializer serializer) => new()
        {
            Id = b.Id,
            TenantId = b.TenantId ?? string.Empty,
            Name = b.Name,
            Hash = b.Hash,
            PipelineInstanceId = b.PipelineInstanceId,
            StepInstanceId = b.StepInstanceId,
            CorrelationId = b.CorrelationId,
            CreatedAt = Utc(b.CreatedAt),
            Data = serializer.PayloadOf(b.Payload),
            Metadata = serializer.PayloadOf(b.Metadata),
        };

        public StoredBookmark ToEntity(EngineEntitySerializer serializer) => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            Hash = Hash,
            PipelineInstanceId = PipelineInstanceId,
            StepInstanceId = StepInstanceId,
            CorrelationId = CorrelationId,
            CreatedAt = Offset(CreatedAt),
            Payload = serializer.PayloadFrom(Data),
            Metadata = serializer.PayloadFrom<Dictionary<string, string>>(Metadata),
        };
    }

    internal sealed class TriggerDocument : EngineDocument
    {
        public string PipelineDefinitionId { get; set; } = null!;
        public string PipelineDefinitionVersionId { get; set; } = null!;
        public string? Name { get; set; }
        public string StepId { get; set; } = null!;
        public string? Hash { get; set; }
        public string? Data { get; set; }

        public static TriggerDocument From(StoredTrigger t, EngineEntitySerializer serializer) => new()
        {
            Id = t.Id,
            TenantId = t.TenantId ?? string.Empty,
            PipelineDefinitionId = t.PipelineDefinitionId,
            PipelineDefinitionVersionId = t.PipelineDefinitionVersionId,
            Name = t.Name,
            StepId = t.StepId,
            Hash = t.Hash,
            Data = serializer.PayloadOf(t.Payload),
        };

        public StoredTrigger ToEntity(EngineEntitySerializer serializer) => new()
        {
            Id = Id,
            TenantId = TenantId,
            PipelineDefinitionId = PipelineDefinitionId,
            PipelineDefinitionVersionId = PipelineDefinitionVersionId,
            Name = Name,
            StepId = StepId,
            Hash = Hash,
            Payload = serializer.PayloadFrom(Data),
        };
    }

    internal sealed class BookmarkQueueDocument : EngineDocument
    {
        public string? PipelineInstanceId { get; set; }
        public string? CorrelationId { get; set; }
        public string? BookmarkId { get; set; }
        public string? StimulusHash { get; set; }
        public string? StepInstanceId { get; set; }
        public string? StepTypeName { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Data { get; set; }

        public static BookmarkQueueDocument From(BookmarkQueueItem q, EngineEntitySerializer serializer) => new()
        {
            Id = q.Id,
            TenantId = q.TenantId ?? string.Empty,
            PipelineInstanceId = q.PipelineInstanceId,
            CorrelationId = q.CorrelationId,
            BookmarkId = q.BookmarkId,
            StimulusHash = q.StimulusHash,
            StepInstanceId = q.StepInstanceId,
            StepTypeName = q.StepTypeName,
            CreatedAt = Utc(q.CreatedAt),
            Data = serializer.PayloadOf(q.Options),
        };

        public BookmarkQueueItem ToEntity(EngineEntitySerializer serializer) => new()
        {
            Id = Id,
            TenantId = TenantId,
            PipelineInstanceId = PipelineInstanceId,
            CorrelationId = CorrelationId,
            BookmarkId = BookmarkId,
            StimulusHash = StimulusHash,
            StepInstanceId = StepInstanceId,
            StepTypeName = StepTypeName,
            CreatedAt = Offset(CreatedAt),
            Options = serializer.PayloadFrom<ResumeBookmarkOptions>(Data),
        };
    }

    internal sealed class StepExecutionDocument : EngineDocument
    {
        public string PipelineInstanceId { get; set; } = null!;
        public string StepId { get; set; } = null!;
        public string StepNodeId { get; set; } = null!;
        public string StepType { get; set; } = null!;
        public int StepTypeVersion { get; set; }
        public string? StepName { get; set; }
        public DateTime StartedAt { get; set; }
        public bool HasBookmarks { get; set; }
        public StepStatus Status { get; set; }
        public int AggregateFaultCount { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsCompleted { get; set; }
        public string? SchedulingStepExecutionId { get; set; }
        public string? SchedulingStepId { get; set; }
        public string? SchedulingPipelineInstanceId { get; set; }
        public int? CallStackDepth { get; set; }
        public string? StepState { get; set; }
        public string? Payload { get; set; }
        public string? Outputs { get; set; }
        public string? Properties { get; set; }
        public string? Metadata { get; set; }
        public string? Exception { get; set; }

        public static StepExecutionDocument From(StepExecutionRecord r, EngineEntitySerializer serializer) => new()
        {
            Id = r.Id,
            TenantId = r.TenantId ?? string.Empty,
            PipelineInstanceId = r.PipelineInstanceId,
            StepId = r.StepId,
            StepNodeId = r.StepNodeId,
            StepType = r.StepType,
            StepTypeVersion = r.StepTypeVersion,
            StepName = r.StepName,
            StartedAt = Utc(r.StartedAt),
            HasBookmarks = r.HasBookmarks,
            Status = r.Status,
            AggregateFaultCount = r.AggregateFaultCount,
            CompletedAt = Utc(r.CompletedAt),
            IsCompleted = r.CompletedAt is not null,
            SchedulingStepExecutionId = r.SchedulingStepExecutionId,
            SchedulingStepId = r.SchedulingStepId,
            SchedulingPipelineInstanceId = r.SchedulingPipelineInstanceId,
            CallStackDepth = r.CallStackDepth,
            StepState = serializer.SafeOf(r.StepState),
            Payload = serializer.PayloadOf(r.Payload),
            Outputs = serializer.SafeOf(r.Outputs),
            Properties = serializer.PayloadOf(r.Properties),
            Metadata = serializer.PayloadOf(r.Metadata),
            Exception = serializer.PayloadOf(r.Exception),
        };

        public StepExecutionRecord ToEntity(EngineEntitySerializer serializer) => new()
        {
            Id = Id,
            TenantId = TenantId,
            PipelineInstanceId = PipelineInstanceId,
            StepId = StepId,
            StepNodeId = StepNodeId,
            StepType = StepType,
            StepTypeVersion = StepTypeVersion,
            StepName = StepName,
            StartedAt = Offset(StartedAt),
            HasBookmarks = HasBookmarks,
            Status = Status,
            AggregateFaultCount = AggregateFaultCount,
            CompletedAt = Offset(CompletedAt),
            SchedulingStepExecutionId = SchedulingStepExecutionId,
            SchedulingStepId = SchedulingStepId,
            SchedulingPipelineInstanceId = SchedulingPipelineInstanceId,
            CallStackDepth = CallStackDepth,
            StepState = serializer.SafeFrom<Dictionary<string, object?>>(StepState),
            Payload = serializer.PayloadFrom<Dictionary<string, object>>(Payload),
            Outputs = serializer.SafeFrom<Dictionary<string, object?>>(Outputs),
            Properties = serializer.PayloadFrom<Dictionary<string, object>>(Properties),
            Metadata = serializer.PayloadFrom<Dictionary<string, object>>(Metadata),
            Exception = serializer.PayloadFrom<ExceptionState>(Exception),
        };
    }

    internal sealed class ExecutionLogDocument : EngineDocument
    {
        public string PipelineDefinitionId { get; set; } = null!;
        public string PipelineDefinitionVersionId { get; set; } = null!;
        public string PipelineInstanceId { get; set; } = null!;
        public int PipelineVersion { get; set; }
        public string StepInstanceId { get; set; } = null!;
        public string? ParentStepInstanceId { get; set; }
        public string StepId { get; set; } = null!;
        public string StepType { get; set; } = null!;
        public int StepTypeVersion { get; set; }
        public string? StepName { get; set; }
        public string StepNodeId { get; set; } = null!;
        public DateTime Timestamp { get; set; }
        public long Sequence { get; set; }
        public string? EventName { get; set; }
        public string? Message { get; set; }
        public string? Source { get; set; }
        public string? StepState { get; set; }
        public string? Payload { get; set; }

        public static ExecutionLogDocument From(PipelineExecutionLogRecord r, EngineEntitySerializer serializer) => new()
        {
            Id = r.Id,
            TenantId = r.TenantId ?? string.Empty,
            PipelineDefinitionId = r.PipelineDefinitionId,
            PipelineDefinitionVersionId = r.PipelineDefinitionVersionId,
            PipelineInstanceId = r.PipelineInstanceId,
            PipelineVersion = r.PipelineVersion,
            StepInstanceId = r.StepInstanceId,
            ParentStepInstanceId = r.ParentStepInstanceId,
            StepId = r.StepId,
            StepType = r.StepType,
            StepTypeVersion = r.StepTypeVersion,
            StepName = r.StepName,
            StepNodeId = r.StepNodeId,
            Timestamp = Utc(r.Timestamp),
            Sequence = r.Sequence,
            EventName = r.EventName,
            Message = r.Message,
            Source = r.Source,
#pragma warning disable CS0618 // the obsolete StepState is still persisted for readers that have not moved
            StepState = serializer.PayloadOf(r.StepState),
#pragma warning restore CS0618
            Payload = serializer.PayloadOf(r.Payload),
        };

        public PipelineExecutionLogRecord ToEntity(EngineEntitySerializer serializer) => new()
        {
            Id = Id,
            TenantId = TenantId,
            PipelineDefinitionId = PipelineDefinitionId,
            PipelineDefinitionVersionId = PipelineDefinitionVersionId,
            PipelineInstanceId = PipelineInstanceId,
            PipelineVersion = PipelineVersion,
            StepInstanceId = StepInstanceId,
            ParentStepInstanceId = ParentStepInstanceId,
            StepId = StepId,
            StepType = StepType,
            StepTypeVersion = StepTypeVersion,
            StepName = StepName,
            StepNodeId = StepNodeId,
            Timestamp = Offset(Timestamp),
            Sequence = Sequence,
            EventName = EventName,
            Message = Message,
            Source = Source,
#pragma warning disable CS0618
            StepState = serializer.PayloadFrom<Dictionary<string, object>>(StepState),
#pragma warning restore CS0618
            Payload = serializer.PayloadFrom(Payload),
        };
    }

    internal sealed class KeyValueDocument : EngineDocument
    {
        public string SerializedValue { get; set; } = null!;

        public static KeyValueDocument From(SerializedKeyValuePair p) => new()
        {
            Id = p.Id,
            TenantId = p.TenantId ?? string.Empty,
            SerializedValue = p.SerializedValue,
        };

        public SerializedKeyValuePair ToEntity() => new()
        {
            Id = Id,
            TenantId = TenantId,
            SerializedValue = SerializedValue,
        };
    }
}
