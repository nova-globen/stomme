#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Erasure;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Comments
{
    // ============================================================================================================
    // BL-229 - comments on a pipeline's steps (better than n8n, whose workflows have no discussion). Who may: anyone who can SEE
    // the pipeline reads and writes them and resolves or reopens any - a reviewer comments without being able to change the
    // pipeline; a comment is deleted by its author, or by anyone with the manage key. Each add, resolve, reopen and delete is in
    // the pipeline activity ledger (by construction, PipelineAudit), naming the step - never the text.
    // ============================================================================================================

    /// <summary>A site's comments on pipeline steps, on the storage-blind module seam.</summary>
    public interface IPipelineStepCommentRepository
    {
        Task AddAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default);

        Task UpdateAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default);

        Task DeleteAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default);

        Task<PipelineStepCommentAggregate?> GetAsync(Guid id, CancellationToken ct = default);

        Task<IReadOnlyList<PipelineStepCommentAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<PipelineStepCommentAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default);

        /// <summary>A pipeline's comments deleted with it; how many there were.</summary>
        Task<int> DeleteByPipelineAsync(int siteId, string definitionId, CancellationToken ct = default);
    }

    // Filters on integers only - the provider-neutral part of FindAsync (D78); a pipeline's comments are picked out of the
    // site's in memory, so no string comparison is left to a provider to decide.
    public sealed class PipelineStepCommentRepository(IRepository<PipelineStepCommentAggregate, Guid> inner) : IPipelineStepCommentRepository
    {
        public Task AddAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => inner.AddAsync(comment, ct);

        public Task UpdateAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => inner.UpdateAsync(comment, ct);

        public Task DeleteAsync(PipelineStepCommentAggregate comment, CancellationToken ct = default) => inner.DeleteAsync(comment, ct);

        public Task<PipelineStepCommentAggregate?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<PipelineStepCommentAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            inner.FindAsync(comment => comment.SiteId == siteId, ct);

        public Task<IReadOnlyList<PipelineStepCommentAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            inner.FindAsync(comment => comment.SiteId == siteId && comment.UserId == userId, ct);

        public async Task<int> DeleteByPipelineAsync(int siteId, string definitionId, CancellationToken ct = default)
        {
            var deleted = 0;
            foreach (var comment in (await ListBySiteAsync(siteId, ct)).Where(comment => string.Equals(comment.DefinitionId, definitionId, StringComparison.Ordinal)))
            {
                await inner.DeleteAsync(comment, ct);
                deleted++;
            }

            return deleted;
        }
    }

    /// <summary>A pipeline's comments, oldest first - for anyone who can see the pipeline.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListStepCommentsQuery(string DefinitionId) : IQuery<IReadOnlyList<PipelineStepCommentView>>;

    /// <summary>How many comments each of the site's pipelines has open - for the pipelines list, by definition id; none listed when 0.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListOpenCommentCountsQuery : IQuery<IReadOnlyDictionary<string, int>>;

    /// <summary>The people a comment may mention: the site's people who can see its pipelines.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListMentionablePeopleQuery : IQuery<IReadOnlyList<PipelineMentionView>>;

    /// <summary>A comment added to a step of a pipeline - answered as it is now shown; null when the site has no such pipeline.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record AddStepCommentCommand(string DefinitionId, string StepId, string Text) : ICommand<PipelineStepCommentView?>;

    /// <summary>A comment dealt with, or open again; null when the pipeline has no such comment.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record SetStepCommentResolvedCommand(string DefinitionId, Guid Id, bool Resolved) : ICommand<PipelineStepCommentView?>;

    /// <summary>A comment deleted - its author's, or anyone's with the manage key - answered as it was; null when the pipeline has no such comment.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record DeleteStepCommentCommand(string DefinitionId, Guid Id) : ICommand<PipelineStepCommentView?>;

    public sealed class PipelineStepCommentHandlers(
        IPipelineStepCommentRepository comments, IUnitOfWork unitOfWork, ICurrentUser user, ITenantContext tenantContext, SiteState siteState,
        IAccessGuard accessGuard, IPipelineDefinitionStore definitions, PipelineMentionNotifier mentions, TimeProvider clock)
        : IQueryHandler<ListStepCommentsQuery, IReadOnlyList<PipelineStepCommentView>>,
          IQueryHandler<ListMentionablePeopleQuery, IReadOnlyList<PipelineMentionView>>,
          IQueryHandler<ListOpenCommentCountsQuery, IReadOnlyDictionary<string, int>>,
          ICommandHandler<AddStepCommentCommand, PipelineStepCommentView?>,
          ICommandHandler<SetStepCommentResolvedCommand, PipelineStepCommentView?>,
          ICommandHandler<DeleteStepCommentCommand, PipelineStepCommentView?>
    {
        public async Task<IReadOnlyList<PipelineStepCommentView>> Handle(ListStepCommentsQuery request, CancellationToken ct)
        {
            var siteId = Site();
            var pipeline = (request.DefinitionId ?? string.Empty).Trim();
            if (siteId <= 0 || pipeline.Length == 0)
            {
                return Array.Empty<PipelineStepCommentView>();
            }

            var manager = await accessGuard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct);
            return (await comments.ListBySiteAsync(siteId, ct))
                .Where(comment => string.Equals(comment.DefinitionId, pipeline, StringComparison.Ordinal))
                .OrderBy(comment => comment.CreatedOnUtc).ThenBy(comment => comment.Id)
                .Select(comment => View(comment, manager))
                .ToList();
        }

        public async Task<IReadOnlyDictionary<string, int>> Handle(ListOpenCommentCountsQuery request, CancellationToken ct) =>
            Site() is var siteId && siteId > 0
                ? (await comments.ListBySiteAsync(siteId, ct)).Where(comment => !comment.Resolved)
                    .GroupBy(comment => comment.DefinitionId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);

        public async Task<IReadOnlyList<PipelineMentionView>> Handle(ListMentionablePeopleQuery request, CancellationToken ct) =>
            Site() is var siteId && siteId > 0
                ? (await mentions.PeopleAsync(siteId, ct)).Select(person => person.View).ToList()
                : Array.Empty<PipelineMentionView>();

        public async Task<PipelineStepCommentView?> Handle(AddStepCommentCommand request, CancellationToken ct)
        {
            var (siteId, userId) = Writer();
            if (tenantContext.CurrentTenantId is not { } tenantId)
            {
                throw new InvalidOperationException("No tenant is resolved, so the comment has nowhere to be kept.");
            }

            // The site's pipeline (the engine store answers for the request's site only) - never an id made up or another site's.
            var pipeline = (request.DefinitionId ?? string.Empty).Trim();
            if (pipeline.Length == 0 || await LatestAsync(pipeline, ct) is not { } definition)
            {
                return null;
            }

            var comment = PipelineStepCommentAggregate.Write(
                Guid.NewGuid(), tenantId, siteId, pipeline, request.StepId, userId, user.UserName, request.Text, clock.GetUtcNow().UtcDateTime);
            await comments.AddAsync(comment, ct);
            await unitOfWork.SaveChangesAsync(ct);
            var written = await WrittenAsync(comment, definition, ct);
            // The comment is kept first: telling the people it mentions never decides whether it is written.
            (written.Told, written.NotTold) = await mentions.TellAsync(siteState.Alias!, comment, definition.Name, written.StepName, ct);
            return written;
        }

        public async Task<PipelineStepCommentView?> Handle(SetStepCommentResolvedCommand request, CancellationToken ct)
        {
            var (siteId, userId) = Writer();
            if (await Find(request.DefinitionId, request.Id, siteId, ct) is not { } comment)
            {
                return null;
            }

            if (comment.SetResolved(request.Resolved, userId, user.UserName, clock.GetUtcNow().UtcDateTime))
            {
                await comments.UpdateAsync(comment, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }

            return await WrittenAsync(comment, null, ct);
        }

        public async Task<PipelineStepCommentView?> Handle(DeleteStepCommentCommand request, CancellationToken ct)
        {
            var (siteId, userId) = Writer();
            if (await Find(request.DefinitionId, request.Id, siteId, ct) is not { } comment)
            {
                return null;
            }

            if (comment.UserId != userId && !await accessGuard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct))
            {
                throw new AccessDeniedException("Only its author, or someone who can change pipelines, deletes someone else's comment.");
            }

            await comments.DeleteAsync(comment, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return await WrittenAsync(comment, null, ct);
        }

        private Task<PipelineDefinition?> LatestAsync(string definitionId, CancellationToken ct) =>
            definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Latest }, ct);

        // A write's answer: the comment as shown, with its step's name as the pipeline has it now - the activity ledger is read by
        // people, who know a step by its name. A step not saved yet, or a pipeline that does not read, leaves the id to say it.
        private async Task<PipelineStepCommentView> WrittenAsync(PipelineStepCommentAggregate comment, PipelineDefinition? definition, CancellationToken ct)
        {
            var view = View(comment, await accessGuard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct));
            try
            {
                definition ??= await LatestAsync(comment.DefinitionId, ct);
                var name = definition is null ? null : PipelineGraphCodec.ToGraph(definition.StringData).Steps.FirstOrDefault(step => step.Id == comment.StepId)?.Name;
                view.StepName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                view.StepName = null; // the comment is written; only its step's name is not said
            }

            return view;
        }

        private int Site() => siteState.Alias?.SiteId ?? 0;

        // Who writes: someone signed in on a resolved site.
        private (int SiteId, int UserId) Writer()
        {
            var siteId = Site();
            if (siteId <= 0)
            {
                throw new InvalidOperationException("No site is resolved.");
            }

            if (user.UserId is not { } userId || userId <= 0)
            {
                throw new AccessDeniedException("A comment is written by someone signed in.");
            }

            return (siteId, userId);
        }

        // The comment by its id, when it is this site's and this pipeline's - an id from another pipeline is no comment here.
        private async Task<PipelineStepCommentAggregate?> Find(string definitionId, Guid id, int siteId, CancellationToken ct) =>
            await comments.GetAsync(id, ct) is { } comment && comment.SiteId == siteId
                && string.Equals(comment.DefinitionId, (definitionId ?? string.Empty).Trim(), StringComparison.Ordinal)
                ? comment
                : null;

        private PipelineStepCommentView View(PipelineStepCommentAggregate comment, bool manager) => new()
        {
            Id = comment.Id,
            StepId = comment.StepId,
            UserName = comment.UserName,
            Mine = user.UserId == comment.UserId,
            CanDelete = manager || user.UserId == comment.UserId,
            Text = comment.Text,
            CreatedOnUtc = comment.CreatedOnUtc,
            Resolved = comment.Resolved,
            ResolvedBy = comment.ResolvedByUserName,
            ResolvedOnUtc = comment.ResolvedOnUtc,
        };
    }

    /// <summary>
    /// Comments under the right to erasure (D112): a comment is its author's own words, so every comment the subject wrote on
    /// the site is DELETED. Idempotent: a second run finds nothing. A comment someone else wrote and the subject only resolved
    /// keeps its text and loses the resolver's name.
    /// </summary>
    public sealed class PipelineStepCommentErasureParticipant(
        IPipelineStepCommentRepository comments, IUnitOfWork unitOfWork, ILogger<PipelineStepCommentErasureParticipant> logger) : ISubjectErasureParticipant
    {
        public string Name => "Pipeline step comments";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("PipelineStepCommentAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (subject.UserId <= 0 || subject.SiteId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var deleted = 0;
            foreach (var comment in await comments.ListByUserAsync(subject.SiteId, subject.UserId, ct))
            {
                await comments.DeleteAsync(comment, ct);
                deleted++;
            }

            // Resolved by the subject, written by someone else: the comment stays resolved, the resolver's name goes. Never one of
            // the subject's own - those are deleted above, and a store that defers its writes to the save may still list them.
            foreach (var comment in (await comments.ListBySiteAsync(subject.SiteId, ct))
                .Where(comment => comment.ResolvedByUserId == subject.UserId && comment.UserId != subject.UserId))
            {
                comment.ForgetResolver();
                await comments.UpdateAsync(comment, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Count} pipeline step comment(s) of user {UserId} on site {SiteId}.", deleted, subject.UserId, subject.SiteId);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, deleted) };
        }
    }

    /// <summary>
    /// A person's comments on a site's pipeline steps, for their data package (D107): their own words, so carried, not left
    /// out - each with the pipeline and step it is on, when, and whether it was resolved (never who resolved it: that is
    /// someone else's name). The erasure participant deletes the same rows.
    /// </summary>
    public sealed class PipelineStepCommentSubjectContributor(IPipelineStepCommentRepository comments) : Stomme.Portability.ISubjectDataContributor
    {
        public const string Type = "pipeline-step-comment";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<Stomme.Portability.PortableEntity>> ExportForSubjectAsync(Stomme.Portability.SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await comments.ListByUserAsync(scope.SiteId, scope.UserId, cancellationToken))
                .OrderBy(comment => comment.CreatedOnUtc).ThenBy(comment => comment.Id)
                .Select(comment => new Stomme.Portability.PortableEntity(
                    new Stomme.Portability.PortableEntityRef(Type, comment.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)),
                    comment.CreatedOnUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                    1,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        pipeline = comment.DefinitionId,
                        step = comment.StepId,
                        text = comment.Text,
                        createdOnUtc = comment.CreatedOnUtc,
                        resolvedOnUtc = comment.ResolvedOnUtc,
                    })))
                .ToList();
    }

    /// <summary>The comments' composition, from the module's startup (only where Pipelines is composed).</summary>
    public static class PipelineStepCommentRegistration
    {
        public static void Register(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<PipelineStepCommentAggregate, Guid>();
            services.AddScoped<IPipelineStepCommentRepository, PipelineStepCommentRepository>();
            services.AddScoped<PipelineStepCommentHandlers>();
            services.TryAddScoped<Stomme.Infrastructure.Security.IStoredRowPermissionEvaluator, Stomme.Infrastructure.Security.StoredRowPermissionEvaluator>();
            services.AddScoped<PipelineMentionNotifier>();
            services.AddScoped<IRequestHandler<ListMentionablePeopleQuery, IReadOnlyList<PipelineMentionView>>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<IRequestHandler<ListOpenCommentCountsQuery, IReadOnlyDictionary<string, int>>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<IRequestHandler<ListStepCommentsQuery, IReadOnlyList<PipelineStepCommentView>>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<IRequestHandler<AddStepCommentCommand, PipelineStepCommentView?>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<IRequestHandler<SetStepCommentResolvedCommand, PipelineStepCommentView?>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<IRequestHandler<DeleteStepCommentCommand, PipelineStepCommentView?>>(provider => provider.GetRequiredService<PipelineStepCommentHandlers>());
            services.AddScoped<ISubjectErasureParticipant, PipelineStepCommentErasureParticipant>(); // D112: the author's own words, deleted
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, PipelineStepCommentSubjectContributor>(); // D107: and carried
        }
    }
}
