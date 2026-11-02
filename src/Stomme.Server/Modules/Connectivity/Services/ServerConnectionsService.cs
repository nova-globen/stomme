#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation;
using Stomme.Connectivity;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Services;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Connectivity.Services
{
    /// <summary>The module's server IService: the client contract dispatched through the mediator. Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).</summary>
    public sealed class ServerConnectionsService(IMediator mediator) : IConnectionsService, ITransientService
    {
        public async Task<List<ConnectionView>> ListAsync() => (await mediator.Send(new ListConnectionsQuery())).ToList();

        public Task<ConnectionView?> GetAsync(Guid id) => mediator.Send(new GetConnectionQuery(id));

        public async Task<List<ConnectorView>> ListConnectorsAsync() => (await mediator.Send(new ListConnectorsQuery())).ToList();

        public async Task<ConnectionView?> CreateAsync(ConnectionRequest request)
        {
            var id = await Refusing(() => mediator.Send(new CreateConnectionCommand(request.Name, request.ConnectorType, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds)));
            return await mediator.Send(new GetConnectionQuery(id));
        }

        public async Task<ConnectionView?> UpdateAsync(Guid id, ConnectionRequest request)
        {
            await Refusing(() => mediator.Send(new UpdateConnectionCommand(id, request.Name, request.Endpoint, request.Settings, request.Credentials, request.TimeoutSeconds)));
            return await mediator.Send(new GetConnectionQuery(id));
        }

        // A form left incomplete says what is missing in the validator's words, as the browser's HTTP service does from the
        // 400 (ServiceBase.ReadRefusalReasonAsync), never FluentValidation's own "Validation failed: -- Endpoint: ..." text.
        private static async Task<T> Refusing<T>(Func<Task<T>> send)
        {
            try
            {
                return await send();
            }
            catch (ValidationException validation)
            {
                var reason = validation.Errors.Select(error => error.ErrorMessage).FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "The connection is not complete.";
                throw new ServiceRefusalException(reason, HttpStatusCode.BadRequest);
            }
        }

        public Task<ConnectionTestView> TestAsync(Guid id) => mediator.Send(new TestConnectionCommand(id));

        public Task DeleteAsync(Guid id) => mediator.Send(new DeleteConnectionCommand(id));

        public Task<ConnectionEgressView> GetEgressAsync() => mediator.Send(new GetConnectionEgressQuery());

        public Task<ConnectionEgressView> SaveEgressAsync(string allowedHosts) => mediator.Send(new SaveConnectionEgressCommand(allowedHosts));
    }
}
