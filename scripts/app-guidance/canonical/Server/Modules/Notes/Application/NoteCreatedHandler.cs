#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>Runs inside IUnitOfWork.SaveChangesAsync after a note is created. Stages work; never saves.</summary>
    public sealed class NoteCreatedHandler(ILogger<NoteCreatedHandler> logger) : IDomainEventHandler<NoteCreatedEvent>
    {
        public Task HandleAsync(NoteCreatedEvent domainEvent, CancellationToken ct = default)
        {
            logger.LogInformation("Note {NoteId} created on site {SiteId}.", domainEvent.NoteId, domainEvent.SiteId);
            return Task.CompletedTask;
        }
    }
}
