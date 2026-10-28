#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Services;
using Stomme.Shared;

namespace Stomme.Modules.Connectivity.Services
{
    /// <summary>The module's server IService (BL-94 increment 7): the client contract dispatched through the mediator. Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).</summary>
    public sealed class ServerConnectionsService(IMediator mediator) : IConnectionsService, ITransientService
    {
        public async Task<List<ConnectionView>> ListAsync() => (await mediator.Send(new ListConnectionsQuery())).ToList();

        public Task<ConnectionView?> GetAsync(Guid id) => mediator.Send(new GetConnectionQuery(id));

        public async Task<List<ConnectorView>> ListConnectorsAsync() => (await mediator.Send(new ListConnectorsQuery())).ToList();

        public async Task<ConnectionView?> CreateAsync(ConnectionRequest request)
        {
            var id = await mediator.Send(new CreateConnectionCommand(request.Name, request.ConnectorType, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds));
            return await mediator.Send(new GetConnectionQuery(id));
        }

        public async Task<ConnectionView?> UpdateAsync(Guid id, ConnectionRequest request)
        {
            await mediator.Send(new UpdateConnectionCommand(id, request.Name, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds));
            return await mediator.Send(new GetConnectionQuery(id));
        }

        public Task<ConnectionTestView> TestAsync(Guid id) => mediator.Send(new TestConnectionCommand(id));

        public Task DeleteAsync(Guid id) => mediator.Send(new DeleteConnectionCommand(id));

        public Task<ConnectionEgressView> GetEgressAsync() => mediator.Send(new GetConnectionEgressQuery());

        public Task<ConnectionEgressView> SaveEgressAsync(string allowedHosts) => mediator.Send(new SaveConnectionEgressCommand(allowedHosts));
    }
}
