using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Notifications;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The published producer port - <see cref="INotificationDispatcher"/> and its DTO + mirror
    /// enums - lives in <c>Stomme.Shared</c>, dependency-free. Pinned here: placement, the enum WIRE values (a producer compiled
    /// against one release must mean the same channel against the next), and the optional members' defaults. The
    /// behavioural contract (a Shared-only dependent reaches a channel; Endpoint recipients rejected) is
    /// NotificationDispatcherContractTests, which lands with the module.
    /// </summary>
    public sealed class NotificationDispatcherPortShapeTests
    {
        [Fact]
        public void The_port_and_its_dto_live_in_the_shared_assembly()
        {
            foreach (var type in new[]
            {
                typeof(INotificationDispatcher), typeof(NotificationDispatchRequest), typeof(DispatchRecipient),
                typeof(DispatchParameter), typeof(NotificationChannelKind), typeof(NotificationRecipientKind),
                typeof(NotificationSeverity),
            })
            {
                Assert.Equal("Stomme.Shared", type.Assembly.GetName().Name);
            }
        }

        [Fact]
        public void The_mirror_enums_keep_Predecessors_wire_values()
        {
            Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<NotificationChannelKind>().Select(value => (int)value));
            Assert.Equal(new[] { "Email", "InApp", "Webhook", "Sms" }, Enum.GetNames<NotificationChannelKind>());
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<NotificationRecipientKind>().Select(value => (int)value));
            Assert.Equal(new[] { "User", "Email", "Endpoint" }, Enum.GetNames<NotificationRecipientKind>());
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<NotificationSeverity>().Select(value => (int)value));
            Assert.Equal(new[] { "Information", "Warning", "Critical" }, Enum.GetNames<NotificationSeverity>());
        }

        [Fact]
        public async Task A_request_defaults_to_information_with_no_parameters_or_topic_and_the_port_returns_the_request_id()
        {
            var request = new NotificationDispatchRequest(
                "generic.message",
                new[] { new DispatchRecipient(NotificationRecipientKind.User, "7") },
                new[] { NotificationChannelKind.InApp });

            Assert.Null(request.Parameters);
            Assert.Equal(NotificationSeverity.Information, request.Severity);
            Assert.Null(request.Topic);
            Assert.Equal(new DispatchParameter("name", "Ada"), new DispatchParameter("name", "Ada"));

            INotificationDispatcher dispatcher = new RecordingDispatcher();
            var id = await dispatcher.DispatchAsync(request);
            Assert.Equal(RecordingDispatcher.Id, id);
        }

        [Fact]
        public void The_dispatch_flag_is_declared_beside_the_email_flag_and_seeds_on()
        {
            var dispatch = Assert.Single(Stomme.FeatureManagement.FeatureCatalog.All,
                descriptor => descriptor.Key == Stomme.FeatureManagement.FeatureCatalog.NotificationDispatch);
            var email = Assert.Single(Stomme.FeatureManagement.FeatureCatalog.All,
                descriptor => descriptor.Key == Stomme.FeatureManagement.FeatureCatalog.EmailNotifications);

            Assert.Equal("notifications.dispatch", dispatch.Key);
            Assert.Equal("Deliver queued notification requests through the channel pipeline.", dispatch.Description);
            Assert.Equal(email.Category, dispatch.Category);
            Assert.True(dispatch.DefaultEnabled);
        }

        private sealed class RecordingDispatcher : INotificationDispatcher
        {
            public static readonly Guid Id = Guid.NewGuid();

            public Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default) => Task.FromResult(Id);
        }
    }
}