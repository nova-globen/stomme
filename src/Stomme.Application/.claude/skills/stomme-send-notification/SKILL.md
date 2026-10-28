---
name: stomme-send-notification
description: Use when a module must tell someone something - send an in-app notification, an e-mail or a webhook call, notify a user or the site's administrators when something happens, add a notification template, or when "notify", "alert the user", "send an e-mail" or "INotificationDispatcher" comes up.
---

# Send a notification from a module

You will have a module that contributes its own notification template to every site and sends it to a site user through
Nova Globen Stomme's `INotificationDispatcher`, delivered in the app and by e-mail, honouring the user's preferences. Read
[notifications](../../../docs/services/notifications.md) for how the pipeline works. Do not build your own e-mail
sending, inbox or notification table.

The example extends the Notes module that the `stomme-create-module` skill builds: when someone other than a note's
author changes the note (a holder of `notes:manage`), the author is told.

## Steps

1. **The template.** In the `Server` project, create `Server/Modules/Notes/Notifications/NoteNotifications.cs`. The key is
   unique per site - prefix it with your module. Every `{{ name }}` in the content must be in the placeholder list.

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

    /// <summary>Gives every site the module's templates; Stomme creates a missing one at each host start.</summary>
    public sealed class NoteTemplateSource : INotificationTemplateSource
    {
        public string Name => "Notes templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            NoteNotifications.Templates(tenantId, siteId);
    }
}
```

2. **The sender.** Create `Server/Modules/Notes/Notifications/NoteEditedNotifier.cs`. It is best-effort: a refused
   dispatch is logged and never undoes the change that caused it.

```csharp
#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Notifications;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Modules.Notes.Notifications
{
    public sealed class NoteEditedNotifier(INotificationDispatcher dispatcher, ILogger<NoteEditedNotifier> logger)
    {
        /// <summary>Queues the notice for <paramref name="authorUserId"/>; returns the send-log id, or null when it was refused.</summary>
        public async Task<Guid?> NotifyAsync(int authorUserId, string title, string editedByName, CancellationToken ct = default)
        {
            try
            {
                return await dispatcher.DispatchAsync(new NotificationDispatchRequest(
                    NoteNotifications.NoteEditedKey,
                    new[] { new DispatchRecipient(NotificationRecipientKind.User, authorUserId.ToString(CultureInfo.InvariantCulture)) },
                    new[] { NotificationChannelKind.InApp, NotificationChannelKind.Email },
                    new[]
                    {
                        new DispatchParameter("title", title),
                        new DispatchParameter("editedBy", editedByName),
                        new DispatchParameter("link", "/notes"),
                    },
                    NotificationSeverity.Information,
                    NoteNotifications.Topic), ct);
            }
            catch (InvalidOperationException ex)
            {
                // A refusal: the user is not a user of this site, no site is resolved in this scope, or the request is invalid.
                logger.LogWarning(ex, "The note was changed, but its author {UserId} could not be notified.", authorUserId);
                return null;
            }
        }
    }
}
```

3. **Register both.** In the module's server startup, `NotesServerStartup.ConfigureServices`
   (`Server/Modules/Notes/Startup/NotesServerStartup.cs`), add these two lines before the last registration
   (`AddTransient<INotesService, ServerNotesService>()` stays last), with
   `using Stomme.Application.Modules.Notes.Notifications;` and
   `using Stomme.Modules.Notifications.Application.Templates;` at the top
   (`Microsoft.Extensions.DependencyInjection.Extensions` is already there):

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationTemplateSource, NoteTemplateSource>());
services.AddScoped<NoteEditedNotifier>();
```

4. **Call it** from the server code that makes the change, after `IUnitOfWork.SaveChangesAsync` has succeeded. In
   `Server/Modules/Notes/Application/NoteRequests.cs`, add `using Stomme.Application.Modules.Notes.Notifications;`, add a
   last constructor parameter `NoteEditedNotifier notifier` to `EditNoteCommandHandler`, and end its `Handle` like this:

