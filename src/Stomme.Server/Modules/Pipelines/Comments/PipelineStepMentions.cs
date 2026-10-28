#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Pipelines.Application;
using Stomme.Notifications;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Comments
{
    /// <summary>
    /// BL-229 - @mentions in a step's discussion: a comment that names someone after an @ tells them, in the app and by e-mail
    /// (each as their own notification preferences allow), with the comment and a link to the step. Only someone on the site
    /// who can SEE pipelines is ever told - a name that is anyone else is said back to the author as told nobody, never as
    /// "no such user" - and never the author themself.
    /// </summary>
    public static class PipelineStepMentionNames
    {
        // An @ that starts a word, then a user name: letters, digits, '.', '_', '-', and an e-mail-shaped name's own @.
        private static readonly Regex Mention = new(@"(?<![\w@.])@(?<name>[\w.\-]+(?:@[\w\-]+(?:\.[\w\-]+)+)?)", RegexOptions.CultureInvariant);

        /// <summary>The names a text mentions, each once (ignoring case), in the order first written; a sentence's full stop is not part of one.</summary>
        public static IReadOnlyList<string> Of(string? text)
        {
            var names = new List<string>();
            foreach (Match match in Mention.Matches(text ?? string.Empty))
            {
                var name = match.Groups["name"].Value.TrimEnd('.', '-');
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>Who may be mentioned on a site, and the telling of those a comment names.</summary>
    public sealed class PipelineMentionNotifier(
        IUserRoleRepository userRoles, IStoredRowPermissionEvaluator permissions, IPageModuleRepository pageModules, IServiceProvider services,
        ILogger<PipelineMentionNotifier> logger)
    {
        public const string Topic = "pipelines.comments";

        /// <summary>The site's people who can see its pipelines, by display name - those a comment may mention.</summary>
        public async Task<IReadOnlyList<(int UserId, PipelineMentionView View)>> PeopleAsync(int siteId, CancellationToken ct)
        {
            var people = new List<(int, PipelineMentionView)>();
            var users = (await userRoles.GetUserRolesAsync(siteId))
                .Where(role => role.User is { IsDeleted: false } && !string.IsNullOrWhiteSpace(role.User.Username))
                .Select(role => role.User)
                .GroupBy(user => user.UserId)
                .Select(group => group.First());
            foreach (var user in users)
            {
                if (await permissions.IsAuthorizedAsync(siteId, user.UserId, global::Stomme.Pipelines.PipelinesPermissions.View, ct))
                {
                    people.Add((user.UserId, new PipelineMentionView { UserName = user.Username, DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName }));
                }
            }

            return people.OrderBy(person => person.Item2.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Tells the people a just-written comment mentions; answers whom it told and the names that told nobody. A failure to
        /// tell never undoes the comment: it is logged, and the names are said back as told nobody.
        /// </summary>
        public async Task<(List<string> Told, List<string> NotTold)> TellAsync(
            Alias alias, PipelineStepCommentAggregate comment, string? pipelineName, string? stepName, CancellationToken ct)
        {
            var told = new List<string>();
            var notTold = new List<string>();
            var names = PipelineStepMentionNames.Of(comment.Text);
            if (names.Count == 0)
            {
                return (told, notTold);
            }

            var people = await PeopleAsync(alias.SiteId, ct);
            var recipients = new List<(int UserId, string DisplayName)>();
            foreach (var name in names)
            {
                var person = people.FirstOrDefault(candidate => string.Equals(candidate.View.UserName, name, StringComparison.OrdinalIgnoreCase));
                if (person.View is null || recipients.Count >= PipelineStepCommentLimits.MaxMentions)
                {
                    notTold.Add(name);
                }
                else if (person.UserId != comment.UserId && recipients.All(recipient => recipient.UserId != person.UserId))
                {
                    recipients.Add((person.UserId, person.View.DisplayName));
                }
            }

            if (recipients.Count == 0)
            {
                return (told, notTold);
            }

            if (services.GetService<INotificationDispatcher>() is not { } dispatcher)
            {
                logger.LogWarning("A comment on pipeline {DefinitionId} mentions {Count} people, but the Notifications module is not composed here.", comment.DefinitionId, recipients.Count);
                notTold.AddRange(recipients.Select(recipient => recipient.DisplayName));
                return (told, notTold);
            }

            try
            {
                var link = await PipelineApprovalNotices.ModulePageAsync(alias, pageModules, "Editor",
                    "definition=" + Uri.EscapeDataString(comment.DefinitionId) + "&step=" + Uri.EscapeDataString(comment.StepId));
                var (subject, body) = Message(comment.UserName, pipelineName, stepName ?? comment.StepId, comment.Text, link);
                await dispatcher.DispatchAsync(new NotificationDispatchRequest(
                    NotificationTemplateCatalogue.GenericMessageKey,
                    recipients.Select(recipient => new DispatchRecipient(NotificationRecipientKind.User, recipient.UserId.ToString(CultureInfo.InvariantCulture))).ToList(),
                    new[] { NotificationChannelKind.InApp, NotificationChannelKind.Email },
                    new[] { new DispatchParameter("subject", subject), new DispatchParameter("body", body) },
                    NotificationSeverity.Information,
                    Topic), ct);
                told.AddRange(recipients.Select(recipient => recipient.DisplayName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The people a comment on pipeline {DefinitionId} mentions could not be told.", comment.DefinitionId);
                notTold.AddRange(recipients.Select(recipient => recipient.DisplayName));
            }

            return (told, notTold);
        }

        /// <summary>What the mentioned are told: who, where, what was said, and the way to the step.</summary>
        public static (string Subject, string Body) Message(string? author, string? pipelineName, string stepName, string text, string? link)
        {
            var who = string.IsNullOrWhiteSpace(author) ? "Someone" : author;
            var where = string.IsNullOrWhiteSpace(pipelineName) ? "a pipeline" : "'" + pipelineName + "'";
            var subject = $"{who} mentioned you on {where}";
            var body = $"{who} mentioned you on step '{stepName}' of {where}:\n\n{text}"
                + (link is null ? "\n\nOpen the pipeline on the site's Pipelines page to answer." : "\n\nAnswer here: " + link);
            return (subject.Length > 200 ? subject[..200] : subject, body);
        }
    }
}
