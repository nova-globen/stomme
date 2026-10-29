#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.EventBus;
using Stomme.Models;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Modules.Collaboration.Realtime;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Shared;
using Stomme.Tests.Notifications;
using Stomme.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// Presence and the change feed without a host: each domain event becomes ONE notice of its kind,
    /// naming the resource and who caused it and nothing else; a board stored by the Dashboards module is a Saved notice;
    /// with the switch off or outside a site's scope nothing is told; the notice rides the bus when one is composed and is
    /// delivered in-process otherwise, to the site key's signal and the site's hub group; the push is a no-op while the
    /// SignalR transport is off. the predecessor platform's <c>EditSessionPresencePublicationTests</c> and its change-feed push, by what they
    /// proved: an editor of the resource is told, on every path a change can take.
    /// </summary>
    public sealed class CollaborationRealtimeTests
    {
        private const string Board = "dashboard/3f2504e0-4f89-41d3-9a0c-0305e82c3301";

        [Fact]
        public async Task A_lock_a_presence_and_an_announced_change_are_each_one_notice_of_their_kind()
        {
            var (signal, push, _) = Rig(enabled: true, userId: 12);

            await signal.HandleAsync(new LockAcquiredEvent(Guid.NewGuid(), 9, Board, LockScope.Resource, 12, DateTime.UtcNow.AddMinutes(2)));
            await signal.HandleAsync(new PresenceChangedEvent(Guid.NewGuid(), 9, Board, 12, PresenceChangeKind.Joined));
            await signal.HandleAsync(new ChangeBroadcastEvent(Guid.NewGuid(), 9, Board, 12, "layout changed", DateTime.UtcNow));
            await signal.HandleAsync(new LockReleasedEvent(Guid.NewGuid(), 9, Board, LockScope.Resource, 12));

            Assert.Equal(
                new[] { CollaborationNoticeKinds.Lock, CollaborationNoticeKinds.Presence, CollaborationNoticeKinds.Change, CollaborationNoticeKinds.Lock },
                push.Pushes.Select(pushed => pushed.Notice.Kind));
            Assert.All(push.Pushes, pushed => Assert.Equal((3, 9, "dashboard", "3f2504e0-4f89-41d3-9a0c-0305e82c3301", 12), (pushed.TenantId, pushed.SiteId, pushed.Notice.ResourceType, pushed.Notice.ResourceId, pushed.Notice.ByUserId)));
        }

        [Fact]
        public void A_notice_carries_the_resource_the_kind_and_who_and_nothing_else()
        {
            Assert.Equal(new[] { "ByUserId", "Kind", "ResourceId", "ResourceType" }, typeof(CollaborationNotice).GetProperties().Select(property => property.Name).Where(name => name != "EqualityContract").OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public async Task An_administrators_take_over_names_the_administrator_and_the_sweep_names_the_holder()
        {
            var (signal, push, user) = Rig(enabled: true, userId: 40);
            await signal.HandleAsync(new LockReleasedEvent(Guid.NewGuid(), 9, Board, LockScope.Resource, 12));
            Assert.Equal(40, push.Pushes.Single().Notice.ByUserId);

            user.UserId = null; // the sweep job: no request, no user
            await signal.HandleAsync(new LockReleasedEvent(Guid.NewGuid(), 9, Board, LockScope.Resource, 12));
            Assert.Equal(12, push.Pushes.Last().Notice.ByUserId);
        }

        [Fact]
        public async Task A_board_stored_by_the_Dashboards_module_is_a_Saved_notice()
        {
            var (signal, push, _) = Rig(enabled: true, userId: 5);
            var boardId = Guid.NewGuid();

            await signal.HandleAsync(new DashboardUpdatedEvent(boardId));
            await signal.HandleAsync(new WidgetAddedEvent(boardId, Guid.NewGuid()));
            await signal.HandleAsync(new DashboardPublishedEvent(boardId));

            Assert.Equal(3, push.Pushes.Count);
            Assert.All(push.Pushes, pushed => Assert.Equal(new CollaborationNotice("dashboard", boardId.ToString("D"), CollaborationNoticeKinds.Saved, 5), pushed.Notice));
        }

        [Fact]
        public async Task With_collaboration_switched_off_nothing_is_told()
        {
            var (signal, push, _) = Rig(enabled: false, userId: 5);

            await signal.HandleAsync(new DashboardUpdatedEvent(Guid.NewGuid()));
            await signal.HandleAsync(new LockAcquiredEvent(Guid.NewGuid(), 9, Board, LockScope.Resource, 12, DateTime.UtcNow));

            Assert.Empty(push.Pushes);
        }

        [Fact]
        public async Task Outside_a_sites_scope_nothing_is_told()
        {
            var (signal, push, _) = Rig(enabled: true, userId: 5, withAlias: false);

            await signal.HandleAsync(new DashboardUpdatedEvent(Guid.NewGuid()));

            Assert.Empty(push.Pushes);
        }

        [Fact]
        public async Task With_a_bus_composed_the_notice_rides_the_bus_and_its_handler_tells_the_signal_and_the_push()
        {
            var bus = new RecordingBus();
            var (signal, push, _) = Rig(enabled: true, userId: 5, bus: bus);
            var boardId = Guid.NewGuid();

            await signal.HandleAsync(new DashboardUpdatedEvent(boardId));

            Assert.Empty(push.Pushes); // published, not delivered
            var notice = Assert.IsType<CollaborationChangedNotice>(Assert.Single(bus.Published));
            Assert.Equal(new CollaborationChangedNotice(3, 9, "dashboard", boardId.ToString("D"), CollaborationNoticeKinds.Saved, 5), notice);

            var inProcess = new CollaborationSignal();
            var raised = new List<(string SiteKey, CollaborationNotice Notice)>();
            inProcess.Changed += (siteKey, message) => raised.Add((siteKey, message));
            var services = new ServiceCollection().AddSingleton<ICollaborationRealtimePush>(push).AddSingleton<ICollaborationSignal>(inProcess).BuildServiceProvider();
            await new CollaborationChangedNoticeHandler(services, NullLogger<CollaborationChangedNoticeHandler>.Instance).HandleAsync(notice);

            Assert.Equal((new Alias { TenantId = 3, SiteId = 9 }.SiteKey, new CollaborationNotice("dashboard", boardId.ToString("D"), CollaborationNoticeKinds.Saved, 5)), Assert.Single(raised));
            Assert.Equal((3, 9), (push.Pushes.Single().TenantId, push.Pushes.Single().SiteId));
        }

        [Fact]
        public async Task A_notice_that_cannot_be_delivered_is_logged_never_the_callers_failure()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new CollaborationOptions { Enabled = true });
            services.AddSingleton<ICollaborationRealtimePush>(new ThrowingPush());
            var notifier = new CollaborationNotifier(services.BuildServiceProvider(), NullLogger<CollaborationNotifier>.Instance, SiteWith(true));

            await notifier.NotifyAsync("pipeline", "p1", CollaborationNoticeKinds.Saved, 1);
        }

        [Fact]
        public void The_push_is_a_no_op_while_the_SignalR_transport_is_off_and_the_hub_group_is_the_site_key()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = "Data Source=:memory:",
                })
                .Build());
            services.AddSingleton(new TransportOptions());
            new Stomme.Modules.Collaboration.Startup.ServerStartup().ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            Assert.IsType<NoCollaborationRealtimePush>(provider.GetRequiredService<ICollaborationRealtimePush>());
            Assert.IsType<CollaborationSignal>(provider.GetRequiredService<ICollaborationSignal>());
            Assert.Equal("collaboration:" + new Alias { TenantId = 7, SiteId = 11 }.SiteKey, CollaborationHub.GroupFor(7, 11));
        }

        private static (CollaborationDomainSignal Signal, RecordingPush Push, SettableCurrentUser User) Rig(bool enabled, int? userId, bool withAlias = true, IEventBus? bus = null)
        {
            var push = new RecordingPush();
            var services = new ServiceCollection();
            services.AddSingleton(new CollaborationOptions { Enabled = enabled });
            services.AddSingleton<ICollaborationRealtimePush>(push);
            if (bus is not null)
            {
                services.AddSingleton(bus);
            }

            var user = new SettableCurrentUser { UserId = userId };
            var notifier = new CollaborationNotifier(services.BuildServiceProvider(), NullLogger<CollaborationNotifier>.Instance, SiteWith(withAlias));
            return (new CollaborationDomainSignal(notifier, user), push, user);
        }

        private static SiteState SiteWith(bool alias) => new() { Alias = alias ? new Alias { AliasId = 1, TenantId = 3, SiteId = 9, Name = "localhost" } : null };

        private sealed class RecordingPush : ICollaborationRealtimePush
        {
            private readonly ConcurrentQueue<(int TenantId, int SiteId, CollaborationNotice Notice)> _queue = new();

            public IReadOnlyList<(int TenantId, int SiteId, CollaborationNotice Notice)> Pushes => _queue.ToList();

            public Task PushAsync(int tenantId, int siteId, CollaborationNotice notice, CancellationToken cancellationToken = default)
            {
                _queue.Enqueue((tenantId, siteId, notice));
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingPush : ICollaborationRealtimePush
        {
            public Task PushAsync(int tenantId, int siteId, CollaborationNotice notice, CancellationToken cancellationToken = default) => throw new InvalidOperationException("the hub is gone");
        }

        private sealed class RecordingBus : IEventBus
        {
            public List<object> Published { get; } = new();

            public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : class
            {
                Published.Add(@event);
                return Task.CompletedTask;
            }

            public Task PublishAsync<TEvent>(TEvent @event, IDictionary<string, string>? headers = null, CancellationToken ct = default) where TEvent : class => PublishAsync(@event, ct);

            public Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task SendAsync<TCommand>(TCommand command, string destinationQueue, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task DeferAsync<TMessage>(TimeSpan delay, TMessage message, CancellationToken ct = default) where TMessage : class => throw new NotSupportedException();
        }
    }
}