```csharp
            await unitOfWork.SaveChangesAsync(ct);
            if (currentUser.UserId != note.AuthorUserId)
            {
                await notifier.NotifyAsync(note.AuthorUserId, note.Title, currentUser.UserName ?? "Someone", ct);
            }

            return NotesScope.ToModel(note);
```

   Never call it from a Razor component (the dispatcher exists only on the server) and never from a domain-event handler
   that runs inside the save. From a background job, run it in a per-site scope with the site's alias set
   ([jobs](../../../docs/services/jobs.md)).

5. **Restart the app.** The template reaches each existing site at the next host start.

6. **Configure the channels on each site** (once, as an administrator): open `admin/notifications`, then Channels, and add
   an **In-app** channel and an **E-mail** channel (a new channel is enabled). No channel exists on a new site: without a
   channel of a kind, every delivery of that kind fails and is retried. For e-mail to leave the machine, also set SMTP
   in Admin > Site Settings with SMTP enabled; saving there switches the Notification Job on.

## Verify

1. Build, run, sign in as the host. Open `admin/notifications` > Templates: `notes.note-edited` is listed.
2. Create a second user (Admin > User Management, or `POST api/User` as the host); a user created on the site holds the
   Registered Users role there.
3. Sign in as that user and write a note. Sign in as the host and change that note.
4. Within about a minute, `admin/notifications` > Send log shows the request, both attempts `Succeeded` and the request
   `Sent`; Channels now lists the default "E-mail" and "In-app" channels the first notification created (since 10.73.2;
   before, it was `Failed` until an administrator created them). An E-mail attempt `Succeeded`
   means the mail was put in the site's mail queue, not that it was sent - with SMTP not configured nothing leaves.
5. Sign in as the note's author and open `notifications`: the inbox holds "Your note was changed: ..." with the editor's
   username in the body. (Over HTTP: `GET api/Notification?siteid=1&direction=to&userid={id}`.)
6. As that user, open Preferences, mute the topic `notes`, have the host change the note again: the new request shows
   both attempts `Skipped` - "Suppressed by recipient notification preferences." - and nothing new reaches the inbox.
7. The author changing their own note sends nothing (the handler skips it); nothing appears in the send log.

## Common mistakes

- **Send log shows "No enabled channel is configured for InApp".** The site's channel of that kind is disabled (since
  10.73.2 a missing e-mail or in-app channel is created on first use; on an older Stomme, none existed). Enable it at
  `admin/notifications` > Channels; the pending request is retried and delivered on a later pass.
- **The notice shows twice in the inbox.** The inbox lists every row addressed to the user, and with both channels
  requested the queued e-mail row is listed beside the in-app row. Request only `InApp` when e-mail adds nothing.
- **Log shows "No template found for key ...".** The app was not restarted after the source was added, the source is
  not registered, or the site was created after the last start. Restart.
- **Your log shows "no site is resolved in this scope".** The dispatch ran in a scope without the site's alias - a job
  or a `Task.Run`. Set the alias for the site in the job's per-site scope.
- **Refused "user N is not a user of site M".** The recipient holds no role on that site. Send only to site users; for
  the site's operators use `IUserRoleRepository.GetUserRolesAsync(RoleNames.Admin, siteId)` and
  `GetUserRolesAsync(RoleNames.Host, siteId)`.
- **Changed the wording in code, nothing changed on the site.** An existing template is never overwritten (an
  administrator may have edited it). Edit it at `admin/notifications` > Templates, or ship a new key.
- **E-mail attempts succeed but no mail arrives.** The attempt only queues the mail. The Notification Job (Admin >
  Scheduled Jobs) sends the queue; it is created disabled and SMTP must be configured. Its log shows the reason.
- **Markup shows as text.** Parameters in a body are HTML-encoded for e-mail and in-app. Send plain text.
- **A secret in a parameter.** Parameters are stored with the request; never pass a token or a reset link.
