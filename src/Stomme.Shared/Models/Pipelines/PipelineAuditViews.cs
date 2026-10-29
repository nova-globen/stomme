#nullable enable
using System;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// One line of a site's pipeline activity (n8n Enterprise's audit log): who did what to which pipeline, when - a
    /// publish, an archive, a run started or stopped, a folder moved, the site's variables changed. Written when the change is
    /// made, never edited.
    /// </summary>
    public sealed class PipelineAuditEntryView
    {
        public DateTime OccurredOnUtc { get; set; }

        /// <summary>Who did it - their user name as it was then.</summary>
        public string? UserName { get; set; }

        /// <summary>What was done, said as a person would: "published", "archived", "started a run".</summary>
        public string Action { get; set; } = "";

        /// <summary>The pipeline it was done to; empty for what concerns the site (its variables, its data tables).</summary>
        public string DefinitionId { get; set; } = "";

        /// <summary>The pipeline's name when it was done.</summary>
        public string? PipelineName { get; set; }

        public string? RunId { get; set; }

        /// <summary>What else says it: the note a publish gave, the folder moved to, the variables named - never a value.</summary>
        public string? Detail { get; set; }
    }

    public static class PipelineAuditLimits
    {
        /// <summary>The most lines one read returns, newest first.</summary>
        public const int MaxTake = 500;

        public const int DefaultTake = 100;
    }
}
