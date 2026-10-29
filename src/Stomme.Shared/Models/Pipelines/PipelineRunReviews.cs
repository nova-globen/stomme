#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// n8n's execution annotations: what a person said about a run - that it went well or badly, some tags, a note - and who
    /// said it, when. Kept beside the run, shown on its page and in the runs list, found by the runs search, deleted with the run.
    /// </summary>
    public class PipelineRunReview
    {
        /// <summary><see cref="PipelineRunReviews.Good"/>, <see cref="PipelineRunReviews.Bad"/>, or none.</summary>
        public string? Rating { get; set; }

        public List<string> Tags { get; set; } = new();

        public string? Note { get; set; }

        public string? By { get; set; }

        public DateTime? AtUtc { get; set; }
    }

    /// <summary>The rules for a run's review - the one place both sides read.</summary>
    public static class PipelineRunReviews
    {
        public const string Good = "good";

        public const string Bad = "bad";

        public const int MaxTags = 10;

        public const int MaxTagLength = 40;

        public const int MaxNoteLength = 2000;

        /// <summary>The tags as kept: trimmed, none empty, each once whatever its case, in the order given.</summary>
        public static List<string> Tags(IEnumerable<string?>? tags) =>
            (tags ?? Array.Empty<string?>()).Select(tag => (tag ?? "").Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Whether the review may be kept; the reason when not.</summary>
        public static bool Check(PipelineRunReview review, out string? error)
        {
            error = null;
            if (review.Rating is not null && review.Rating != Good && review.Rating != Bad)
            {
                error = $"'{review.Rating}' is not a rating: good, bad, or none.";
            }
            else if (review.Tags.Count > MaxTags)
            {
                error = $"A run takes at most {MaxTags} tags.";
            }
            else if (review.Tags.FirstOrDefault(tag => tag.Length > MaxTagLength || tag.Any(char.IsControl)) is { } bad)
            {
                error = $"'{bad}' cannot be a tag: at most {MaxTagLength} characters, no control characters.";
            }
            else if ((review.Note ?? "").Length > MaxNoteLength)
            {
                error = $"A note is at most {MaxNoteLength} characters.";
            }

            return error is null;
        }

        /// <summary>Whether a review says nothing - nothing to keep.</summary>
        public static bool IsEmpty(PipelineRunReview? review) =>
            review is null || (review.Rating is null && review.Tags.Count == 0 && string.IsNullOrWhiteSpace(review.Note));

        /// <summary>Whether the runs search finds a run by its review: a tag or the rating named, or words of the note.</summary>
        public static bool Matches(PipelineRunReview? review, string? search)
        {
            var term = (search ?? "").Trim();
            if (review is null || term.Length == 0)
            {
                return false;
            }

            return string.Equals(review.Rating, term, StringComparison.OrdinalIgnoreCase)
                || review.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (review.Note ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }
}
