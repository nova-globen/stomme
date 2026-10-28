#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Services;
using Stomme.Shared;

namespace Stomme.Modules.Datasets.Services
{
    /// <summary>The module's server IService (BL-94 increment 10): the client contract dispatched through the mediator. Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).</summary>
    public sealed class ServerDatasetsService(IMediator mediator) : IDatasetsService, ITransientService
    {
        public async Task<List<DatasetDescriptor>> ListAsync() => (await mediator.Send(new ListDatasetsQuery())).ToList();

        public Task<DatasetDescriptor?> GetAsync(Guid id) => mediator.Send(new GetDatasetQuery(id));

        public Task<DatasetPreviewRows> PreviewAsync(Guid id) => mediator.Send(new PreviewDatasetRowsQuery(id));

        public async Task<DatasetDescriptor?> RegisterAsync(RegisterDatasetRequest request)
        {
            var id = await mediator.Send(new RegisterDatasetCommand(request.Name, request.OriginKind, request.OriginReference, request.SampleJson, request.Classification));
            return await mediator.Send(new GetDatasetQuery(id));
        }

        public async Task<DatasetDescriptor?> UploadAsync(UploadDatasetRequest request)
        {
            var id = await mediator.Send(new RegisterUploadedDatasetCommand(request.FileId, request.Name, request.Classification));
            return await mediator.Send(new GetDatasetQuery(id));
        }

        public async Task<DatasetDescriptor?> RefreshAsync(Guid id, RefreshDatasetRequest request)
        {
            await mediator.Send(new RefreshDatasetCommand(id, request.SampleJson));
            return await mediator.Send(new GetDatasetQuery(id));
        }

        public Task DeleteAsync(Guid id) => mediator.Send(new DeleteDatasetCommand(id));
    }
}
