using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Materializers;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Services
{
    /// <inheritdoc />
    public class PipelineDefinitionImporter : IPipelineDefinitionImporter
    {
        private readonly IStepSerializer _serializer;
        private readonly IPipelineDefinitionPublisher _pipelineDefinitionPublisher;
        private readonly VariableDefinitionMapper _variableDefinitionMapper;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipelineDefinitionImporter"/> class.
        /// </summary>
        public PipelineDefinitionImporter(
            IStepSerializer serializer,
            IPipelineDefinitionPublisher pipelineDefinitionPublisher,
            VariableDefinitionMapper variableDefinitionMapper)
        {
            _serializer = serializer;
            _pipelineDefinitionPublisher = pipelineDefinitionPublisher;
            _variableDefinitionMapper = variableDefinitionMapper;
        }

        /// <inheritdoc />
        public async Task<ImportPipelineResult> ImportAsync(SavePipelineDefinitionRequest request, CancellationToken cancellationToken = default)
        {
            var model = request.Model;
            var definitionId = model.DefinitionId;

            // Get a pipeline draft version.
            var draft = !string.IsNullOrWhiteSpace(definitionId)
                ? await _pipelineDefinitionPublisher.GetDraftAsync(definitionId, VersionOptions.Latest, cancellationToken).ConfigureAwait(false)
                : null;

            var isNew = draft == null;

            // Create a new pipeline in case no existing definition was found.
            if (isNew)
            {
                draft = await _pipelineDefinitionPublisher.NewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(definitionId))
                    draft.DefinitionId = definitionId;
                
                if (!string.IsNullOrWhiteSpace(model.Id))
                    draft.Id = model.Id;
            }

            // Update the draft with the received model.
            var root = model.Root ?? new Sequence();
            var stringData = _serializer.Serialize(root);
            var variables = _variableDefinitionMapper.Map(model.Variables).ToList();

            draft!.StringData = stringData;
            draft.MaterializerName = JsonPipelineMaterializer.MaterializerName;
            draft.Name = model.Name?.Trim();
            draft.Description = model.Description?.Trim();
            draft.CustomProperties = model.CustomProperties ?? new Dictionary<string, object>();
            draft.Variables = variables;
            draft.Inputs = model.Inputs ?? new List<InputDefinition>();
            draft.Outputs = model.Outputs ?? new List<OutputDefinition>();
            draft.Outcomes = model.Outcomes ?? new List<string>();
            draft.IsReadonly = model.IsReadonly;
            draft.Options = model.Options ?? new PipelineOptions();
            draft.ToolVersion = model.ToolVersion;

            if (request.Publish ?? model.IsPublished)
            {
                var result = await _pipelineDefinitionPublisher.PublishAsync(draft, cancellationToken).ConfigureAwait(false);
                return new ImportPipelineResult(result.Succeeded, draft, result.ValidationErrors);
            }

            await _pipelineDefinitionPublisher.SaveDraftAsync(draft, cancellationToken).ConfigureAwait(false);
            return new ImportPipelineResult(true, draft, new List<PipelineValidationError>());
        }
    }
}