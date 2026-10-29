using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The harness for the CQRS suites, in place of an in-memory setup (<c>AddInMemoryPersistence</c> + <c>AddMediator(assembly)</c> +
    /// <c>ValidationBehavior</c> + <c>new NotificationsModule().ConfigureServices</c>): the module's own <c>ServerStartup</c> on the
    /// in-process FILE store (<see cref="NotificationPipelineHarness"/>), the mediator with the SAME behavior order the host
    /// composes (authorization outermost, validation innermost), fake e-mail + webhook transports (no DNS, no network), a
    /// dictionary <see cref="ISiteSecretStore"/> and a settable <see cref="ICurrentUser"/>.
    /// </summary>
    /// <remarks>
    /// The guard is ALLOW-ALL here: these suites pin what the handlers DO. What the keys DECIDE is pinned where it can mean
    /// something - reflectively and over real HTTP on an installed host (<c>NotificationPermissionDeclarationTests</c>). The
    /// mediator is registered HERE, not by the module (<c>AddMediator</c> is additive and Guestbook's startup already
    /// calls it in the host).
    /// </remarks>
    internal static class NotificationCqrsHarness
    {
        public static async Task<(NotificationPipelineHarness Harness, FakeSiteSecretStore Secrets, SettableCurrentUser User)> StartAsync(
            IEnumerable<INotificationChannel>? transports = null, int userId = 7)
        {
            var secrets = new FakeSiteSecretStore();
            var user = new SettableCurrentUser { UserId = userId, SiteId = NotificationPipelineHarness.SiteId };
            var harness = await NotificationPipelineHarness.StartAsync(
                transports ?? new INotificationChannel[]
                {
                    new FakeTransportChannel(ChannelKind.Email, NotificationProviderKeys.Email),
                    new FakeTransportChannel(ChannelKind.Webhook, NotificationProviderKeys.Webhook),
                    new FakeTransportChannel(ChannelKind.InApp, NotificationProviderKeys.InApp),
                },
                configure: services =>
                {
                    services.AddScoped<IMediator, Stomme.Mediator.Mediator>();
                    services.AddMediatorBehavior(typeof(AuthorizationBehavior<,>));
                    services.AddMediatorBehavior(typeof(ValidationBehavior<,>));
                    services.AddScoped<IAccessGuard, AllowAllAccessGuard>();
                    services.RemoveAll<ICurrentUser>();
                    services.AddSingleton<ICurrentUser>(user);
                    services.RemoveAll<ISiteSecretStore>();
                    services.AddSingleton<ISiteSecretStore>(secrets);
                });
            return (harness, secrets, user);
        }

        public static Task<T> SendAsync<T>(this NotificationPipelineHarness harness, IRequest<T> request) =>
            harness.InScopeAsync(sp => sp.GetRequiredService<IMediator>().Send(request));
    }

    internal sealed class SettableCurrentUser : ICurrentUser
    {
        public int? UserId { get; set; }

        public string? UserName => UserId is null ? null : "user" + UserId;

        public bool IsAuthenticated => UserId is not null;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int SiteId { get; set; }
    }

    internal sealed class AllowAllAccessGuard : IAccessGuard
    {
        public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
    }
}