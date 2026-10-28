using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Matches;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Parameters;
using Stomme.Engine.Pipelines.Runtime.Params;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Results;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

public partial class LocalPipelineRuntime
{
    private readonly Lazy<ObsoletePipelineRuntime> _obsoleteApi;
    private ObsoletePipelineRuntime ObsoleteApi => _obsoleteApi.Value;

    public Task<CanStartPipelineResult> CanStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null) => ObsoleteApi.CanStartPipelineAsync(definitionId, options);
    public Task<PipelineExecutionResult> StartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null) => ObsoleteApi.StartPipelineAsync(definitionId, options);
    public Task<ICollection<PipelineExecutionResult>> StartPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null) => ObsoleteApi.StartPipelinesAsync(stepTypeName, bookmarkPayload, options);
    public Task<PipelineExecutionResult?> TryStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = null) => ObsoleteApi.TryStartPipelineAsync(definitionId, options);
    public Task<PipelineExecutionResult?> ResumePipelineAsync(string pipelineInstanceId, ResumePipelineRuntimeParams? options = null) => ObsoleteApi.ResumePipelineAsync(pipelineInstanceId, options);
    public Task<ICollection<PipelineExecutionResult>> ResumePipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null) => ObsoleteApi.ResumePipelinesAsync(stepTypeName, bookmarkPayload, options);
    public Task<TriggerPipelinesResult> TriggerPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = null) => ObsoleteApi.TriggerPipelinesAsync(stepTypeName, bookmarkPayload, options);
    public Task<PipelineExecutionResult> ExecutePipelineAsync(PipelineMatch match, ExecutePipelineParams? options = default) => ObsoleteApi.ExecutePipelineAsync(match, options);
    public Task<CancellationResult> CancelPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default) => ObsoleteApi.CancelPipelineAsync(pipelineInstanceId, cancellationToken);
    public Task<IEnumerable<PipelineMatch>> FindPipelinesAsync(PipelinesFilter filter, CancellationToken cancellationToken = default) => ObsoleteApi.FindPipelinesAsync(filter, cancellationToken);
    public Task<PipelineState?> ExportPipelineStateAsync(string pipelineInstanceId, CancellationToken cancellationToken = default) => ObsoleteApi.ExportPipelineStateAsync(pipelineInstanceId, cancellationToken);
    public Task ImportPipelineStateAsync(PipelineState pipelineState, CancellationToken cancellationToken = default) => ObsoleteApi.ImportPipelineStateAsync(pipelineState, cancellationToken);
    public Task UpdateBookmarkAsync(StoredBookmark bookmark, CancellationToken cancellationToken = default) => ObsoleteApi.UpdateBookmarkAsync(bookmark, cancellationToken);
    public Task<long> CountRunningPipelinesAsync(CountRunningPipelinesRequest request, CancellationToken cancellationToken = default) => ObsoleteApi.CountRunningPipelinesAsync(request, cancellationToken);
}