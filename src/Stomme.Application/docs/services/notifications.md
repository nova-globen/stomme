# Notifications

Nova Globen Stomme's Notifications module tells people things: in the app (a notification center and the user's inbox), by e-mail,
or by an HTTP POST to a configured webhook. Any module sends one through a single port, `INotificationDispatcher`; the
module renders a per-site template, honours each recipient's preferences (quiet hours, digest, severity, muted topics)
and delivers from a background job, with retries and a send log.

## How it works

The types an app uses:

| Type | Namespace | Assembly (package) | What it is |
|---|---|---|---|
| `INotificationDispatcher` | `Stomme.Notifications` | `Stomme.Shared` | The producer port: `Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)`. |
| `NotificationDispatchRequest` | `Stomme.Models.Notifications` | `Stomme.Shared` | `record (string TemplateKey, IReadOnlyList<DispatchRecipient> Recipients, IReadOnlyList<NotificationChannelKind> Channels, IReadOnlyList<DispatchParameter>? Parameters = null, NotificationSeverity Severity = Information, string? Topic = null)`. |
| `DispatchRecipient`, `DispatchParameter` | `Stomme.Models.Notifications` | `Stomme.Shared` | `record (NotificationRecipientKind Kind, string Value)`; `record (string Name, string Value)`. |
| `NotificationChannelKind` | `Stomme.Models.Notifications` | `Stomme.Shared` | `Email`, `InApp`, `Webhook`, `Sms` (no SMS transport ships; a request for it is not delivered). |
| `NotificationRecipientKind` | `Stomme.Models.Notifications` | `Stomme.Shared` | `User` (a user id), `Email` (an address), `Endpoint` (always refused). |
| `NotificationSeverity` | `Stomme.Models.Notifications` | `Stomme.Shared` | `Information`, `Warning`, `Critical` (Critical ignores quiet hours). |
| `INotificationTemplateSource` | `Stomme.Modules.Notifications.Application.Templates` | `Stomme.Server` | How a module contributes its templates to every site. |
| `NotificationTemplateAggregate`, `ChannelKind`, `Locale`, `Content`, `LocalizedContent`, `PlaceholderSchema` | `Stomme.Modules.Notifications.Domain` | `Stomme.Server` | The template a source creates. |

The flow of one notification:

1. Server code calls `DispatchAsync`. The dispatcher takes the site from the scope's resolved alias (`SiteState.Alias`)
   and the tenant from `ITenantContext` - never from the request. A scope with no site (a job that has not set an
   alias) is refused.
2. It checks the request and each recipient: a `User` recipient must be a positive user id that holds at least one role
   on this site; an `Email` recipient is accepted only when the site setting `NotificationExternalRecipients` is `True`
   and the value is exactly one address; `Endpoint` is always refused. A refusal throws
   `NotificationDispatchRefusedException`, which derives from `InvalidOperationException`, persists nothing, and its
   message never contains the address.
3. It stores the request as Queued with its parameters and returns the request id. Nothing is delivered yet. When the
   message bus between instances is switched on (`Messaging:Enabled`), it also sends a hint so delivery starts sooner.
4. The **Notification Dispatch Job** runs every minute, per tenant and per site. For each due request it loads the
   site's template by key, applies each recipient's preferences, renders the template once per channel kind, and hands
   the result to every **enabled channel** of each requested kind on that site.
5. Each delivery is recorded as an attempt on the request. A failed attempt is retried later with a growing delay; after
   the last retry the request completes as Failed or PartiallyFailed. The site's send log (`admin/notifications`) shows
   requests and attempts and can retry one.

The channels:

- **In-app** writes a delivered row to the user's inbox (the `notifications` page and the inbox on the user's profile).
  With `Transport:SignalR:Enabled` it is also pushed live to a WebAssembly browser.
- **E-mail** writes an undelivered row to the site's mail queue; the attempt is then `Succeeded`, though nothing has been
  sent. The separate **Notification Job** sends the queue over SMTP with the site's SMTP settings (Admin > Site
  Settings). Mail older than 24 hours in the queue is not sent. The queued row is also listed in the user's inbox, so a
  request for both In-app and E-mail shows the notice there twice.
- **Webhook** POSTs a JSON envelope to the channel's endpoint, with an optional Bearer credential from the site's secret
  store. The connection refuses loopback, private, link-local, unspecified and multicast addresses unless the host is in
  the site setting `NotificationWebhookAllowedHosts`; redirects are not followed.

