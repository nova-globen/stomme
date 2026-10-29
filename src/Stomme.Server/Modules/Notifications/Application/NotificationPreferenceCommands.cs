#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Notifications.Application
{
    // Self-service: each user reads and writes their OWN
    // preferences on the current site - the user is ICurrentUser.UserId (int), never a request field, so there is no
    // parameter a caller could point at someone else. Gated by ViewOwn.

    /// <summary>The caller's preferences on the current site; permissive defaults when none are saved.</summary>
    [Requires(NotificationsPermissions.ViewOwn)]
    public sealed record GetMyNotificationPreferencesQuery : IQuery<NotificationPreferencesView>;

    public sealed class GetMyNotificationPreferencesQueryHandler(
        INotificationPreferenceRepository preferences, ICurrentUser currentUser, SiteState siteState)
        : IQueryHandler<GetMyNotificationPreferencesQuery, NotificationPreferencesView>
    {
        public async Task<NotificationPreferencesView> Handle(GetMyNotificationPreferencesQuery request, CancellationToken ct)
        {
            var userId = PreferenceUser.Require(currentUser, "read");
            var preference = await preferences.GetByUserAsync(NotificationWire.RequireSiteId(siteState), userId, ct);
            if (preference is null)
            {
                return new NotificationPreferencesView(
                    Array.Empty<NotificationChannelKind>(), NotificationSeverityPreference.All, Array.Empty<string>(), null,
                    NotificationDigestMode.Immediate, null);
            }

            return new NotificationPreferencesView(
                NotificationWire.ToWire(preference.AcceptedChannels),
                NotificationWire.ToWire(preference.SeverityFilter),
                new List<string>(preference.MutedTopics),
                preference.QuietHours is { } quiet ? new NotificationQuietHoursInput(quiet.Start, quiet.End, quiet.UtcOffset) : null,
                NotificationWire.ToWire(preference.Digest),
                preference.PreferredLocale);
        }
    }

    /// <summary>Creates or replaces the caller's preferences on the current site.</summary>
    [Requires(NotificationsPermissions.ViewOwn)]
    public sealed record SaveMyNotificationPreferencesCommand(
        IReadOnlyList<NotificationChannelKind> AcceptedChannels,
        NotificationSeverityPreference SeverityFilter,
        IReadOnlyList<string> MutedTopics,
        NotificationQuietHoursInput? QuietHours,
        NotificationDigestMode Digest,
        string? PreferredLocale) : ICommand<Unit>;

    public sealed class SaveMyNotificationPreferencesCommandHandler(
        INotificationPreferenceRepository preferences,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        SiteState siteState)
        : ICommandHandler<SaveMyNotificationPreferencesCommand, Unit>
    {
        public async Task<Unit> Handle(SaveMyNotificationPreferencesCommand request, CancellationToken ct)
        {
            var userId = PreferenceUser.Require(currentUser, "save");
            var siteId = NotificationWire.RequireSiteId(siteState);

            var accepted = NotificationWire.ToDomain(request.AcceptedChannels);
            var severity = NotificationWire.ToDomain(request.SeverityFilter);
            var digest = NotificationWire.ToDomain(request.Digest);
            var muted = request.MutedTopics ?? Array.Empty<string>();
            var quietHours = request.QuietHours is { } quiet ? new QuietHours(quiet.Start, quiet.End, quiet.UtcOffset) : null;

            var preference = await preferences.GetByUserAsync(siteId, userId, ct);
            if (preference is null)
            {
                var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved in this scope.");
                preference = NotificationPreferenceAggregate.Create(Guid.NewGuid(), tenantId, siteId, userId);
                preference.Configure(accepted, severity, muted, quietHours, digest, request.PreferredLocale);
                await preferences.AddAsync(preference, ct);
            }
            else
            {
                preference.Configure(accepted, severity, muted, quietHours, digest, request.PreferredLocale);
                await preferences.UpdateAsync(preference, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    internal static class PreferenceUser
    {
        public static int Require(ICurrentUser currentUser, string verb) =>
            currentUser.UserId is { } userId && userId > 0
                ? userId
                : throw new InvalidOperationException($"An authenticated user is required to {verb} notification preferences.");
    }
}