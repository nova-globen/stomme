#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Mediator;

namespace Stomme.Application.Modules.Notes.Services
{
    /// <summary>The pages' service on the server: the same requests the API sends, so [Requires] applies alike.</summary>
    public sealed class ServerNotesService(IMediator mediator) : INotesService
    {
        public async Task<List<NoteModel>> ListAsync() => (await mediator.Send(new ListNotesQuery())).ToList();

        public Task<NoteModel?> GetAsync(Guid noteId) => mediator.Send(new GetNoteQuery(noteId));

        public Task<NoteModel> CreateAsync(SaveNoteRequest request) =>
            mediator.Send(new CreateNoteCommand(request.Title, request.Body));

        public Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request) =>
            mediator.Send(new EditNoteCommand(noteId, request.Title, request.Body));

        public async Task DeleteAsync(Guid noteId) => await mediator.Send(new DeleteNoteCommand(noteId));
    }
}
