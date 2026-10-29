#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The ONE template this module contributes to the per-site notification template reconciliation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One key, not three.</b> Separate <c>health.dependency.unhealthy</c>, <c>…degraded</c> and
    /// <c>…recovered</c> templates would make the state part of the template IDENTITY — so an operator who wanted to
    /// reword "a dependency is in trouble" had to edit three templates and keep them consistent, and adding a
    /// fourth state would have meant a fourth template. Here the state is a PARAMETER, so one template says it
    /// once and the catalogue does not grow with the state machine.
    /// </para>
    /// <para>
    /// <b>The body never interpolates the exception message.</b> There is no placeholder for it, so a template a
    /// site owner edits cannot accidentally start mailing out the database host and user — the field simply is
    /// not available to them. That is a stronger guarantee than remembering not to pass it.
    /// </para>
    /// </remarks>
    public static class HealthTemplateCatalogue
    {
        public const string AlertKey = "health.alert";

        public static IReadOnlyList<NotificationTemplateAggregate> All(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);

            var placeholders = new PlaceholderSchema(new[]
            {
                "subject", "dependency", "state", "previousState", "scope", "exceptionType", "latencyMs", "sinceMinutes"
            });

            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, AlertKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "{{ dependency }} is {{ state }}",
                            "{{ dependency }} ({{ scope }} scope) went from {{ previousState }} to {{ state }} " +
                            "after {{ sinceMinutes }} minute(s). Last check took {{ latencyMs }} ms" +
                            "{{ exceptionType }}. Open the health page for the detail."))
                    },
                    placeholders: placeholders)
            };
        }
    }

    /// <summary>Contributes <see cref="HealthTemplateCatalogue"/> to the per-site template reconciliation.</summary>
    public sealed class HealthTemplateSource : INotificationTemplateSource
    {
        public string Name => "Health alert template";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            HealthTemplateCatalogue.All(tenantId, siteId);
    }
}
