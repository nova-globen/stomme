#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Erasure.Participants
{
    /// <summary>
    /// The framework's own subject-bearing stores: each declares its handling once, in code. Everything acts on
    /// ids and is idempotent - a second run finds nothing.
    /// </summary>
    public sealed class UserSettingsErasureParticipant(ISettingRepository settings) : ISubjectErasureParticipant
    {
        public string Name => "User settings";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("SettingAggregate (User)") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var count = (await settings.GetSettingsAsync(EntityNames.User, subject.UserId)).Count();
            if (count > 0)
            {
                await settings.DeleteSettingsAsync(EntityNames.User, subject.UserId);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, count) };
        }
    }

    /// <summary>The framework's site notifications sent to or by the subject.</summary>
    public sealed class SiteNotificationsErasureParticipant(INotificationRepository notifications) : ISubjectErasureParticipant
    {
        public string Name => "Site notifications";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("NotificationAggregate (framework)") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = (await notifications.GetNotificationsAsync(subject.SiteId, subject.UserId, -1))
                .Concat(await notifications.GetNotificationsAsync(subject.SiteId, -1, subject.UserId))
                .GroupBy(n => n.NotificationId).Select(g => g.First()).ToList();
            foreach (var notification in mine)
            {
                await notifications.DeleteNotificationAsync(notification.NotificationId);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count) };
        }
    }

    /// <summary>Per-visit analytics rows the subject was signed in for: deleted (address, agent and url are theirs).</summary>
    public sealed class VisitorsErasureParticipant(IVisitorStore visitors, IFrameworkStoreSessionFactory sessions) : ISubjectErasureParticipant
    {
        public string Name => "Visitors";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("VisitorAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var rows = FrameworkStoreReads.Query(sessions, session => session.Query<VisitorAggregate>().Where(v => v.SiteId == subject.SiteId && v.UserId == subject.UserId).ToList());
            foreach (var row in rows)
            {
                await visitors.DeleteAsync(row, ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, rows.Count) };
        }
    }

    /// <summary>
    /// The operational event log: one row can name several people and hold request internals, so the rows stay and the
    /// subject's identifier is removed from them (anonymised: the user id is cleared and cannot be recovered); the log
    /// ages out by LogRetention regardless.
    /// </summary>
    public sealed class LogsErasureParticipant(ILogStore logs, IFrameworkStoreSessionFactory sessions) : ISubjectErasureParticipant
    {
        public string Name => "Event log";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Anonymise("LogAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var rows = FrameworkStoreReads.Query(sessions, session => session.Query<LogAggregate>().Where(l => l.SiteId == subject.SiteId && l.UserId == subject.UserId).ToList());
            foreach (var row in rows)
            {
                row.UserId = null;
                await logs.UpdateAsync(row, ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Anonymise, rows.Count) };
        }
    }

    /// <summary>The subject's personal pages on the site (a page with a UserId is theirs alone).</summary>
    public sealed class PersonalPagesErasureParticipant(IPageRepository pages) : ISubjectErasureParticipant
    {
        public string Name => "Personal pages";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("PageAggregate (personal)") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = (await pages.GetPagesAsync(subject.SiteId)).Where(p => p.UserId == subject.UserId).ToList();
            foreach (var page in mine)
            {
                await pages.DeletePageAsync(page.PageId);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count) };
        }
    }

    /// <summary>The subject's roles on the site and their per-user permission rows: the site's access configuration about the person, gone with them.</summary>
    public sealed class SiteAccessErasureParticipant(IUserRoleRepository userRoles, IPermissionStore permissions, IFrameworkStoreSessionFactory sessions) : ISubjectErasureParticipant
    {
        public string Name => "Site access";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("UserRoleAggregate"), ErasureDeclaration.Delete("PermissionAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var roles = (await userRoles.GetUserRolesAsync(subject.UserId, subject.SiteId)).ToList();
            foreach (var role in roles)
            {
                await userRoles.DeleteUserRoleAsync(role.UserRoleId);
            }

            var rows = FrameworkStoreReads.Query(sessions, session => session.Query<PermissionAggregate>().Where(p => p.SiteId == subject.SiteId && p.UserId == subject.UserId).ToList());
            foreach (var row in rows)
            {
                await permissions.DeleteAsync(row, ct);
            }

            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, roles.Count),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Delete, rows.Count),
            };
        }
    }

    /// <summary>
    /// The per-tenant authority's tokens and authorizations issued to the subject (by the identity provider's subject id,
    /// captured before the identity user went). The managers exist only while <c>OpenIddict:Enabled</c>; without them
    /// there is nothing to erase and the outcome says zero.
    /// </summary>
    public sealed class OpenIddictErasureParticipant(IServiceProvider services) : ISubjectErasureParticipant
    {
        public string Name => "Authority tokens";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("OpenIddict tokens"), ErasureDeclaration.Delete("OpenIddict authorizations") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var tokens = 0;
            var authorizations = 0;
            if (!string.IsNullOrEmpty(subject.IdentitySubject))
            {
                if (services.GetService<OpenIddict.Abstractions.IOpenIddictTokenManager>() is { } tokenManager)
                {
                    await foreach (var token in tokenManager.FindBySubjectAsync(subject.IdentitySubject, ct))
                    {
                        await tokenManager.DeleteAsync(token, ct);
                        tokens++;
                    }
                }

                if (services.GetService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>() is { } authorizationManager)
                {
                    await foreach (var authorization in authorizationManager.FindBySubjectAsync(subject.IdentitySubject, ct))
                    {
                        await authorizationManager.DeleteAsync(authorization, ct);
                        authorizations++;
                    }
                }
            }

            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, tokens),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Delete, authorizations),
            };
        }
    }

    /// <summary>A read through the framework seam's query session (reads never go through a store).</summary>
    internal static class FrameworkStoreReads
    {
        public static T Query<T>(IFrameworkStoreSessionFactory sessions, Func<IQuerySession, T> read)
        {
            var session = sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                (session as IDisposable)?.Dispose();
            }
        }
    }
}
