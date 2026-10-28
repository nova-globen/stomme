#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;

namespace Stomme.Application.Modules.Notes.Services
{
    /// <summary>The contract the pages use: over HTTP in a browser, through the mediator on the server.</summary>
    public interface INotesService
    {
        Task<List<NoteModel>> ListAsync();

        Task<NoteModel?> GetAsync(Guid noteId);

        Task<NoteModel> CreateAsync(SaveNoteRequest request);

        Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request);

        Task DeleteAsync(Guid noteId);
    }
}
