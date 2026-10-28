using System;
using System.Collections.Generic;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - a comment on a step of a pipeline: what someone said about it, and whether it was dealt with (resolved). Kept
    /// apart from the pipeline - not a change to it, in no version and no file - so a reviewer who cannot change the pipeline
    /// can still say something about it.
    /// </summary>
    public sealed class PipelineStepCommentView
    {
        public Guid Id { get; set; }

        public string StepId { get; set; }

        /// <summary>
        /// The step's name as the pipeline has it now, on the answer to a write - so the activity ledger names the step a person
        /// knows, not its id; null on a list, and for a step not saved yet or with no name.
        /// </summary>
        public string StepName { get; set; }

        /// <summary>Who wrote it, as their user name was then.</summary>
        public string UserName { get; set; }

        /// <summary>The person reading wrote it.</summary>
        public bool Mine { get; set; }

        /// <summary>The person reading may delete it: their own, or anyone's with the pipelines' manage key.</summary>
        public bool CanDelete { get; set; }

        public string Text { get; set; }

        public DateTime CreatedOnUtc { get; set; }

        public bool Resolved { get; set; }

        public string ResolvedBy { get; set; }

        public DateTime? ResolvedOnUtc { get; set; }

        /// <summary>On the answer to an add: the people the comment's @mentions told (display names), in the order named.</summary>
        public List<string> Told { get; set; } = new();

        /// <summary>On the answer to an add: the names after an @ that are nobody here who can see pipelines - none was told.</summary>
        public List<string> NotTold { get; set; } = new();
    }

    /// <summary>Someone a comment can mention: a person on the site who can see its pipelines.</summary>
    public sealed class PipelineMentionView
    {
        public string UserName { get; set; }

        public string DisplayName { get; set; }
    }

    /// <summary>A comment to add to a step.</summary>
    public sealed class PipelineStepCommentRequest
    {
        public string StepId { get; set; }

        public string Text { get; set; }
    }

    public static class PipelineStepCommentLimits
    {
        public const int MaxText = 2000;

        public const int MaxStepId = 128;

        /// <summary>How many people one comment may mention; a name past it is not told.</summary>
        public const int MaxMentions = 10;
    }
}
