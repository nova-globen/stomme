#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Notifications.Application
{
    // Lifted from the predecessor platform NotificationTemplateCommands.cs (BL-78 step 5), site-scoped: the key is unique per SITE through
    // KeyNormalized (D49, both sides normalized), a template of another site is "does not exist", and the save runs the
    // hardened renderer's validate-at-save (the step-3 residue: EnsureValidForSave had no caller until now).

    /// <summary>
    /// Creates a template (null or unknown <see cref="Id"/>) or replaces an existing one's channels, localized content and
    /// placeholder schema. Every placeholder a content references must be declared in <see cref="Placeholders"/>.
    /// </summary>
    [Requires(NotificationsPermissions.ManageTemplates)]
    public sealed record SaveNotificationTemplateCommand(
        Guid? Id,
        string Key,
        IReadOnlyList<NotificationChannelKind> SupportedChannels,
        IReadOnlyList<NotificationTemplateContentInput> Contents,
        IReadOnlyList<string> Placeholders) : ICommand<Guid>;

    public sealed class SaveNotificationTemplateCommandValidator : AbstractValidator<SaveNotificationTemplateCommand>
    {
        public SaveNotificationTemplateCommandValidator()
        {
            RuleFor(command => command.Key).NotEmpty().MaximumLength(120);
            RuleFor(command => command.SupportedChannels).NotEmpty();
            RuleFor(command => command.Contents).NotEmpty();
        }
    }

    public sealed class SaveNotificationTemplateCommandHandler(
        INotificationTemplateRepository templates,
        NotificationTemplateRenderer renderer,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState)
        : ICommandHandler<SaveNotificationTemplateCommand, Guid>
    {
        public async Task<Guid> Handle(SaveNotificationTemplateCommand request, CancellationToken ct)
        {
            var siteId = NotificationWire.RequireSiteId(siteState);
            var key = request.Key.Trim();
            var schema = new PlaceholderSchema(request.Placeholders ?? Array.Empty<string>());
            var contents = (request.Contents ?? Array.Empty<NotificationTemplateContentInput>())
                .Select(content => new LocalizedContent(new Locale(content.LocaleCode), new Content(content.Subject, content.Body)))
                .ToList();
            var channels = NotificationWire.ToDomain(request.SupportedChannels);

            var validationErrors = renderer.Validate(contents, schema);
            if (validationErrors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", validationErrors));
            }

            var byKey = await templates.GetByKeyAsync(siteId, key, ct);

            NotificationTemplateAggregate? existing = null;
            if (request.Id is { } id)
            {
                existing = await templates.GetByIdAsync(id, ct);
                if (existing is not null && existing.SiteId != siteId)
                {
                    throw new InvalidOperationException($"Template '{id}' does not exist.");
                }
            }

            if (existing is not null)
            {
                if (!string.Equals(existing.KeyNormalized, NotificationTemplateAggregate.NormalizeKey(key), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A template's key cannot be changed.");
                }

                if (byKey is not null && byKey.Id != existing.Id)
                {
                    throw new InvalidOperationException($"A template with key '{key}' already exists.");
                }

                existing.Update(channels, contents, schema);
                renderer.EnsureValidForSave(existing);
                await templates.UpdateAsync(existing, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return existing.Id;
            }

            if (byKey is not null)
            {
                throw new InvalidOperationException($"A template with key '{key}' already exists.");
            }

            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved in this scope.");
            var template = NotificationTemplateAggregate.Create(request.Id ?? Guid.NewGuid(), tenantId, siteId, key, channels, contents, schema);
            renderer.EnsureValidForSave(template);
            await templates.AddAsync(template, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return template.Id;
        }
    }

    /// <summary>Deletes a template of the current site, refusing when any of the site's requests was sent against its key.</summary>
    [Requires(NotificationsPermissions.ManageTemplates)]
    public sealed record DeleteNotificationTemplateCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteNotificationTemplateCommandHandler(
        INotificationTemplateRepository templates,
        INotificationRequestRepository requests,
        IUnitOfWork unitOfWork,
        SiteState siteState)
        : ICommandHandler<DeleteNotificationTemplateCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteNotificationTemplateCommand request, CancellationToken ct)
        {
            var siteId = NotificationWire.RequireSiteId(siteState);
            var template = await templates.GetByIdAsync(request.Id, ct);
            if (template is null || template.SiteId != siteId)
            {
                throw new InvalidOperationException($"Template '{request.Id}' does not exist.");
            }

            // A FindAsync on two FindAsync-safe terms: an int and a D49 normalized column, the argument normalized in code.
            var normalized = template.KeyNormalized;
            var referenced = await requests.FindAsync(item => item.SiteId == siteId && item.TemplateKeyNormalized == normalized, ct);
            if (referenced.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Template '{template.Key}' is referenced by one or more notification requests and cannot be deleted.");
            }

            await templates.DeleteAsync(template, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }
}