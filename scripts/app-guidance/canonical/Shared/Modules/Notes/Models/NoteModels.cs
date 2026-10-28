using System;

namespace Stomme.Application.Modules.Notes.Models
{
    /// <summary>A note as the API and the pages see it. Never the aggregate itself.</summary>
    public sealed record NoteModel(Guid Id, string Title, string Body, int AuthorUserId, DateTime CreatedUtc, DateTime UpdatedUtc);

    /// <summary>What a caller sends to create or change a note. No tenant, site or author: the server decides those.</summary>
    public sealed class SaveNoteRequest
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }
}
