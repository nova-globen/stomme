#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>The engine's entities to the wire models the client sees, and nothing else - no body, no state, no payloads.</summary>
    internal static class PipelineWire
    {
        public static PipelineView ToView(PipelineDefinitionSummary summary) => new()
        {
            Id = summary.Id,
            DefinitionId = summary.DefinitionId,
            Name = summary.Name,
            Description = summary.Description,
            Version = summary.Version ?? 0,
            IsLatest = summary.IsLatest,
            IsPublished = summary.IsPublished,
            CreatedOnUtc = summary.CreatedAt.UtcDateTime,
        };

        public static PipelineView ToView(PipelineDefinition definition) => Fill(new PipelineView(), definition);

        public static PipelineDetail ToDetail(PipelineDefinition definition)
        {
            var detail = Fill(new PipelineDetail(), definition);
            detail.Body = definition.StringData;
            return detail;
        }

        private static T Fill<T>(T view, PipelineDefinition definition) where T : PipelineView
        {
            view.Id = definition.Id;
            view.DefinitionId = definition.DefinitionId;
            view.Name = definition.Name;
            view.Description = definition.Description;
            view.Version = definition.Version;
            view.IsLatest = definition.IsLatest;
            view.IsPublished = definition.IsPublished;
            view.CreatedOnUtc = definition.CreatedAt.UtcDateTime;
            return view;
        }

        /// <summary>
        /// BL-229 increment 7: the definition's custom property naming who last saved the version - the engine keeps no author of
        /// its own, so the module's saves stamp it (a version saved before has none).
        /// </summary>
        public const string SavedByProperty = "stomme.savedBy";

        public static void StampSavedBy(PipelineDefinition definition, string? userName, int? userId)
        {
            var who = !string.IsNullOrWhiteSpace(userName) ? userName : userId is { } id ? "user " + id : null;
            if (who is not null)
            {
                // A NEW dictionary: a draft made over a published version is the engine's clone of it and shares its custom
                // properties, so writing into them stamped the published version too (found by its test: version 1 read
                // "saved by grace" after grace saved version 2).
                definition.CustomProperties = new Dictionary<string, object>(definition.CustomProperties) { [SavedByProperty] = who };
            }
        }

        /// <summary>
        /// BL-229: the definition's custom property holding what the author said changed when publishing the version - stamped by
        /// the publish (and taken away by a publish that says nothing), so it is the publish's own.
        /// </summary>
        public const string NoteProperty = "stomme.publishNote";

        /// <summary>The note, tidied and bounded, on a new dictionary (the draft shares its properties with the version it was made from).</summary>
        public static void StampNote(PipelineDefinition definition, string? note)
        {
            var tidy = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (tidy is { Length: > PublishPipelineRequest.MaxNoteLength })
            {
                throw new ArgumentException($"A note is at most {PublishPipelineRequest.MaxNoteLength} characters.");
            }

            var properties = new Dictionary<string, object>(definition.CustomProperties);
            properties.Remove(NoteProperty);
            if (tidy is not null)
            {
                properties[NoteProperty] = tidy;
            }

            definition.CustomProperties = properties;
        }

        public static PipelineVersionView ToVersion(PipelineDefinition definition) => new()
        {
            Id = definition.Id,
            Version = definition.Version,
            IsPublished = definition.IsPublished,
            IsLatest = definition.IsLatest,
            CreatedOnUtc = definition.CreatedAt.UtcDateTime,
            SavedBy = definition.CustomProperties.TryGetValue(SavedByProperty, out var who) ? who?.ToString() : null,
            // A draft is a copy of the version before it, its properties too: the note it carries is that version's, not its own.
            Note = (definition.IsPublished || !definition.IsLatest) && definition.CustomProperties.TryGetValue(NoteProperty, out var note) && note?.ToString() is { Length: > 0 } said ? said : null,
        };

        public static PipelineRunView ToView(PipelineInstanceSummary instance) => new()
        {
            Id = instance.Id,
            DefinitionId = instance.DefinitionId,
            DefinitionVersionId = instance.DefinitionVersionId,
            Version = instance.Version,
            Name = instance.Name,
            CorrelationId = instance.CorrelationId,
            Status = instance.Status.ToString(),
            SubStatus = instance.SubStatus.ToString(),
            IncidentCount = instance.IncidentCount,
            CreatedOnUtc = instance.CreatedAt.UtcDateTime,
            UpdatedOnUtc = (instance.UpdatedAt ?? instance.CreatedAt).UtcDateTime,
            FinishedOnUtc = instance.FinishedAt?.UtcDateTime,
        };

        public static PipelineRunView ToView(PipelineInstance instance) => new()
        {
            Id = instance.Id,
            DefinitionId = instance.DefinitionId,
            DefinitionVersionId = instance.DefinitionVersionId,
            Version = instance.Version,
            Name = instance.Name,
            CorrelationId = instance.CorrelationId,
            Status = instance.Status.ToString(),
            SubStatus = instance.SubStatus.ToString(),
            IncidentCount = instance.IncidentCount,
            CreatedOnUtc = instance.CreatedAt.UtcDateTime,
            UpdatedOnUtc = instance.UpdatedAt.UtcDateTime,
            FinishedOnUtc = instance.FinishedAt?.UtcDateTime,
        };

        public static PipelineRunLogLine ToLine(PipelineExecutionLogRecord record) => new()
        {
            Sequence = record.Sequence,
            TimestampUtc = record.Timestamp.UtcDateTime,
            StepName = record.StepName,
            StepType = record.StepType,
            EventName = record.EventName,
            Message = record.Message,
        };
    }
}
