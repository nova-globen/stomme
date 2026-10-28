using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class DefaultRegistriesPopulatorTests
{
    private readonly IPipelineDefinitionStorePopulator _pipelineDefinitionStorePopulatorMock;
    private readonly INotificationSender _notificationSenderMock;
    private readonly DefaultRegistriesPopulator _populator;

    public DefaultRegistriesPopulatorTests()
    {
        _pipelineDefinitionStorePopulatorMock = Substitute.For<IPipelineDefinitionStorePopulator>();
        var stepRegistryPopulatorMock = Substitute.For<IStepRegistryPopulator>();
        _notificationSenderMock = Substitute.For<INotificationSender>();

        _populator = new(
            _pipelineDefinitionStorePopulatorMock,
            stepRegistryPopulatorMock,
            _notificationSenderMock);
    }

    [Fact(DisplayName = "PopulateAsync publishes PipelineDefinitionsReloaded notification")]
    public async Task PopulateAsync_PublishesPipelineDefinitionsReloadedNotification()
    {
        // Arrange
        var pipelineDefinitions = new List<PipelineDefinition>
        {
            new()
            {
                Id = "1",
                DefinitionId = "def-1",
                Version = 1
            },
            new()
            {
                Id = "2",
                DefinitionId = "def-2",
                Version = 1
            }
        };

        _pipelineDefinitionStorePopulatorMock
            .PopulateStoreAsync(true, Arg.Any<CancellationToken>())
            .Returns(pipelineDefinitions);

        // Act
        await _populator.PopulateAsync();

        // Assert
        await _notificationSenderMock.Received(1).SendAsync(
            Arg.Is<PipelineDefinitionsReloaded>(n => n.ReloadedPipelineDefinitions.Count == 2),
            Arg.Any<CancellationToken>());
    }
}