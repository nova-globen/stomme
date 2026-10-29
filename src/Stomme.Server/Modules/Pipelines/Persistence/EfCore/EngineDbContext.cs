#nullable enable
using System;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stomme.Modules.Pipelines.Persistence.EfCore
{
    /// <summary>
    /// The engine's eight store entities on the install's relational provider (BL-95 increment (a)). It lives
    /// in the module store's database (co-located when <c>ConnectionStrings:ModuleAggregates</c> is set, the SQLite
    /// sidecar otherwise) under <c>Engine*</c> tables, and is created and caught up additively by the same schema
    /// initializer the storage-blind modules use. Rules that are load-bearing:
    /// <list type="bullet">
    /// <item>One context per store OPERATION, from the factory - never held by a scope, so an Interactive Server
    /// circuit can never read a stale tracked instance, and every read is <c>AsNoTracking</c>.</item>
    /// <item>Every non-scalar member (an instance's state, payloads, variables, outputs...) is text in a SHADOW
    /// column written and read through <see cref="EngineEntitySerializer"/>; the entity types stay the engine's
    /// own, so the engine's filters apply to them verbatim.</item>
    /// <item>Every <see cref="DateTimeOffset"/> is stored as UTC ticks: SQLite cannot compare or order a
    /// DateTimeOffset column, and one representation on all three providers keeps their answers identical.</item>
    /// <item>Rows carry the engine's <c>TenantId</c> (normalised: the default tenant is the empty string) and a
    /// global query filter scopes every query to the tenant the engine's accessor names; a filter marked
    /// <c>TenantAgnostic</c> ignores it.</item>
    /// </list>
    /// </summary>
    public sealed class EngineDbContext : DbContext
    {
        // The text (shadow) columns. Named apart from the CLR members they carry: EF refuses a shadow property that
        // shares its name with an ignored CLR property of another type (measured on StoredBookmark.Metadata).
        public const string DataColumn = "Data";
        public const string MetadataColumn = "MetadataJson";
        public const string StepStateColumn = "StepStateJson";
        public const string PayloadColumn = "PayloadJson";
        public const string OutputsColumn = "OutputsJson";
        public const string PropertiesColumn = "PropertiesJson";
        public const string ExceptionColumn = "ExceptionJson";

        // The largest key a SQL Server index accepts (nvarchar(450)); every filtered string column gets it so the
        // index can exist on all three providers.
        private const int IndexedStringLength = 450;

        public EngineDbContext(DbContextOptions<EngineDbContext> options, ITenantAccessor tenants) : base(options)
        {
            TenantId = tenants.TenantId;
        }

        /// <summary>The engine tenant this context was opened for (the accessor's, normalised).</summary>
        public string TenantId { get; }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
            configurationBuilder.Properties<Version>().HaveConversion<VersionConverter>();
            configurationBuilder.Properties<Type>().HaveConversion<TypeConverter>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PipelineDefinition>(definition =>
            {
                definition.ToTable("EnginePipelineDefinitions");
                definition.HasKey(x => x.Id);
                definition.Ignore(x => x.Variables);
                definition.Ignore(x => x.Inputs);
                definition.Ignore(x => x.Outputs);
                definition.Ignore(x => x.Outcomes);
                definition.Ignore(x => x.CustomProperties);
                definition.Property<string>(DataColumn);
                // The options are COLUMNS, not text: the definition filter tests Options.UsableAsStep in SQL.
                definition.ComplexProperty(x => x.Options, options =>
                {
                    options.Property(o => o.ActivationStrategyType).HasConversion<TypeConverter>();
                    options.Property(o => o.IncidentStrategyType).HasConversion<TypeConverter>();
                });
                definition.Property(x => x.DefinitionId).HasMaxLength(IndexedStringLength);
                definition.Property(x => x.Name).HasMaxLength(IndexedStringLength);
                definition.Property(x => x.MaterializerName).HasMaxLength(IndexedStringLength);
                definition.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                definition.HasIndex(x => x.DefinitionId);
                definition.HasIndex(x => new { x.DefinitionId, x.Version });
                definition.HasIndex(x => x.Name);
                definition.HasIndex(x => x.IsLatest);
                definition.HasIndex(x => x.IsPublished);
                definition.HasIndex(x => x.TenantId);
                definition.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<PipelineInstance>(instance =>
            {
                instance.ToTable("EnginePipelineInstances");
                instance.HasKey(x => x.Id);
                instance.Ignore(x => x.PipelineState);
                instance.Property<string>(DataColumn);
                instance.Property(x => x.DefinitionId).HasMaxLength(IndexedStringLength);
                instance.Property(x => x.DefinitionVersionId).HasMaxLength(IndexedStringLength);
                instance.Property(x => x.ParentPipelineInstanceId).HasMaxLength(IndexedStringLength);
                instance.Property(x => x.CorrelationId).HasMaxLength(IndexedStringLength);
                instance.Property(x => x.Name).HasMaxLength(IndexedStringLength);
                instance.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                instance.HasIndex(x => x.DefinitionId);
                instance.HasIndex(x => x.DefinitionVersionId);
                instance.HasIndex(x => x.ParentPipelineInstanceId);
                instance.HasIndex(x => x.CorrelationId);
                instance.HasIndex(x => x.Name);
                instance.HasIndex(x => x.Status);
                instance.HasIndex(x => x.SubStatus);
                instance.HasIndex(x => x.IsExecuting);
                instance.HasIndex(x => x.CreatedAt);
                instance.HasIndex(x => x.UpdatedAt);
                instance.HasIndex(x => x.FinishedAt);
                instance.HasIndex(x => x.TenantId);
                instance.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<StoredBookmark>(bookmark =>
            {
                bookmark.ToTable("EngineBookmarks");
                bookmark.HasKey(x => x.Id);
                bookmark.Ignore(x => x.Payload);
                bookmark.Ignore(x => x.Metadata);
#pragma warning disable CS0618 // StepTypeName is an obsolete alias of Name; without Ignore EF would map a second column for it.
                bookmark.Ignore(x => x.StepTypeName);
#pragma warning restore CS0618
                bookmark.Property<string>(DataColumn);
                bookmark.Property<string>(MetadataColumn);
                bookmark.Property(x => x.Name).HasMaxLength(IndexedStringLength);
                bookmark.Property(x => x.Hash).HasMaxLength(IndexedStringLength);
                bookmark.Property(x => x.PipelineInstanceId).HasMaxLength(IndexedStringLength);
                bookmark.Property(x => x.StepInstanceId).HasMaxLength(IndexedStringLength);
                bookmark.Property(x => x.CorrelationId).HasMaxLength(IndexedStringLength);
                bookmark.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                bookmark.HasIndex(x => x.Name);
                bookmark.HasIndex(x => x.Hash);
                bookmark.HasIndex(x => x.PipelineInstanceId);
                bookmark.HasIndex(x => x.StepInstanceId);
                bookmark.HasIndex(x => x.CorrelationId);
                bookmark.HasIndex(x => x.TenantId);
                bookmark.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<StoredTrigger>(trigger =>
            {
                trigger.ToTable("EngineTriggers");
                trigger.HasKey(x => x.Id);
                trigger.Ignore(x => x.Payload);
                trigger.Property<string>(DataColumn);
                trigger.Property(x => x.Name).HasMaxLength(IndexedStringLength);
                trigger.Property(x => x.Hash).HasMaxLength(IndexedStringLength);
                trigger.Property(x => x.PipelineDefinitionId).HasMaxLength(IndexedStringLength);
                trigger.Property(x => x.PipelineDefinitionVersionId).HasMaxLength(IndexedStringLength);
                trigger.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                trigger.HasIndex(x => x.Name);
                trigger.HasIndex(x => x.Hash);
                trigger.HasIndex(x => x.PipelineDefinitionId);
                trigger.HasIndex(x => x.PipelineDefinitionVersionId);
                trigger.HasIndex(x => x.TenantId);
                trigger.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<BookmarkQueueItem>(item =>
            {
                item.ToTable("EngineBookmarkQueue");
                item.HasKey(x => x.Id);
                item.Ignore(x => x.Options);
                item.Property<string>(DataColumn);
                item.Property(x => x.BookmarkId).HasMaxLength(IndexedStringLength);
                item.Property(x => x.StimulusHash).HasMaxLength(IndexedStringLength);
                item.Property(x => x.PipelineInstanceId).HasMaxLength(IndexedStringLength);
                item.Property(x => x.StepInstanceId).HasMaxLength(IndexedStringLength);
                item.Property(x => x.StepTypeName).HasMaxLength(IndexedStringLength);
                item.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                item.HasIndex(x => x.BookmarkId);
                item.HasIndex(x => x.StimulusHash);
                item.HasIndex(x => x.PipelineInstanceId);
                item.HasIndex(x => x.StepInstanceId);
                item.HasIndex(x => x.StepTypeName);
                item.HasIndex(x => x.CreatedAt);
                item.HasIndex(x => x.TenantId);
                item.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<StepExecutionRecord>(record =>
            {
                record.ToTable("EngineStepExecutions");
                record.HasKey(x => x.Id);
                record.Ignore(x => x.StepState);
                record.Ignore(x => x.Payload);
                record.Ignore(x => x.Outputs);
                record.Ignore(x => x.Properties);
                record.Ignore(x => x.Metadata);
                record.Ignore(x => x.Exception);
                record.Ignore(x => x.SerializedSnapshot);
                record.Property<string>(StepStateColumn);
                record.Property<string>(PayloadColumn);
                record.Property<string>(OutputsColumn);
                record.Property<string>(PropertiesColumn);
                record.Property<string>(MetadataColumn);
                record.Property<string>(ExceptionColumn);
                record.Property(x => x.PipelineInstanceId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepNodeId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepName).HasMaxLength(IndexedStringLength);
                record.Property(x => x.SchedulingStepExecutionId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.SchedulingStepId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.SchedulingPipelineInstanceId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                record.HasIndex(x => x.PipelineInstanceId);
                record.HasIndex(x => x.StepId);
                record.HasIndex(x => x.StepNodeId);
                record.HasIndex(x => x.StepName);
                record.HasIndex(x => x.Status);
                record.HasIndex(x => x.StartedAt);
                record.HasIndex(x => x.CompletedAt);
                record.HasIndex(x => x.SchedulingStepExecutionId);
                record.HasIndex(x => x.SchedulingPipelineInstanceId);
                record.HasIndex(x => x.TenantId);
                record.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<PipelineExecutionLogRecord>(record =>
            {
                record.ToTable("EngineExecutionLog");
                record.HasKey(x => x.Id);
#pragma warning disable CS0618 // StepState is obsolete on the record; it is still persisted, as text.
                record.Ignore(x => x.StepState);
#pragma warning restore CS0618
                record.Ignore(x => x.Payload);
                record.Property<string>(StepStateColumn);
                record.Property<string>(PayloadColumn);
                record.Property(x => x.PipelineInstanceId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepInstanceId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.ParentStepInstanceId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepNodeId).HasMaxLength(IndexedStringLength);
                record.Property(x => x.StepType).HasMaxLength(IndexedStringLength);
                record.Property(x => x.EventName).HasMaxLength(IndexedStringLength);
                record.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                record.HasIndex(x => x.PipelineInstanceId);
                record.HasIndex(x => x.StepInstanceId);
                record.HasIndex(x => x.ParentStepInstanceId);
                record.HasIndex(x => x.StepId);
                record.HasIndex(x => x.StepNodeId);
                record.HasIndex(x => x.StepType);
                record.HasIndex(x => x.EventName);
                record.HasIndex(x => x.Timestamp);
                record.HasIndex(x => x.Sequence);
                record.HasIndex(x => x.TenantId);
                record.HasQueryFilter(x => x.TenantId == TenantId);
            });

            modelBuilder.Entity<SerializedKeyValuePair>(pair =>
            {
                pair.ToTable("EngineKeyValues");
                pair.HasKey(x => x.Id);
                pair.Ignore(x => x.Key); // an alias of Id
                pair.Property(x => x.Id).HasMaxLength(IndexedStringLength);
                pair.Property(x => x.TenantId).HasMaxLength(IndexedStringLength);
                pair.HasIndex(x => x.TenantId);
                pair.HasQueryFilter(x => x.TenantId == TenantId);
            });
        }

        private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        private sealed class VersionConverter() : ValueConverter<Version, string>(
            version => version.ToString(),
            text => Version.Parse(text));

        private sealed class TypeConverter() : ValueConverter<Type, string>(
            type => type.AssemblyQualifiedName!,
            name => Type.GetType(name, true)!);
    }
}
