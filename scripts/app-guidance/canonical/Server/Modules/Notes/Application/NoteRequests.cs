#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using FluentValidation;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>A change refused because it clashes with what is stored (the API answers 409). <see cref="IExpectedRefusal"/>: the
    /// mediator logs it as a refusal, not as an error.</summary>
    public sealed class NoteConflictException(string message) : Exception(message), IExpectedRefusal;

    // ---- Queries ---------------------------------------------------------------------------------------------------

    [Requires(NotesPermissions.View)]
    public sealed record ListNotesQuery : IQuery<IReadOnlyList<NoteModel>>;

    public sealed class ListNotesQueryHandler(INoteRepository notes, SiteState siteState)
        : IQueryHandler<ListNotesQuery, IReadOnlyList<NoteModel>>
    {
        public async Task<IReadOnlyList<NoteModel>> Handle(ListNotesQuery request, CancellationToken ct) =>
            (await notes.ListForSiteAsync(NotesScope.Site(siteState), ct)).Select(NotesScope.ToModel).ToList();
    }

    [Requires(NotesPermissions.View)]
    public sealed record GetNoteQuery(Guid NoteId) : IQuery<NoteModel?>;

    public sealed class GetNoteQueryHandler(INoteRepository notes, SiteState siteState)
        : IQueryHandler<GetNoteQuery, NoteModel?>
    {
        public async Task<NoteModel?> Handle(GetNoteQuery request, CancellationToken ct) =>
            await notes.GetForSiteAsync(NotesScope.Site(siteState), request.NoteId, ct) is { } note
                ? NotesScope.ToModel(note)
                : null;
    }

    // ---- Commands --------------------------------------------------------------------------------------------------

    [Requires(NotesPermissions.Edit)]
    public sealed record CreateNoteCommand(string Title, string Body) : ICommand<NoteModel>;

    public sealed class CreateNoteCommandValidator : AbstractValidator<CreateNoteCommand>
    {
        public CreateNoteCommandValidator()
        {
            RuleFor(command => command.Title)
                .NotEmpty().WithMessage("Give the note a title.")
                .MaximumLength(Note.MaxTitleLength);
            RuleFor(command => command.Body)
                .MaximumLength(Note.MaxBodyLength);
        }
    }

    public sealed class CreateNoteCommandHandler(
        INoteRepository notes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        ICurrentUser currentUser,
        TimeProvider clock) : ICommandHandler<CreateNoteCommand, NoteModel>
    {
        public async Task<NoteModel> Handle(CreateNoteCommand request, CancellationToken ct)
        {
            var (tenantId, siteId) = NotesScope.Resolve(tenantContext, siteState);
            var author = currentUser.UserId ?? throw new AccessDeniedException("A note is written by a signed-in user.");
            if (await notes.FindByTitleAsync(siteId, request.Title, ct) is not null)
            {
                throw new NoteConflictException($"A note titled '{request.Title.Trim()}' already exists on this site.");
            }

            var note = Note.Create(Guid.NewGuid(), tenantId, siteId, author, request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
            await notes.AddAsync(note, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return NotesScope.ToModel(note);
        }
    }

    [Requires(NotesPermissions.Edit)]
    public sealed record EditNoteCommand(Guid NoteId, string Title, string Body) : ICommand<NoteModel>;

    public sealed class EditNoteCommandValidator : AbstractValidator<EditNoteCommand>
    {
        public EditNoteCommandValidator()
        {
            RuleFor(command => command.NoteId).NotEmpty();
            RuleFor(command => command.Title)
                .NotEmpty().WithMessage("Give the note a title.")
                .MaximumLength(Note.MaxTitleLength);
            RuleFor(command => command.Body)
                .MaximumLength(Note.MaxBodyLength);
        }
    }

    public sealed class EditNoteCommandHandler(
        INoteRepository notes,
        IUnitOfWork unitOfWork,
        SiteState siteState,
        ICurrentUser currentUser,
        IAccessGuard accessGuard,
        TimeProvider clock) : ICommandHandler<EditNoteCommand, NoteModel>
    {
        public async Task<NoteModel> Handle(EditNoteCommand request, CancellationToken ct)
        {
            var siteId = NotesScope.Site(siteState);
            var note = await notes.GetForSiteAsync(siteId, request.NoteId, ct)
                ?? throw new KeyNotFoundException($"Note '{request.NoteId}' was not found.");
            await NotesScope.EnsureMayChangeAsync(note, currentUser, accessGuard, ct);
            if (await notes.FindByTitleAsync(siteId, request.Title, ct) is { } other && other.Id != note.Id)
            {
                throw new NoteConflictException($"A note titled '{request.Title.Trim()}' already exists on this site.");
            }

            // Read, change, save THAT instance: the module store's repositories do not track reads.
            note.Edit(request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
            await notes.UpdateAsync(note, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return NotesScope.ToModel(note);
        }
    }

    [Requires(NotesPermissions.Edit)]
    public sealed record DeleteNoteCommand(Guid NoteId) : ICommand;

    public sealed class DeleteNoteCommandHandler(
        INoteRepository notes,
        IUnitOfWork unitOfWork,
        SiteState siteState,
        ICurrentUser currentUser,
        IAccessGuard accessGuard) : ICommandHandler<DeleteNoteCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteNoteCommand request, CancellationToken ct)
        {
            var note = await notes.GetForSiteAsync(NotesScope.Site(siteState), request.NoteId, ct)
                ?? throw new KeyNotFoundException($"Note '{request.NoteId}' was not found.");
            await NotesScope.EnsureMayChangeAsync(note, currentUser, accessGuard, ct);
            await notes.DeleteAsync(note, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>The request's site and tenant - refused when missing, never guessed - the per-row check, and the wire mapping.</summary>
    internal static class NotesScope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0
                ? siteId
                : throw new InvalidOperationException("Notes belong to the request's site; none is resolved.");

        public static (TenantId TenantId, int SiteId) Resolve(ITenantContext tenantContext, SiteState siteState) =>
            (tenantContext.CurrentTenantId
                ?? throw new InvalidOperationException("Notes belong to a tenant; none is resolved."),
             Site(siteState));

        // [Requires] decided the key; the ROW decides the rest: the author, or a holder of notes:manage.
        public static async Task EnsureMayChangeAsync(Note note, ICurrentUser currentUser, IAccessGuard accessGuard, CancellationToken ct)
        {
            var isAuthor = currentUser.UserId is int userId && note.AuthorUserId == userId;
            if (!isAuthor && !await accessGuard.IsAllowedAsync(null, NotesPermissions.Manage, ct))
            {
                throw new AccessDeniedException("Only the note's author or a notes manager may change it.");
            }
        }

        public static NoteModel ToModel(Note note) =>
            new(note.Id, note.Title, note.Body, note.AuthorUserId, note.CreatedUtc, note.UpdatedUtc);
    }
}
