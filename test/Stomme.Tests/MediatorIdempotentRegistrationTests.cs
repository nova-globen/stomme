using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>AddMediator</c> is idempotent. An app whose Server assembly holds two modules, each startup calling
    /// <c>AddMediator&lt;T&gt;()</c> over the same assembly, registered every handler twice, so each notification handler ran
    /// twice (found by the app-guidance writers). Now each handler is registered once however often it is called.
    /// </summary>
    public sealed class MediatorIdempotentRegistrationTests
    {
        public sealed record ProbeNotice : INotification;

        public sealed class ProbeNoticeHandler : INotificationHandler<ProbeNotice>
        {
            public Task Handle(ProbeNotice notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Fact]
        public void Calling_AddMediator_twice_registers_each_handler_once()
        {
            var services = new ServiceCollection();
            services.AddMediator<ProbeNotice>();
            services.AddMediator<ProbeNotice>();

            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INotificationHandler<ProbeNotice>) && descriptor.ImplementationType == typeof(ProbeNoticeHandler));
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediator));
            using var provider = services.BuildServiceProvider();
            Assert.Single(provider.GetServices<INotificationHandler<ProbeNotice>>().OfType<ProbeNoticeHandler>());
        }
    }
}
