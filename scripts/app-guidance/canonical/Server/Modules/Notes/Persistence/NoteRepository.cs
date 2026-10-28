#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes.Persistence
{
    /// <summary>The host's repository for Note, with the module's reads. Every read is one site's.</summary>
    public interface INoteRepository : IRepository<Note, Guid>
    {
        Task<IReadOnlyList<Note>> ListForSiteAsync(int siteId, CancellationToken ct = default);

        Task<Note?> FindByTitleAsync(int siteId, string title, CancellationToken ct = default);

        /// <summary>The site's note with this id; null for an unknown id or another site's.</summary>
        Task<Note?> GetForSiteAsync(int siteId, Guid id, CancellationToken ct = default);
    }

    public sealed class NoteRepository(IRepository<Note, Guid> inner) : INoteRepository
    {
        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(Note aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(Note aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(Note aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        // Forward FindAsync explicitly: the interface's default implementation throws NotSupportedException.
        public Task<IReadOnlyList<Note>> FindAsync(Expression<Func<Note, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<Note>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(note => note.SiteId == siteId, ct))
                .OrderByDescending(note => note.UpdatedUtc)
                .ToList();

        // Text is compared through the normalised column, normalised here in C#: string equality differs between stores.
        public async Task<Note?> FindByTitleAsync(int siteId, string title, CancellationToken ct = default)
        {
            var key = Note.KeyOf(title);
            return (await inner.FindAsync(note => note.SiteId == siteId && note.TitleKey == key, ct)).FirstOrDefault();
        }

        public async Task<Note?> GetForSiteAsync(int siteId, Guid id, CancellationToken ct = default) =>
            await inner.GetByIdAsync(id, ct) is { } note && note.SiteId == siteId ? note : null;
    }
}
