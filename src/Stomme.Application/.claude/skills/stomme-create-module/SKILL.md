---
name: stomme-create-module
description: Use when adding a new module (a feature placed on a page) to this Nova Globen Stomme app - "create a module", "add a feature page", "new module with its own data and API" - end to end across Shared, Client and Server, storage-blind, inside the app's own projects.
---

# Create a module inside the app

You will have built a module called **Notes** inside this app's three projects: a storage-blind, tenant-owned aggregate
(`Note`) with a repository, queries and commands behind three permission keys, an API at `api/Notes`, a service contract
with a server and an HTTP implementation, a list page and an add/edit page, and a page template that puts the module on
`/notes` on every site. This is the reference module every Stomme skill in this app uses; it was built, run and driven
on Stomme's packages exactly as written here. Replace "Notes"/"Note" with your module's name throughout. Read first:
[Module anatomy](../../../docs/modules/anatomy.md) (discovery, the three projects) and
[Module UI](../../../docs/modules/module-ui.md). For depth on each layer see
[Module persistence](../../../docs/persistence/module-persistence.md),
[Application layer](../../../docs/modules/application-layer.md),
[API and client services](../../../docs/modules/api-and-client-services.md) and the skills
[stomme-add-aggregate](../stomme-add-aggregate/SKILL.md),
[stomme-add-command-or-query](../stomme-add-command-or-query/SKILL.md),
[stomme-add-api-endpoint](../stomme-add-api-endpoint/SKILL.md),
[stomme-secure-a-module](../stomme-secure-a-module/SKILL.md) and
[stomme-build-module-ui](../stomme-build-module-ui/SKILL.md), which explain each file below.

**The convention this skill uses** (it is what discovery needs):

- One namespace root for the module in all three projects: `Stomme.Application.Modules.Notes`, with sub-namespaces
  (`.Models`, `.Services`, `.Domain`, `.Persistence`, `.Application`, `.Controllers`, `.Startup`).
- In `Client`, `ModuleInfo` and EVERY `.razor` component of the module are in exactly `Stomme.Application.Modules.Notes`
  (set with `@namespace`). That namespace plus the `Client` assembly name is the module's identity.
- Files go in a `Modules/Notes` folder in each project. The app's assembly names already end in `.Stomme`
  (discovery needs `Stomme` in the name); do not change them.
- The startups are named after the module (`NotesServerStartup`, `NotesClientStartup`): every module of the app shares one
  `Server` and one `Client` assembly, and distinct names keep logs and `using` lines unambiguous.

