#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Notifications.Persistence;
using Stomme.Portability;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// A person's notification preferences on a site, for their data package (BL-82 over BL-78): the choices they made
    /// - severity filter, quiet hours, digest, preferred language.
    /// </summary>
    public sealed class NotificationPreferenceSubjectContributor(INotificationPreferenceRepository preferences) : ISubjectDataContributor
    {
        public const string Type = "notification-preference";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            var preference = await preferences.GetByUserAsync(scope.SiteId, scope.UserId, cancellationToken);
            return preference is null
                ? new List<PortableEntity>()
                : new List<PortableEntity>
                {
                    new(new PortableEntityRef(Type, preference.Id.ToString("N", CultureInfo.InvariantCulture)), "Notification preferences", 1,
                        JsonSerializer.Serialize(new
                        {
                            severityFilter = preference.SeverityFilter.ToString(),
                            quietHours = preference.QuietHours?.Value,
                            digest = preference.Digest.ToString(),
                            preferredLocale = preference.PreferredLocale,
                        })),
                };
        }
    }
}