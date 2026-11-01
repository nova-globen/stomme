#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Mediator;
using Stomme.Persistence.Abstractions;
using Stomme.Security.Abstractions;
using Stomme.Services;
using FluentValidation;

namespace Stomme.Application.Modules.Notes.Services
{
    /// <summary>
    /// The pages' service on the server: the same requests the API sends, so [Requires] applies alike. A refusal leaves it as
    /// <see cref="ServiceRefusalException"/> with the reason the API would give, as the browser's NotesService throws, so a
    /// page shows the same words on a Server site and on a WebAssembly site, and shows no other exception's text.
    /// </summary>
    public sealed class ServerNotesService(IMediator mediator) : INotesService
    {
        public async Task<List<NoteModel>> ListAsync() => (await Answer(() => mediator.Send(new ListNotesQuery()))).ToList();

        public async Task<NoteModel?> GetAsync(Guid noteId) => await Answer(() => mediator.Send(new GetNoteQuery(noteId)));

        public Task<NoteModel> CreateAsync(SaveNoteRequest request) =>
            Answer(() => mediator.Send(new CreateNoteCommand(request.Title, request.Body)));

        public Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request) =>
            Answer(() => mediator.Send(new EditNoteCommand(noteId, request.Title, request.Body)));

        public Task DeleteAsync(Guid noteId) => Answer(async () =>
        {
            await mediator.Send(new DeleteNoteCommand(noteId));
            return true;
        });

        // The controller's answers, as exceptions: the same reasons, the same statuses.
        private static async Task<T> Answer<T>(Func<Task<T>> send)
        {
            try
            {
                return await send();
            }
            catch (ValidationException ex)
            {
                throw new ServiceRefusalException(ex.Errors.Select(failure => failure.ErrorMessage).FirstOrDefault() ?? "The note is not valid.", HttpStatusCode.BadRequest);
            }
            catch (KeyNotFoundException)
            {
                throw new ServiceRefusalException("The note no longer exists.", HttpStatusCode.NotFound);
            }
            catch (NoteConflictException ex)
            {
                throw new ServiceRefusalException(ex.Message, HttpStatusCode.Conflict);
            }
            catch (ConcurrencyConflictException)
            {
                throw new ServiceRefusalException("Someone else changed this note in the meantime. Reload it and try again.", HttpStatusCode.Conflict);
            }
            catch (AccessDeniedException)
            {
                throw new ServiceRefusalException("You do not have permission to do that.", HttpStatusCode.Forbidden);
            }
            catch (ArgumentException ex)
            {
                throw new ServiceRefusalException(ex.Message, HttpStatusCode.BadRequest);
            }
        }
    }
}
