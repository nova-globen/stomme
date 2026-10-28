#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration.Persistence;
using Stomme.Erasure;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Collaboration.Application;
using Stomme.Persistence.Abstractions;
using Stomme.Portability;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Collaboration.Infrastructure
{
    /// <summary>
    /// The lock sweep (BL-97 increment 11, the predecessor platform's <c>CollaborationLockSweepJob</c>): every minute, per SITE of the job's
    /// tenant, the lapsed leases are expired so their release reaches the other editors. Switched off with the feature
    /// (<c>Collaboration:Enabled</c>, read from this host's configuration each run - BL-162); one site's failure is logged
    /// and contained. <b>CHANGED:</b> the framework's job scheduler and a pass per site, the advisor job's shape, instead
    /// of the predecessor platform's cron over a tenant directory.
    /// </summary>
    public class CollaborationSweepJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.Collaboration.Infrastructure.CollaborationSweepJob, Stomme.Server"
        public CollaborationSweepJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Collaboration Lock Sweep Job";
            Frequency = "m"; // minute - leases are minutes long
            Interval = 1;
            IsEnabled = true;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!(provider.GetService<CollaborationOptions>()?.Enabled ?? false))
            {
                return "Collaboration Lock Sweep: Switched Off (Collaboration:Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Collaboration Lock Sweep: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<CollaborationSweepJob>>();
            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            List<Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync()).Where(site => !site.IsDeleted).ToList();
            }

            var log = new StringBuilder();
            foreach (var site in sites)
            {
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant!.TenantId, site.SiteId);
                    var expired = await siteScope.ServiceProvider.GetRequiredService<CollaborationSweeper>().SweepSiteAsync(site.SiteId);
                    if (expired > 0)
                    {
                        log.Append("Collaboration Lock Sweep For Site: ").Append(site.Name).Append(" - Expired ").Append(expired).Append("<br />");
                    }

                    // Increment 12: the participants a crashed browser left behind, forgotten once silent past their presence window.
                    var absent = siteScope.ServiceProvider.GetService<PresenceSweeper>() is { } presence ? await presence.SweepSiteAsync(site.SiteId) : 0;
                    if (absent > 0)
                    {
                        log.Append("Collaboration Presence Sweep For Site: ").Append(site.Name).Append(" - Forgot ").Append(absent).Append("<br />");
                    }
                }
                catch (Exception ex)
                {
                    // Contained per site: one site's failure must not leave the others' leases standing.
                    logger.LogWarning(ex, "The collaboration lock sweep failed for site {SiteId} of tenant {TenantId}; continuing with the remaining sites.", site.SiteId, tenant!.TenantId);
                    log.Append("Collaboration Lock Sweep Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Collaboration Lock Sweep: Nothing Lapsed<br />" : log.ToString();
        }
    }

    /// <summary>
    /// Collaboration's right-to-erasure participant (D114 over D112): a user's locks are DELETED - live ones included, their
    /// holder is gone - and the user is removed from every session of the site, their presence and their change entries
    /// with it. Idempotent: a second run finds nothing.
    /// </summary>
    public sealed class CollaborationErasure(IResourceLockRepository locks, IEditSessionRepository sessions, IUnitOfWork unitOfWork, ILogger<CollaborationErasure> logger) : ISubjectErasureParticipant
    {
        public string Name => "Collaboration locks and editing sessions";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete("ResourceLockAggregate"),
            ErasureDeclaration.Delete("EditSessionAggregate"),
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (subject.UserId <= 0 || subject.SiteId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var deleted = 0;
            foreach (var resourceLock in await locks.ListForUserAsync(subject.SiteId, subject.UserId, ct))
            {
                await locks.DeleteAsync(resourceLock, ct);
                deleted++;
            }

            var forgotten = 0;
            foreach (var session in await sessions.ListForSiteAsync(subject.SiteId, ct))
            {
                if (session.Forget(subject.UserId))
                {
                    await sessions.UpdateAsync(session, ct);
                    forgotten++;
                }
            }

            if (deleted + forgotten > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Erased user {UserId} from collaboration on site {SiteId}: {Locks} lock(s) deleted, {Sessions} session(s) cleared.", subject.UserId, subject.SiteId, deleted, forgotten);
            }

            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, deleted),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Delete, forgotten),
            };
        }
    }

    /// <summary>
    /// A person's collaboration record for their data package (D107): the locks they took and the sessions they were present
    /// in on the site, with the changes they announced there. Coordination state, carried because it names them.
    /// </summary>
    public sealed class CollaborationSubjectContributor(IResourceLockRepository locks, IEditSessionRepository sessions) : ISubjectDataContributor
    {
        public const string Type = "collaboration";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            var entities = new List<PortableEntity>();
            foreach (var resourceLock in (await locks.ListForUserAsync(scope.SiteId, scope.UserId, cancellationToken)).OrderBy(candidate => candidate.AcquiredUtc))
            {
                entities.Add(new PortableEntity(
                    new PortableEntityRef(Type, "lock-" + resourceLock.Id.ToString("N", CultureInfo.InvariantCulture)),
                    resourceLock.AcquiredUtc.ToString("u", CultureInfo.InvariantCulture),
                    1,
                    JsonSerializer.Serialize(new { kind = "lock", resource = resourceLock.Resource.Value, scope = resourceLock.Scope.ToString(), acquiredOnUtc = resourceLock.AcquiredUtc, expiresOnUtc = resourceLock.ExpiresUtc, released = resourceLock.Released })));
            }

            foreach (var session in (await sessions.ListForSiteAsync(scope.SiteId, cancellationToken)).OrderBy(candidate => candidate.StartedUtc))
            {
                var presence = session.Participants.FirstOrDefault(participant => participant.UserId == scope.UserId);
                var changes = session.Changes.Where(change => change.AuthorUserId == scope.UserId).ToList();
                if (presence is null && changes.Count == 0)
                {
                    continue;
                }

                entities.Add(new PortableEntity(
                    new PortableEntityRef(Type, "session-" + session.Id.ToString("N", CultureInfo.InvariantCulture)),
                    session.StartedUtc.ToString("u", CultureInfo.InvariantCulture),
                    1,
                    JsonSerializer.Serialize(new
                    {
                        kind = "session",
                        resource = session.Resource.Value,
                        status = session.Status.ToString(),
                        startedOnUtc = session.StartedUtc,
                        presence = presence is null ? null : new { displayName = presence.DisplayName, joinedOnUtc = presence.JoinedUtc, activeRegion = presence.ActiveRegion },
                        changes = changes.Select(change => new { atUtc = change.AtUtc, descriptor = change.Descriptor }),
                    })));
            }

            return entities;
        }
    }
}