Templates are per site and keyed. Each has one or more localized contents (subject and body) and a declared list of
placeholders. Contents use Liquid syntax (`{{ name }}`), rendered by Fluid with a step limit and a per-render time
budget; for e-mail and in-app the values in the body are HTML-encoded (the subject is text and is not). The
recipient's preferred locale is chosen first, then an English content, then the first one. The module ships one template, `generic.message` (placeholders `subject` and
`body`). Every other module brings its own through an `INotificationTemplateSource`: on every host start, for every site,
a template whose key the site does not have yet is created; an existing one is never touched, so an administrator's edit
survives upgrades.

Per-user preferences (the user's Preferences page, reached from `notifications`), stored per site:

- **Accepted channels** - empty means all.
- **Severity filter** - `All`, `ErrorsOnly` (Warning and Critical), `CriticalOnly`, `None`.
- **Muted topics** - a request whose `Topic` the user muted is not delivered to them.
- **Quiet hours** - a daily window with a fixed UTC offset; a non-Critical request is deferred to the end of the window
  when every reachable recipient is in quiet hours.
- **Digest** - held and delivered with the next hourly pass.
- **Preferred locale** - picks the template content.

## Using it from an app

Send from server code only - a command handler, a domain service, a job. The dispatcher has no implementation in the
browser; a component asks its module's server through its service, and the server sends.

A module's template source (`Server/Modules/Notes/Notifications/NoteNotifications.cs`):

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Notifications
{
    public static class NoteNotifications
    {
        public const string NoteEditedKey = "notes.note-edited";

        public const string Topic = "notes";

        public static IReadOnlyList<NotificationTemplateAggregate> Templates(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, NoteEditedKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Your note was changed: {{ title }}",
                            "{{ editedBy }} changed your note \"{{ title }}\". Open it at {{ link }}.")),
                    },
                    placeholders: new PlaceholderSchema(new[] { "title", "editedBy", "link" })),
            };
        }
    }

    public sealed class NoteTemplateSource : INotificationTemplateSource
    {
        public string Name => "Notes templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            NoteNotifications.Templates(tenantId, siteId);
    }
}
```

Registered in the module's `IServerStartup.ConfigureServices` (`using Microsoft.Extensions.DependencyInjection.Extensions;`):

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationTemplateSource, NoteTemplateSource>());
```

Sending - inject `INotificationDispatcher` and call it after your own change is saved (here, telling a note's author
that someone else changed it):

```csharp
try
{
    await dispatcher.DispatchAsync(new NotificationDispatchRequest(
        NoteNotifications.NoteEditedKey,
        new[] { new DispatchRecipient(NotificationRecipientKind.User, note.AuthorUserId.ToString(CultureInfo.InvariantCulture)) },
        new[] { NotificationChannelKind.InApp, NotificationChannelKind.Email },
        new[]
        {
            new DispatchParameter("title", note.Title),
            new DispatchParameter("editedBy", currentUser.UserName ?? "Someone"),
            new DispatchParameter("link", "/notes"),
        },
        NotificationSeverity.Information,
        NoteNotifications.Topic), ct);
}
catch (InvalidOperationException ex)
{
    logger.LogWarning(ex, "The note was changed, but its author {UserId} could not be notified.", note.AuthorUserId);
}
```

The complete module code, with registration and verification, is the
[stomme-send-notification](../../.claude/skills/stomme-send-notification/SKILL.md) skill.

To notify a site's operators, collect their user ids: `IUserRoleRepository` (`Stomme.Repository`) has
`GetUserRolesAsync(string roleName, int siteId)`; take `RoleNames.Admin` and `RoleNames.Host` (`Stomme.Shared`). A fresh
site's only user is the host account, which holds no Administrators role on the site - Administrators alone can be nobody.

## Configuration

| Setting | Where | Default | Effect |
|---|---|---|---|
| `notifications.dispatch` | Feature flag (Admin > Feature Flags, or `FeatureManagement` in configuration) | on | The dispatch job delivers queued requests. Off, requests stay queued. |
| `notifications.email` | Feature flag | on | The Notification Job sends queued e-mail. |
| Notification Job | Admin > Scheduled Jobs | created disabled | Sends the e-mail queue. Saving SMTP settings with SMTP enabled in Admin > Site Settings switches it on. |
| SMTP settings | Admin > Site Settings | none | Host, port, sender and authentication for the e-mail queue. |
| `NotificationExternalRecipients` | Site setting; Admin > Module Management > Notifications > Configure | `False` | Allows `Email` recipients (literal addresses). |
| `NotificationRequestRetention` | Site setting; same Configure tab | 30 days | Completed requests older than this are purged by the hourly pass. |
| `NotificationWebhookAllowedHosts` | Site setting; same Configure tab | empty | Host names or IP literals a webhook channel may reach even when they resolve to a private address. |
| `Messaging:Enabled` | Configuration | `false` | With the bus on, a dispatch also sends a hint so delivery does not wait for the next minute. |
| `Transport:SignalR:Enabled` | Configuration | `false` | In-app notifications are also pushed live to WebAssembly browsers. |

