using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Requests;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class BackgroundPipelineDispatcherTests
{
    private readonly ICommandSender _commandSender = Substitute.For<ICommandSender>();
    private readonly INotificationSender _notificationSender = Substitute.For<INotificationSender>();
    private readonly ITenantAccessor _tenantAccessor = Substitute.For<ITenantAccessor>();

    [Fact]
    public async Task DispatchPipelineDefinition_DoesNotPropagateCallerToken()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var request = new DispatchPipelineDefinitionRequest("test-definition-id");
        using var callerCts = new CancellationTokenSource();

        // Act
        await dispatcher.DispatchAsync(request, cancellationToken: callerCts.Token);

        // Assert
        await _commandSender.Received(1).SendAsync(
            Arg.Any<DispatchPipelineDefinitionCommand>(),
            CommandStrategy.Background,
            Arg.Any<IDictionary<object, object>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task DispatchPipelineInstance_DoesNotPropagateCallerToken()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var request = new DispatchPipelineInstanceRequest("test-instance-id");
        using var callerCts = new CancellationTokenSource();

        // Act
        await dispatcher.DispatchAsync(request, cancellationToken: callerCts.Token);

        // Assert
        await _commandSender.Received(1).SendAsync(
            Arg.Any<DispatchPipelineInstanceCommand>(),
            CommandStrategy.Background,
            Arg.Any<IDictionary<object, object>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task DispatchTriggerPipelines_DoesNotPropagateCallerToken()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var request = new DispatchTriggerPipelinesRequest("TestStepType", new { });
        using var callerCts = new CancellationTokenSource();

        // Act
        await dispatcher.DispatchAsync(request, cancellationToken: callerCts.Token);

        // Assert
        await _commandSender.Received(1).SendAsync(
            Arg.Any<DispatchTriggerPipelinesCommand>(),
            CommandStrategy.Background,
            Arg.Any<IDictionary<object, object>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task DispatchResumePipelines_DoesNotPropagateCallerToken()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var request = new DispatchResumePipelinesRequest("TestStepType", new { });
        using var callerCts = new CancellationTokenSource();

        // Act
        await dispatcher.DispatchAsync(request, cancellationToken: callerCts.Token);

        // Assert
        await _commandSender.Received(1).SendAsync(
            Arg.Any<DispatchResumePipelinesCommand>(),
            CommandStrategy.Background,
            Arg.Any<IDictionary<object, object>>(),
            CancellationToken.None);
    }

    private BackgroundPipelineDispatcher CreateDispatcher()
    {
        return new(_commandSender, _notificationSender, _tenantAccessor);
    }
}
