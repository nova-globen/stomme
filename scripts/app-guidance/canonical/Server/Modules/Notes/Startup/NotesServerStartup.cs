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
