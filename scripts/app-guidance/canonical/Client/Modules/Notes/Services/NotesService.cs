#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Services
{
    /// <summary>INotesService over HTTP, for the browser. The server decides every permission again.</summary>
    public class NotesService : ServiceBase, INotesService
    {
        public NotesService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        // A refused call throws ServiceRefusalException with the server's reason, as the server implementation throws.
        protected override bool ThrowOnRefusal => true;

        private string ApiUrl => CreateApiUrl("Notes");

        public async Task<List<NoteModel>> ListAsync() =>
            await GetJsonAsync<List<NoteModel>>(ApiUrl) ?? new List<NoteModel>();

        public async Task<NoteModel?> GetAsync(Guid noteId) =>
            await GetJsonAsync<NoteModel?>($"{ApiUrl}/{noteId}");

        public async Task<NoteModel> CreateAsync(SaveNoteRequest request) =>
            await PostJsonAsync<SaveNoteRequest, NoteModel>(ApiUrl, request);

        public async Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request) =>
            await PutJsonAsync<SaveNoteRequest, NoteModel>($"{ApiUrl}/{noteId}", request);

        public async Task DeleteAsync(Guid noteId) =>
            await DeleteAsync($"{ApiUrl}/{noteId}");
    }
}
