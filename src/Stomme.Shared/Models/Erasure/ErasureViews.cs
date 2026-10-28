using System;
using System.Collections.Generic;

namespace Stomme.Models.Erasure
{
    /// <summary>
    /// A subject's erasure receipt (D112): what an operator shows a supervisory authority. It holds NO personal data -
    /// the subject is an opaque erasure id, the outcomes name stores and counts, a retention names its reason and due
    /// date. The identity behind it is gone once the run completes.
    /// </summary>
    public class ErasureReceiptView
    {
        public Guid Id { get; set; }

        /// <summary>The opaque erasure id the subject is known by from now on; random, unlinkable.</summary>
        public string SubjectErasureId { get; set; }

        public int SiteId { get; set; }

        /// <summary>Pending, Retaining, Done or Failed.</summary>
        public string Status { get; set; }

        public DateTime RequestedOnUtc { get; set; }

        public DateTime? ErasedOnUtc { get; set; }

        public DateTime? CompletedOnUtc { get; set; }

        /// <summary>The latest date any retained rows fall due, while some are retained.</summary>
        public DateTime? RetainedUntilUtc { get; set; }

        public int Attempts { get; set; }

        /// <summary>The failing participant and the exception type of the last failed attempt; never a message with data in it.</summary>
        public string LastError { get; set; }

        public List<ErasureOutcomeView> Outcomes { get; set; } = new List<ErasureOutcomeView>();
    }

    public class ErasureOutcomeView
    {
        public string Participant { get; set; }

        public string Store { get; set; }

        /// <summary>Delete, Anonymise or Retain.</summary>
        public string Handling { get; set; }

        public int Affected { get; set; }

        public string Reason { get; set; }

        public DateTime? RetainedUntilUtc { get; set; }

        /// <summary>For a retention: when the same job deleted the retained rows.</summary>
        public DateTime? PurgedOnUtc { get; set; }
    }
}
