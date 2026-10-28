using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Requests;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class BackgroundStimulusDispatcherTests
{
    private readonly ICommandSender _commandSender = Substitute.For<ICommandSender>();
    private readonly ITenantAccessor _tenantAccessor = Substitute.For<ITenantAccessor>();

    [Fact]
    public async Task SendAsync_DoesNotPropagateCallerToken()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var request = new DispatchStimulusRequest();
        using var callerCts = new CancellationTokenSource();

        // Act
        await dispatcher.SendAsync(request, callerCts.Token);

        // Assert - background commands run independently of caller's lifecycle
        await _commandSender.Received(1).SendAsync(
            Arg.Any<DispatchStimulusCommand>(),
            CommandStrategy.Background,
            Arg.Any<IDictionary<object, object>>(),
            CancellationToken.None);
    }

    private BackgroundStimulusDispatcher CreateDispatcher()
    {
        return new(_commandSender, _tenantAccessor);
    }
}
