#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Pipelines.Audit
{
    /// <summary>
    /// The site's pipeline activity, newest first - all of it, or one pipeline's; at most <see cref="PipelineAuditLimits.MaxTake"/>
    /// lines. An author's to read (who changed what is not every viewer's business).
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ListPipelineAuditQuery(string? DefinitionId, int Take = PipelineAuditLimits.DefaultTake) : IQuery<IReadOnlyList<PipelineAuditEntryView>>;

    public sealed class ListPipelineAuditQueryHandler(IPipelineAuditRepository audit, SiteState siteState) : IQueryHandler<ListPipelineAuditQuery, IReadOnlyList<PipelineAuditEntryView>>
    {
        public async Task<IReadOnlyList<PipelineAuditEntryView>> Handle(ListPipelineAuditQuery request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? 0;
            if (siteId <= 0)
            {
                return Array.Empty<PipelineAuditEntryView>();
            }

            var take = Math.Clamp(request.Take, 1, PipelineAuditLimits.MaxTake);
            var pipeline = string.IsNullOrWhiteSpace(request.DefinitionId) ? null : request.DefinitionId.Trim();
            return (await audit.ListBySiteAsync(siteId, ct))
                .Where(entry => pipeline is null || string.Equals(entry.DefinitionId, pipeline, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.OccurredOnUtc).ThenByDescending(entry => entry.Id)
                .Take(take)
                .Select(entry => new PipelineAuditEntryView
                {
                    OccurredOnUtc = entry.OccurredOnUtc,
                    UserName = entry.UserName,
                    Action = entry.Action,
                    DefinitionId = entry.DefinitionId,
                    PipelineName = entry.PipelineName,
                    RunId = entry.RunId,
                    Detail = entry.Detail,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Ages out the pipeline activity ledger per site, on the framework's contributed-pass seam (as the MCP ledger). It is a
    /// security record - who published, ran, archived, deleted - so, as the MCP ledger's, a setting below
    /// <see cref="MinimumRetentionDays"/> is read as the minimum: the record cannot be emptied through a text box.
    /// </summary>
    public sealed class PipelineAuditRetentionPurger(IPipelineAuditRepository audit) : IRetentionPurger
    {
        public const string RetentionSettingKey = "PipelineAuditRetention";

        public const int DefaultRetentionDays = 365;

        public const int MinimumRetentionDays = 30;

        public string SettingKey => RetentionSettingKey;

        public int DefaultDays => DefaultRetentionDays;

        public string DisplayName => "Pipeline activity";

        public static int ClampRetention(int days) => days < MinimumRetentionDays ? MinimumRetentionDays : days;

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-ClampRetention(days));
            var purged = 0;
            foreach (var entry in await audit.ListOlderThanAsync(siteId, cutoff, cancellationToken))
            {
                await audit.DeleteAsync(entry, cancellationToken);
                purged++;
            }

            return purged;
        }
    }

    /// <summary>
    /// The pipeline activity ledger under the right to erasure (D112): a SECURITY record of what was changed and run on the
    /// site and by whom, so a subject's lines are retained one year under Article 17(3) and then deleted by the same job - as
    /// the MCP ledger's. They hold the user id and user name, an action and what it concerned - never a value.
    /// </summary>
    public sealed class PipelineAuditErasureParticipant(IPipelineAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock) : ISubjectErasureParticipant
    {
        public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365);

        public string Name => "Pipeline activity";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Retain("PipelineAuditEntryAggregate", RetentionReason.Security, RetentionPeriod) };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var count = (await audit.ListByUserAsync(subject.SiteId, subject.UserId, ct)).Count;
            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Retain, count, RetentionReason.Security, clock.GetUtcNow().UtcDateTime.Add(RetentionPeriod)) };
        }

        public async Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await audit.ListByUserAsync(subject.SiteId, subject.UserId, ct);
            foreach (var entry in mine)
            {
                await audit.DeleteAsync(entry, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return mine.Count;
        }
    }

    /// <summary>The ledger's composition, from the module's startup (only where Pipelines is composed).</summary>
    public static class PipelineAuditRegistration
    {
        public static void Register(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<PipelineAuditEntryAggregate, Guid>();
            services.AddScoped<IPipelineAuditRepository, PipelineAuditRepository>();
            services.AddScoped<PipelineAuditRecorder>();
            AddBehaviors(services);
            services.AddScoped<IRequestHandler<ListPipelineAuditQuery, IReadOnlyList<PipelineAuditEntryView>>, ListPipelineAuditQueryHandler>();
            services.AddScoped<IRetentionPurger, PipelineAuditRetentionPurger>();
            services.AddScoped<ISubjectErasureParticipant, PipelineAuditErasureParticipant>(); // D112: a security record, retained one year then deleted
        }

        /// <summary>The recording behaviour, closed per command: it wraps the module's own commands and nobody else's requests.</summary>
        public static void AddBehaviors(IServiceCollection services)
        {
            foreach (var command in PipelineAudit.Actions.Keys)
            {
                var response = ResponseOf(command);
                services.AddScoped(typeof(IPipelineBehavior<,>).MakeGenericType(command, response), typeof(PipelineAuditBehavior<,>).MakeGenericType(command, response));
            }
        }

        /// <summary>What a command answers - the TResponse of its <see cref="ICommand{TResponse}"/>.</summary>
        public static Type ResponseOf(Type command) =>
            command.GetInterfaces().Single(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>)).GetGenericArguments()[0];
    }
}
