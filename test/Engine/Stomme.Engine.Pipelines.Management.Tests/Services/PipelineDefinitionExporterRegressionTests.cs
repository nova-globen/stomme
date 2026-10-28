using System.IO.Compression;
using System.Text.Json;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Management.Tests.Services;

public class PipelineDefinitionExporterRegressionTests
{
    private readonly IPipelineDefinitionStore _store = Substitute.For<IPipelineDefinitionStore>();
    private readonly IApiSerializer _serializer = Substitute.For<IApiSerializer>();
    private readonly IPipelineDefinitionService _pipelineDefinitionService = Substitute.For<IPipelineDefinitionService>();
    private readonly IPipelineReferenceGraphBuilder _pipelineReferenceGraphBuilder = Substitute.For<IPipelineReferenceGraphBuilder>();

    [Fact]
    public async Task ExportManyAsync_WithSlashInPipelineName_CreatesFlatZipEntry()
    {
        var definition = new PipelineDefinition
        {
            Id = "slash-name-pipeline-v1",
            DefinitionId = "slash-name-pipeline",
            Name = "folder/child",
            CreatedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Version = 1,
            IsLatest = true,
            IsPublished = true,
            MaterializerName = "Json"
        };

        _serializer.GetOptions().Returns(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _store.FindManyAsync(Arg.Any<PipelineDefinitionFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IEnumerable<PipelineDefinition>>([definition]));
        _pipelineDefinitionService.MaterializePipelineAsync(definition, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreatePipelineGraph()));

        var sut = CreateExporter();
        var result = await sut.ExportManyAsync([definition.Id]);

        Assert.NotNull(result);
        Assert.Equal("pipeline-definitions.zip", result.FileName);

        using var zipStream = new MemoryStream(result.Data);
        await using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = Assert.Single(zipArchive.Entries);

        Assert.Equal(entry.Name, entry.FullName);
        Assert.DoesNotContain('/', entry.FullName);
        Assert.DoesNotContain('\\', entry.FullName);
        Assert.Equal("pipeline-definition-folder-child-slash-name-pipeline-slash-name-pipeline-v1.json", entry.FullName);
    }

    private PipelineDefinitionExporter CreateExporter()
    {
        var stepSerializer = Substitute.For<IStepSerializer>();
        var wellKnownTypeRegistry = Substitute.For<IWellKnownTypeRegistry>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var variableDefinitionMapper = new VariableDefinitionMapper(wellKnownTypeRegistry, scopeFactory, NullLogger<VariableDefinitionMapper>.Instance);
        var pipelineDefinitionMapper = new PipelineDefinitionMapper(stepSerializer, _pipelineDefinitionService, variableDefinitionMapper);
        var fileNameSanitizer = new DefaultFileNameSanitizer();

        return new(_store, _serializer, pipelineDefinitionMapper, _pipelineReferenceGraphBuilder, fileNameSanitizer);
    }

    private static PipelineGraph CreatePipelineGraph()
    {
        var root = new Sequence { Id = "root" };
        var rootNode = new StepNode(root, "Root");
        var pipeline = new Pipeline(root);

        return new(pipeline, rootNode, [rootNode]);
    }
}