Channels are configured per site at `admin/notifications` > Channels (permission key `notifications:manage-channels`):
a kind (E-mail, In-app, Webhook), a name, transport settings and, for a webhook, a write-only credential.

The permission keys (`Stomme.Notifications.NotificationsPermissions`): `notifications:view-own` (Registered Users and
Administrators), `notifications:view-log`, `notifications:send`, `notifications:manage-channels`,
`notifications:manage-templates` (Administrators). They gate the pages and the `api/Notifications` endpoints, not the
dispatcher: server code may always dispatch.

## Rules and pitfalls

- **The mail server is a site setting, never configuration.** No configuration key sets SMTP: on an unattended host
  (a container, a compose file) set it per site in **Admin > Site Settings > SMTP Settings**, or have a module of yours
  write the site's settings at start-up through `ISettingRepository` (entity `EntityNames.Site`, the site's id):
  `SMTPEnabled`, `SMTPHost`, `SMTPPort`, `SMTPSSL`, `SMTPSender`, and `SMTPAuthentication` with `SMTPUsername` and
  `SMTPPassword` (a private setting, as the admin page saves it) when the relay needs a sign-in. A relay on the host's network that accepts the host is the usual shape. The health job
  connects to each site's mail server to probe it; for a relay that counts connections, set
  `Health:MailServerProbeIntervalMinutes` ([logging, health and metering](logging-health-metering.md)).
- **E-mail and in-app channels are created on first use; webhook and SMS are not.** Since 10.73.2 a site that has no
  channel of the kind a notification asks for gets a default one, "E-mail" or "In-app", at its first notification (neither
  needs settings: e-mail goes through the site's mail queue and SMTP settings). A channel an administrator disabled stays
  disabled, and then the attempt fails with "No enabled channel is configured for InApp" (or Email) and is retried. A
  webhook or SMS channel must be configured at `admin/notifications` > Channels. Before 10.73.2 a new site had no channel
  at all and every attempt failed until one was created.
- **A dispatch returns before anything is delivered.** The returned id is the send-log handle. Do not wait for
  delivery in a request; look at the send log when verifying.
- **Templates arrive at host start.** The template reconciliation runs on the boot path, per site. A template you just
  added reaches existing sites at the next restart; a site created while the host runs gets it at the following
  restart. A request whose key the site lacks fails with "No template found for key ...".
- **An operator's edit wins.** Changing your template's text in code does not change a site that already has the key.
  Ship a new key when the wording must change everywhere.
- **Recipients are users of this site, by id.** A user id with no role on the site is refused. Do not send to an e-mail
  address taken from configuration or a form: the site must opt in to external recipients, and it is an address
  outside Stomme's user model.
- **Catch the refusal.** `DispatchAsync` throws `InvalidOperationException` for a refused request. Treat a notification
  as best-effort: catch it, log it, and keep your own change. Do not dispatch from inside a domain-event handler that
  runs during a save; dispatch after the save, in the handler that made the change.
- **In a job, set the site first.** A dispatch from a background job needs the site's alias set in its scope, or it is
  refused ("no site is resolved in this scope"). See [jobs](jobs.md).
- **Bodies are HTML-encoded.** A parameter value with markup appears as text in e-mail and in the inbox. Never put a
  secret, a token or a password-reset link in a parameter: requests and their parameters are stored with the
  request.
- **Quiet hours use a fixed offset.** Daylight-saving changes are not followed.
- **Erasure is already handled.** When a user is deleted from a site, their preferences and their inbox rows are
  deleted and the requests addressed to them are anonymised (see [data rights](data-rights.md)).

## Related

- [stomme-send-notification](../../.claude/skills/stomme-send-notification/SKILL.md) - send one from a module, end to end.
- [Background jobs](jobs.md) - sending from a job, per site.
- [Events and messaging](events-and-messaging.md) - the message bus the dispatcher uses when it is on.
- [Identity and permissions](../security/identity-and-permissions.md) - roles and permission keys.
- [Data rights](data-rights.md) - what happens to notifications when a user is erased.
- [Configuration](../architecture/configuration.md) - feature flags and switches.
