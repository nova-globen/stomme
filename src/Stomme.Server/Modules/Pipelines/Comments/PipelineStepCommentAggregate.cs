#nullable enable
using System;
using Stomme.Models.Pipelines;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Pipelines.Comments
{
    /// <summary>
    /// A comment on a step of a site's pipeline: what someone said
    /// about the step, who said it and when, and whether it was dealt with. Not a change to the pipeline - in no version and no
    /// pipeline file - so it is written straight away and a reviewer without the manage key can write one.
    /// </summary>
    /// <remarks>
    /// The text is the author's own words: under the right to erasure it is DELETED with its author's other comments
    /// (<see cref="PipelineStepCommentErasureParticipant"/>). A comment outlives its step - the step can be removed and put back,
    /// or the removal undone - and the editor lists one whose step is gone as such.
    /// </remarks>
    public sealed class PipelineStepCommentAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumUserNameLength = 256;

        public const int MaximumIdLength = 128;

        private DateTime _createdOnUtc;

        private DateTime? _resolvedOnUtc;

        // Materialisation constructor. Not for application use.
        private PipelineStepCommentAggregate()
        {
            TenantId = null!;
            DefinitionId = string.Empty;
            StepId = string.Empty;
            Text = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string DefinitionId { get; private set; }

        public string StepId { get; private set; }

        public int UserId { get; private set; }

        /// <summary>The author's user name as it was then - read without a join, and a renamed user's comments stay theirs.</summary>
        public string? UserName { get; private set; }

        public string Text { get; private set; }

        public DateTime CreatedOnUtc
        {
            get => _createdOnUtc.Kind == DateTimeKind.Utc ? _createdOnUtc : DateTime.SpecifyKind(_createdOnUtc, DateTimeKind.Utc);
            private set => _createdOnUtc = value;
        }

        public int? ResolvedByUserId { get; private set; }

        public string? ResolvedByUserName { get; private set; }

        public DateTime? ResolvedOnUtc
        {
            get => _resolvedOnUtc is { } at && at.Kind != DateTimeKind.Utc ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : _resolvedOnUtc;
            private set => _resolvedOnUtc = value;
        }

        public bool Resolved => ResolvedOnUtc is not null;

        public static PipelineStepCommentAggregate Write(
            Guid id, TenantId tenantId, int siteId, string definitionId, string stepId, int userId, string? userName, string text, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("A comment needs an id.", nameof(id));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A comment belongs to a site.");
            }

            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "A comment is written by someone signed in.");
            }

            var pipeline = (definitionId ?? string.Empty).Trim();
            var step = (stepId ?? string.Empty).Trim();
            if (pipeline.Length == 0 || pipeline.Length > MaximumIdLength || step.Length == 0 || step.Length > PipelineStepCommentLimits.MaxStepId)
            {
                throw new ArgumentException("A comment is on a step of a pipeline.", nameof(stepId));
            }

            var said = (text ?? string.Empty).Trim();
            if (said.Length == 0 || said.Length > PipelineStepCommentLimits.MaxText)
            {
                throw new ArgumentException($"Say something in 1 to {PipelineStepCommentLimits.MaxText} characters.", nameof(text));
            }

            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {nowUtc.Kind}).", nameof(nowUtc));
            }

            return new PipelineStepCommentAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                DefinitionId = pipeline,
                StepId = step,
                UserId = userId,
                UserName = userName is { Length: > MaximumUserNameLength } ? userName[..MaximumUserNameLength] : userName,
                Text = said,
                CreatedOnUtc = nowUtc,
            };
        }

        /// <summary>Who resolved it forgotten (their erasure): still resolved, and when, by nobody named.</summary>
        public void ForgetResolver()
        {
            ResolvedByUserId = null;
            ResolvedByUserName = null;
        }

        /// <summary>Dealt with, by whom and when - or open again. Setting what it already is changes nothing.</summary>
        public bool SetResolved(bool resolved, int userId, string? userName, DateTime nowUtc)
        {
            if (resolved == Resolved)
            {
                return false;
            }

            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {nowUtc.Kind}).", nameof(nowUtc));
            }

            ResolvedOnUtc = resolved ? nowUtc : null;
            ResolvedByUserId = resolved ? userId : null;
            ResolvedByUserName = resolved ? (userName is { Length: > MaximumUserNameLength } ? userName[..MaximumUserNameLength] : userName) : null;
            return true;
        }
    }
}
