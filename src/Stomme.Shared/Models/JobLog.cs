using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    /// <summary>
    /// Log / Journal of <see cref="Job"/>s executed.
    /// </summary>
    public class JobLog
    {
        /// <summary>
        /// Internal ID
        /// </summary>
        public int JobLogId { get; set; }

        /// <summary>
        /// Reference to the <see cref="Job"/> which was run
        /// </summary>
        public int JobId { get; set; }

        /// <summary>
        /// Timestamp when the <see cref="Job"/> started. 
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// Timestamp when the <see cref="Job"/> ended. 
        /// </summary>
        public DateTime? FinishDate { get; set; }

        /// <summary>
        /// Success information. 
        /// </summary>
        public bool? Succeeded { get; set; }

        /// <summary>
        /// Any log information that was generated while the <see cref="Job"/> executed
        /// </summary>
        public string Notes { get; set; }

        /// <summary>
        /// Machine name of the server that executed this job
        /// </summary>
        public string Server { get; set; }

        /// <summary>
        /// In a scale-out environment this contains the environment WEBSITE_INSTANCE_ID, otherwise it is blank
        /// </summary>
        public string Instance { get; set; }

        /// <summary>
        /// Reference to the Job.
        /// </summary>
        /// <remarks>
        /// <c>[NotMapped]</c>: this was a MAPPED EF navigation the repository eager-loaded
        /// with <c>.Include(item =&gt; item.Job)</c>. Now that JobLog is on the persistence seam
        /// (<c>JobLogAggregate</c> references the job by id only), this is view state <c>JobLogRepository</c>
        /// populates from a second query, mirroring <c>SiteGroupMember.SiteGroup</c>. It is
        /// still serialized to the client (the Admin &gt; Jobs &gt; Log screen reads <c>Job.Name</c> and
        /// <c>Job.IsExecuting</c>); <c>[NotMapped]</c> affects EF only, never JSON.
        /// </remarks>
        [NotMapped]
        public Job Job { get; set; }
    }
}
