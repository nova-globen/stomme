#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Application
{
    // Lifted from the predecessor platform NotificationTemplateQueries.cs. CHANGED: every template read - and the PREVIEW -
    // requires ManageTemplates, never a view key. the predecessor platform gated preview on notifications:view, which let any viewer run
    // arbitrary Liquid over arbitrary parameters against the site's templates.

    /// <summary>Lists the current site's templates, by key.</summary>
    [Requires(NotificationsPermissions.ManageTemplates)]
    public sealed record ListNotificationTemplatesQuery : IQuery<IReadOnlyList<NotificationTemplateSummary>>;

    public sealed class ListNotificationTemplatesQueryHandler(INotificationTemplateRepository templates, SiteState siteState)
        : IQueryHandler<ListNotificationTemplatesQuery, IReadOnlyList<NotificationTemplateSummary>>
    {
        public async Task<IReadOnlyList<NotificationTemplateSummary>> Handle(ListNotificationTemplatesQuery request, CancellationToken ct) =>
            (await templates.ListBySiteAsync(NotificationWire.RequireSiteId(siteState), ct))
                .OrderBy(template => template.Key, StringComparer.OrdinalIgnoreCase)
                .Select(NotificationWire.ToSummary)
                .ToList();
    }

    /// <summary>One template's detail for the editor, or null when the site has no such template.</summary>
    [Requires(NotificationsPermissions.ManageTemplates)]
    public sealed record GetNotificationTemplateQuery(Guid Id) : IQuery<NotificationTemplateDetail?>;

    public sealed class GetNotificationTemplateQueryHandler(INotificationTemplateRepository templates, SiteState siteState)
        : IQueryHandler<GetNotificationTemplateQuery, NotificationTemplateDetail?>
    {
        public async Task<NotificationTemplateDetail?> Handle(GetNotificationTemplateQuery request, CancellationToken ct)
        {
            var template = await templates.GetByIdAsync(request.Id, ct);
            return template is null || template.SiteId != NotificationWire.RequireSiteId(siteState) ? null : NotificationWire.ToDetail(template);
        }
    }

    /// <summary>
    /// Renders a template's locale with sample parameters, HTML-encoded (the Preview target) under the renderer's per-render
    /// budget. Falls back to the template's first locale when the requested one has no content. A render failure is the
    /// preview's <c>Error</c>, not an exception.
    /// </summary>
    [Requires(NotificationsPermissions.ManageTemplates)]
    public sealed record PreviewNotificationTemplateQuery(Guid Id, string LocaleCode, IReadOnlyList<DispatchParameter>? Parameters)
        : IQuery<NotificationTemplatePreview>;

    public sealed class PreviewNotificationTemplateQueryHandler(
        INotificationTemplateRepository templates, NotificationTemplateRenderer renderer, SiteState siteState)
        : IQueryHandler<PreviewNotificationTemplateQuery, NotificationTemplatePreview>
    {
        public async Task<NotificationTemplatePreview> Handle(PreviewNotificationTemplateQuery request, CancellationToken ct)
        {
            var template = await templates.GetByIdAsync(request.Id, ct);
            if (template is null || template.SiteId != NotificationWire.RequireSiteId(siteState))
            {
                throw new InvalidOperationException($"Template '{request.Id}' does not exist.");
            }

            var localized = template.Contents.FirstOrDefault(
                    content => string.Equals(content.Locale.Code, request.LocaleCode, StringComparison.OrdinalIgnoreCase))
                ?? template.Contents[0];

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in request.Parameters ?? Array.Empty<DispatchParameter>())
            {
                if (parameter is not null && !string.IsNullOrWhiteSpace(parameter.Name))
                {
                    values[parameter.Name.Trim()] = parameter.Value;
                }
            }

            using var budget = NotificationTemplateRenderer.CreateRenderCancellation(ct);
            var result = await renderer.RenderAsync(localized.Content, values, NotificationRenderTarget.Preview, budget.Token);
            return result.Succeeded
                ? new NotificationTemplatePreview(localized.Locale.Code, result.Payload!.Subject, result.Payload.Body, null)
                : new NotificationTemplatePreview(localized.Locale.Code, null, null, result.Error);
        }
    }
}