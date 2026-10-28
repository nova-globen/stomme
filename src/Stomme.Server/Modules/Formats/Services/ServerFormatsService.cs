#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Formats;
using Stomme.Mediator;
using Stomme.Modules.Formats.Application;
using Stomme.Modules.Formats.Services;
using Stomme.Shared;

namespace Stomme.Modules.Formats.Services
{
    /// <summary>The module's server IService (BL-94 increment 2): the client contract dispatched through the mediator. Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).</summary>
    public sealed class ServerFormatsService(IMediator mediator) : IFormatsService, ITransientService
    {
        public async Task<List<FormatDescriptor>> ListAsync(FormatKind? kind = null, FormatScope? scope = null) =>
            (await mediator.Send(new ListFormatsQuery(kind, null, scope))).ToList();

        public Task<FormatDescriptor?> GetAsync(Guid id) => mediator.Send(new GetFormatQuery(id));

        public async Task<FormatDescriptor?> SaveAsync(FormatDefinitionRequest request)
        {
            var id = await mediator.Send(new SaveFormatDefinitionCommand(request.Id, request.Name, request.Kind, request.Pattern, request.Culture, request.TimeZone));
            return await mediator.Send(new GetFormatQuery(id));
        }

        public Task DeleteAsync(Guid id) => mediator.Send(new DeleteFormatDefinitionCommand(id));
    }
}
