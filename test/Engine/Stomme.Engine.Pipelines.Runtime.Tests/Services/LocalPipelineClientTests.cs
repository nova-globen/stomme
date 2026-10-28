using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Exceptions;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Exceptions;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

public class LocalPipelineClientTests
{
    private readonly IPipelineInstanceManager _pipelineInstanceManager = Substitute.For<IPipelineInstanceManager>();
    private readonly IPipelineDefinitionService _pipelineDefinitionService = Substitute.For<IPipelineDefinitionService>();
    private readonly IPipelineRunner _pipelineRunner = Substitute.For<IPipelineRunner>();
    private readonly IPipelineCanceler _pipelineCanceler = Substitute.For<IPipelineCanceler>();
    private readonly PipelineStateMapper _pipelineStateMapper = Substitute.For<PipelineStateMapper>();
    private readonly IBookmarkManager _bookmarkManager = Substitute.For<IBookmarkManager>();
    private readonly ILogger<LocalPipelineClient> _logger = Substitute.For<ILogger<LocalPipelineClient>>();

    [Fact]
    public async Task CreateInstanceAsync_ThrowsPipelineDefinitionNotFoundException_WhenDefinitionDoesNotExist()
    {
        // Arrange
        var client = CreateClient();
        var definitionHandle = PipelineDefinitionHandle.ByDefinitionId("non-existent-definition");
        var request = new CreatePipelineInstanceRequest
        {
            PipelineDefinitionHandle = definitionHandle
        };

        var findResult = new PipelineGraphFindResult(null, null);

        _pipelineDefinitionService.TryFindPipelineGraphAsync(definitionHandle, Arg.Any<CancellationToken>())
            .Returns(findResult);

        // Act & Assert
        await Assert.ThrowsAsync<PipelineDefinitionNotFoundException>(() =>
            client.CreateInstanceAsync(request));
    }

    [Fact]
    public async Task CreateInstanceAsync_ThrowsPipelineMaterializerNotFoundException_WhenMaterializerNotAvailable()
    {
        // Arrange
        var client = CreateClient();
        var definitionHandle = PipelineDefinitionHandle.ByDefinitionId("test-definition");
        var request = new CreatePipelineInstanceRequest
        {
            PipelineDefinitionHandle = definitionHandle
        };

        var definition = new PipelineDefinition
        {
            Id = "def-1",
            DefinitionId = "test-definition",
            MaterializerName = "unavailable-materializer"
        };

        var findResult = new PipelineGraphFindResult(definition, null); // Null graph indicates materializer not available

        _pipelineDefinitionService.TryFindPipelineGraphAsync(definitionHandle, Arg.Any<CancellationToken>())
            .Returns(findResult);

        // Act & Assert
        await Assert.ThrowsAsync<PipelineMaterializerNotFoundException>(() => client.CreateInstanceAsync(request));
    }

    [Fact]
    public async Task CreateAndRunInstanceAsync_ThrowsPipelineDefinitionNotFoundException_WhenDefinitionDoesNotExist()
    {
        // Arrange
        var client = CreateClient();
        var definitionHandle = PipelineDefinitionHandle.ByDefinitionId("non-existent-definition");
        var request = new CreateAndRunPipelineInstanceRequest
        {
            PipelineDefinitionHandle = definitionHandle
        };

        var findResult = new PipelineGraphFindResult(null, null);

        _pipelineDefinitionService.TryFindPipelineGraphAsync(definitionHandle, Arg.Any<CancellationToken>())
            .Returns(findResult);

        // Act & Assert
        await Assert.ThrowsAsync<PipelineDefinitionNotFoundException>(() =>
            client.CreateAndRunInstanceAsync(request));
    }

    [Fact]
    public async Task CreateAndRunInstanceAsync_ThrowsPipelineMaterializerNotFoundException_WhenMaterializerNotAvailable()
    {
        // Arrange
        var client = CreateClient();
        var definitionHandle = PipelineDefinitionHandle.ByDefinitionId("test-definition");
        var request = new CreateAndRunPipelineInstanceRequest
        {
            PipelineDefinitionHandle = definitionHandle
        };

        var definition = new PipelineDefinition
        {
            Id = "def-1",
            DefinitionId = "test-definition",
            MaterializerName = "unavailable-materializer"
        };

        var findResult = new PipelineGraphFindResult(definition, null);

        _pipelineDefinitionService.TryFindPipelineGraphAsync(definitionHandle, Arg.Any<CancellationToken>())
            .Returns(findResult);

        // Act & Assert
        await Assert.ThrowsAsync<PipelineMaterializerNotFoundException>(() =>
            client.CreateAndRunInstanceAsync(request));
    }

    private LocalPipelineClient CreateClient()
    {
        return new(
            "test-pipeline-instance-id",
            _pipelineInstanceManager,
            _pipelineDefinitionService,
            _pipelineRunner,
            _pipelineCanceler,
            _pipelineStateMapper,
            _bookmarkManager,
            _logger);
    }
}