Make it a separate package instead only when other apps will use it; see the table in
[Module anatomy](../../../docs/modules/anatomy.md#in-the-app-or-as-a-package).

## Steps

### 1. Shared: keys, wire models, service contract

`Shared/Modules/Notes/NotesPermissions.cs`:

```csharp
using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes
{
    /// <summary>The module's permission keys: "resource:operation", lower-case.</summary>
    public static class NotesPermissions
    {
        /// <summary>Read the site's notes.</summary>
        public const string View = "notes:view";

        /// <summary>Write notes, and change or delete your own.</summary>
        public const string Edit = "notes:edit";

        /// <summary>Change or delete anyone's note.</summary>
        public const string Manage = "notes:manage";

        /// <summary>Each key with the roles a site grants it to. PermissionKeys.Declare adds Administrators to every key.</summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { RoleNames.Registered }),
            (Edit, new[] { RoleNames.Registered }),
            (Manage, new[] { RoleNames.Admin }),
        };
    }
}
```

The key's resource (`notes`) must not be one of the framework's entity names (`page`, `module`, `user`, `file`, `folder`,
`site`, `role`, `notification`, ...).

`Shared/Modules/Notes/Models/NoteModels.cs`:

```csharp
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
```

`Shared/Modules/Notes/Services/INotesService.cs`:

```csharp
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
```

### 2. Server: the aggregate

`Server/Modules/Notes/Domain/Note.cs`:

```csharp
#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Domain
{
    public sealed record NoteCreatedEvent(Guid NoteId, int SiteId, int AuthorUserId) : DomainEvent;

    /// <summary>A note on one site of a tenant. Storage-blind and tenant-owned.</summary>
    public sealed class Note : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxTitleLength = 120;

        public const int MaxBodyLength = 4000;

        // Every store builds the aggregate through this constructor and then sets the properties. Not for application code.
        private Note()
        {
            TenantId = null!;
            Title = string.Empty;
            TitleKey = string.Empty;
            Body = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Title { get; private set; }

        /// <summary>The title normalised for lookups: trimmed, upper-case invariant. Set with Title, never alone.</summary>
        public string TitleKey { get; private set; }

        public string Body { get; private set; }

        public int AuthorUserId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public static string KeyOf(string title) => (title ?? string.Empty).Trim().ToUpperInvariant();

        public static Note Create(Guid id, TenantId tenantId, int siteId, int authorUserId, string title, string body, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A note belongs to a site.");
            }

            if (authorUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorUserId), "A note is written by a signed-in user.");
            }

            var note = new Note
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                AuthorUserId = authorUserId,
                CreatedUtc = nowUtc,
            };
            note.Apply(title, body, nowUtc);
            note.RaiseDomainEvent(new NoteCreatedEvent(note.Id, note.SiteId, note.AuthorUserId));
            return note;
        }

        public void Edit(string title, string body, DateTime nowUtc) => Apply(title, body, nowUtc);

        private void Apply(string title, string body, DateTime nowUtc)
        {
            var trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0 || trimmedTitle.Length > MaxTitleLength)
            {
                throw new ArgumentException($"A title is 1 to {MaxTitleLength} characters.", nameof(title));
            }

            var trimmedBody = (body ?? string.Empty).Trim();
            if (trimmedBody.Length > MaxBodyLength)
            {
                throw new ArgumentException($"A note is at most {MaxBodyLength} characters.", nameof(body));
            }

            Title = trimmedTitle;
            TitleKey = KeyOf(trimmedTitle);
            Body = trimmedBody;
            UpdatedUtc = nowUtc;
        }
    }
}
```

Give the aggregate a class name that no other module of the app uses: it becomes the table name in a store all modules
share.

### 3. Server: the repository

`Server/Modules/Notes/Persistence/NoteRepository.cs`:

```csharp
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
```

### 4. Server: the queries and the commands

`Server/Modules/Notes/Application/NoteRequests.cs`:

```csharp
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
```

`[Requires]` makes the host's mediator refuse a caller without the key before the handler runs - from the API, a page or
a job alike. Handlers and request types must be `public`: the mediator invokes them across assemblies.

`Server/Modules/Notes/Application/NoteCreatedHandler.cs` (optional - it shows where a reaction to a domain event goes):

```csharp
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
```

### 5. Server: the API

`Server/Modules/Notes/Controllers/NotesController.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Application.Modules.Notes.Controllers
{
    /// <summary>
    /// api/Notes. Each action is gated by a permission key before it runs, and the request it sends is gated again by its
    /// [Requires]. 400: invalid input; 403: not allowed; 404: unknown or another site's note; 409: a clash or a stale write.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class NotesController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public NotesController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Notes
        [HttpGet]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> List(CancellationToken ct) =>
            Ok(await _mediator.Send(new ListNotesQuery(), ct));

        // GET api/Notes/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNoteQuery(id), ct) is { } note ? Ok(note) : NotFound();

        // POST api/Notes
        [HttpPost]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Create([FromBody] SaveNoteRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new CreateNoteCommand(request.Title, request.Body), ct)));

        // PUT api/Notes/{id}
        [HttpPut("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Edit(Guid id, [FromBody] SaveNoteRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new EditNoteCommand(id, request.Title, request.Body), ct)));

        // DELETE api/Notes/{id}
        [HttpDelete("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
            Answer(async () =>
            {
                await _mediator.Send(new DeleteNoteCommand(id), ct);
                return NoContent();
            });

        // AccessDeniedException is not caught: the host turns it into 403. These are the other answers.
        private async Task<IActionResult> Answer(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (ValidationException ex)
            {
                foreach (var failure in ex.Errors)
                {
                    ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                }

                return ValidationProblem(ModelState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (NoteConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (ConcurrencyConflictException)
            {
                return Conflict(new { error = "Someone else changed this note in the meantime. Reload it and try again." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
```

### 6. Server: the service for server-side rendering

`Server/Modules/Notes/Services/ServerNotesService.cs`:

```csharp
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
```

### 7. Server: the startup

`Server/Modules/Notes/Startup/NotesServerStartup.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Application.Modules.Notes.Services;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.SharedKernel.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Application.Modules.Notes.Startup
{
    /// <summary>Found by the host's convention scan; names no store.</summary>
    public class NotesServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);

            // The aggregate on whichever store the installation chose, and the module's repository over it.
            services.AddModuleAggregatePersistence<Note, Guid>();
            services.AddScoped<INoteRepository, NoteRepository>();

            // The handlers and validators, registered explicitly. The host has already composed the mediator; do not call
            // AddMediator here - every module of the app is in this one Server assembly, and each call registers all of them.
            services.AddScoped<IRequestHandler<ListNotesQuery, IReadOnlyList<NoteModel>>, ListNotesQueryHandler>();
            services.AddScoped<IRequestHandler<GetNoteQuery, NoteModel?>, GetNoteQueryHandler>();
            services.AddScoped<IRequestHandler<CreateNoteCommand, NoteModel>, CreateNoteCommandHandler>();
            services.AddScoped<IRequestHandler<EditNoteCommand, NoteModel>, EditNoteCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteNoteCommand, Unit>, DeleteNoteCommandHandler>();
            services.AddScoped<IValidator<CreateNoteCommand>, CreateNoteCommandValidator>();
            services.AddScoped<IValidator<EditNoteCommand>, EditNoteCommandValidator>();
            services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedHandler>();

            // The server side of the contract. On the server it is what resolves: the client startup's guard skips its HTTP service when this ran first, and a later registration wins when it did not.
            services.AddTransient<INotesService, ServerNotesService>();
        }
    }
}
```

### 8. Client: the HTTP service and its registration

`Client/Modules/Notes/Services/NotesService.cs`:

```csharp
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
```

`Client/Modules/Notes/Startup/NotesClientStartup.cs`:

```csharp
using System.Linq;
using Stomme.Application.Modules.Notes.Services;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Modules.Notes.Startup
{
    /// <summary>
    /// Registers the HTTP service for the browser. On the server both startups run, assembly by assembly: if the server's
    /// ran first its service is there and this guard skips; if this one ran first, the server's later registration wins.
    /// </summary>
    public class NotesClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotesService)))
            {
                services.AddScoped<INotesService, NotesService>();
            }
        }
    }
}
```

### 9. Client: the module definition

`Client/Modules/Notes/ModuleInfo.cs`:

```csharp
using System.Collections.Generic;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes
{
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Notes",
            Description = "Short notes kept per site.",
            Version = "1.0.0",
            PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Notes",
                    Path = "notes",
                    AliasName = "*",
                    Version = "*",
                    Order = 10,
                    IsNavigation = true,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Registered, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            Title = "Notes",
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Registered, true),
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            }
                        }
                    }
                }
            }
        };
    }
}
```

`AliasName = "*"` is required: the default empty alias name matches no site and the template is skipped without a message.
Do not set `Dependencies` for a module inside the app: the app's `Shared` assembly is already part of its WebAssembly client.

### 10. Client: the pages

`Client/Modules/Notes/_Imports.razor`:

```razor
@using System.Net
@using Stomme.Application.Modules.Notes.Models
@using Stomme.Application.Modules.Notes.Services
```

The app's `Client/_Imports.razor` already brings `Microsoft.AspNetCore.Components.Web`, `Stomme.Modules`,
`Stomme.Modules.Controls`, `Stomme.Shared` and the rest to every folder below it - keep it that way.

`Client/Modules/Notes/Index.razor` - the list, the module's default view:

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase
@inject INotesService NotesService

@if (!IsAuthorizedPermission(NotesPermissions.View))
{
    <p id="notes-denied">You do not have access to the notes on this site.</p>
}
else if (_notes == null)
{
    <p class="db-muted">Loading notes...</p>
}
else
{
    @if (IsAuthorizedPermission(NotesPermissions.Edit))
    {
        <div class="db-btn-row">
            <ActionLink Action="Add" Text="Add note" Security="SecurityAccessLevel.View" />
        </div>
    }

    @if (_notes.Count == 0)
    {
        <div class="db-empty" id="notes-empty">
            <div class="db-empty-title">No notes yet</div>
            <div>@(IsAuthorizedPermission(NotesPermissions.Edit) ? "Notes you add appear here. Use Add note to write the first one." : "Notes appear here when someone adds one.")</div>
        </div>
    }
    else
    {
        <Pager Items="@_notes" SearchProperties="Title">
            <Header>
                <th>Title</th>
                <th>Last changed</th>
                <th class="db-col-action"><span class="db-visually-hidden">Actions</span></th>
            </Header>
            <Row>
                <td>@context.Title</td>
                <td>@UtcToLocal(context.UpdatedUtc)?.ToString("g")</td>
                <td class="db-col-action">
                    @if (MayChange(context))
                    {
                        <div class="db-actions">
                            <ActionLink Action="Edit" Text="Edit" Parameters="@($"id={context.Id}")" Security="SecurityAccessLevel.View" />
                            <ActionDialog Id="@($"delete-{context.Id}")" Header="Delete note" Message="@($"Delete the note '{context.Title}'? This cannot be undone.")" Action="Delete" Security="SecurityAccessLevel.View" Class="db-btn db-btn-danger" OnClick="@(async () => await DeleteAsync(context))" />
                        </div>
                    }
                </td>
            </Row>
        </Pager>
    }
}

@code {
    private List<NoteModel> _notes;

    protected override async Task OnParametersSetAsync()
    {
        if (IsAuthorizedPermission(NotesPermissions.View))
        {
            await LoadAsync();
        }
    }

    // A convenience for the UI only: the server decides again on every change.
    private bool MayChange(NoteModel note) =>
        IsAuthorizedPermission(NotesPermissions.Edit)
        && (note.AuthorUserId == PageState.User?.UserId || IsAuthorizedPermission(NotesPermissions.Manage));

    private async Task LoadAsync()
    {
        try
        {
            _notes = await NotesService.ListAsync();
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not load the notes {Error}", ex.Message);
            _notes = new List<NoteModel>();
            AddModuleMessage("The notes could not be loaded. Reload the page; if it keeps failing, ask an administrator to check the event log.", MessageType.Error);
        }
    }

    private async Task DeleteAsync(NoteModel note)
    {
        try
        {
            await NotesService.DeleteAsync(note.Id);
            await logger.LogInformation("Note deleted {NoteId}", note.Id);
            await LoadAsync();
            AddModuleMessage($"The note '{WebUtility.HtmlEncode(note.Title)}' was deleted.", MessageType.Success);
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not delete note {NoteId} {Error}", note.Id, ex.Message);
            AddModuleMessage("The note was not deleted: " + WebUtility.HtmlEncode(ex.Message), MessageType.Error);
        }

        // ActionDialog calls a plain Action, so nothing re-renders the page unless it asks to.
        StateHasChanged();
    }
}
```

`Client/Modules/Notes/Edit.razor` - one form for the `Add` and `Edit` actions:

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase
@inject INotesService NotesService
@inject NavigationManager NavigationManager

@if (_loading)
{
    <p class="db-muted">Loading...</p>
}
else
{
    <form @onsubmit="SaveAsync" @onsubmit:preventDefault novalidate>
        <div class="db-fields">
            <div class="db-field">
                <Label For="note-title" HelpText="A short name, shown in the list">Title:</Label>
                <div class="db-field-control">
                    <input id="note-title" class="db-input" @bind="_title" maxlength="120" required />
                </div>
            </div>
            <div class="db-field">
                <Label For="note-body" HelpText="The text of the note">Note:</Label>
                <div class="db-field-control">
                    <textarea id="note-body" class="db-input" @bind="_body" rows="6" maxlength="4000"></textarea>
                </div>
            </div>
        </div>
        <div class="db-form-actions">
            <button type="submit" class="db-btn" disabled="@_busy">Save</button>
            <NavLink class="db-btn db-btn-neutral" href="@NavigateUrl()">Cancel</NavLink>
        </div>
    </form>
}

@code {
    private Guid _id = Guid.Empty;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private bool _loading = true;
    private bool _busy;

    // View, not Edit: who may write is the notes:edit key, decided on the server; Edit would mean "edits the page".
    public override SecurityAccessLevel SecurityAccessLevel => SecurityAccessLevel.View;

    public override string Actions => "Add,Edit";

    public override string Title => "Note";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            if (PageState.QueryString.ContainsKey("id") && Guid.TryParse(PageState.QueryString["id"], out var id))
            {
                var note = await NotesService.GetAsync(id);
                if (note == null)
                {
                    AddModuleMessage("This note no longer exists. Go back to the list to see the current notes.", MessageType.Warning);
                }
                else
                {
                    _id = note.Id;
                    _title = note.Title;
                    _body = note.Body;
                }
            }
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not load the note {Error}", ex.Message);
            AddModuleMessage("The note could not be loaded. Close this dialog and try again.", MessageType.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_title))
        {
            AddModuleMessage("Give the note a title.", MessageType.Warning);
            return;
        }

        _busy = true;
        try
        {
            var request = new SaveNoteRequest { Title = _title.Trim(), Body = _body ?? string.Empty };
            if (_id == Guid.Empty)
            {
                await NotesService.CreateAsync(request);
            }
            else
            {
                await NotesService.EditAsync(_id, request);
            }

            await logger.LogInformation("Note saved {NoteId}", _id);
            NavigationManager.NavigateTo(NavigateUrl());
        }
        catch (Exception ex)
        {
            await logger.LogError(ex, "Could not save the note {Error}", ex.Message);
            AddModuleMessage("The note was not saved: " + WebUtility.HtmlEncode(ex.Message), MessageType.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
```

Nothing else is registered anywhere: the host finds `NotesServerStartup`, `NotesClientStartup`, `NotesController` and
`ModuleInfo` by convention.

## Verify

1. `dotnet build` at the app root: no errors.
2. `cd Server` and `dotnet run`; install if asked (see `stomme-run-and-install`). The console must show no `fail:` line
   about the Notes module. Page templates are applied when the host starts: on a fresh installation `/notes` exists after
   the install; on an installation that existed before the module, it exists after the next start.
3. Anonymous: `GET /api/Notes` answers **403** (401 is also a refusal), never 404 (404 means the controller was not
   discovered). Opening `/notes` signed out sends you to the login page: the page itself is for Registered Users.
4. Sign in as the host. The navigation shows **Notes**; `/notes` shows "No notes yet" and **Add note**. Add a note: the
   dialog headed "Note" opens; save it and the list shows it with **Edit** and **Delete**. Edit it, then delete it through
   the confirmation; the list follows each time. If nothing happens on a click, events are dead: see Common mistakes.
5. Reload the page: the notes are still there (they were stored). Adding a second note whose title differs only in case is
   refused with "A note titled '...' already exists on this site."
6. In **Admin > Module Management** the module is listed as Notes.
7. Sign in as a user who is only a Registered User: the list shows, **Add note** shows, and **Edit**/**Delete** show only on
   that user's own notes. Over HTTP, that user's `PUT` or `DELETE` of someone else's note answers 403.
8. Check the browser console: no errors. Check the page narrow and in dark mode, and run `stomme-ux-review`.
9. If any of your sites uses the WebAssembly runtime, repeat step 4 on such a site with the console open. It works: the
   page calls `api/Notes` through `NotesService` instead of the mediator.

## Common mistakes

- **The page shows nothing and Admin > Module Management lists a module named after the folder.** A component's
  namespace differs from `ModuleInfo`'s. Every `.razor` file needs `@namespace Stomme.Application.Modules.Notes`.
- **`/notes` never appears.** `AliasName = "*"` is missing from the page template, or the app has not been restarted since the
  module was added, or `module.notes.enabled` is `false` in `Server/features.json`.
- **Buttons do nothing, no console error.** `Microsoft.AspNetCore.Components.Web` is not imported for the component. It comes
  from `Client/_Imports.razor`; do not remove it, and a component in a folder outside `Client` does not get it.
- **`GET /api/Notes` answers 404.** The controller is not discovered (an assembly name without `Stomme`) or the route is wrong:
  keep `[Route(ControllerRoutes.ApiRoute)]` and a class name ending in `Controller`.
- **"No service for type INotesService".** `NotesClientStartup` or `NotesServerStartup` is missing, not `public`, or has a
  constructor with parameters (the host creates startups with a parameterless constructor).
- **"No handler registered for ..." (a 500).** A handler was not registered in `NotesServerStartup`, is registered under a
  different response type than the request declares, or is not `public`.
- **Every request is refused (403) even for the host.** The key in `[Requires]`/`[RequiresPermissionKey]` is not declared in
  `ModuleInfo.PermissionNames` (letter case does not matter); a key no module declares denies everyone.
- **A Registered User sees no Add note although they hold `notes:edit`.** An `ActionLink` or the `Edit` component uses
  `SecurityAccessLevel.Edit`, which means "may edit this page", not "holds the module's key". Keep `Security="SecurityAccessLevel.View"`
  on the links and `SecurityAccessLevel.View` on `Edit.razor`, and decide with `IsAuthorizedPermission(...)` and on the server.
- **Notes of one site show on another.** A query forgot `note.SiteId == siteId`. The store separates tenants, not sites.
- **The add fails with "Notes belong to a tenant; none is resolved".** The command ran outside a request (a job); set the
  tenant and site first, see [Jobs](../../../docs/services/jobs.md).
- **The app does not start after adding a constructor dependency.** The host validates its container at startup; register
  every service your handlers and services take.
