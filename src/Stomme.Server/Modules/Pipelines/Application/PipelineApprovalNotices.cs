#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// What an "Ask for approval" tells the people it names (BL-229 - n8n's approval node SENDS the request): who they are, with
    /// a role resolved to the users who hold it on the site now, and the address of the run's page, where they decide.
    /// </summary>
    public static class PipelineApprovalNotices
    {
        /// <summary>The topic a notice that a decision waits is sent under.</summary>
        public const string Topic = "pipelines.approvals";

        /// <summary>
        /// The recipients the step's lines name: a role is everyone holding it on the site now (an expired or not yet effective
        /// membership, a deleted user, left out) - Administrators with the Host Users -, a user by id, an address as written - each
        /// user once.
        /// </summary>
        public static async Task<IReadOnlyList<DispatchRecipient>> RecipientsAsync(
            IEnumerable<(string Kind, string Value)> named, int siteId, IUserRoleRepository? userRoles, DateTime utcNow)
        {
            var users = new List<int>();
            var addresses = new List<string>();
            foreach (var (kind, value) in named)
            {
                switch (kind)
                {
                    case PipelineApprovals.UserRecipient when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId):
                        users.Add(userId);
                        break;
                    case PipelineApprovals.EmailRecipient:
                        addresses.Add(value);
                        break;
                    case PipelineApprovals.RoleRecipient when userRoles is not null:
                        // Administrators are the site's operators, the Host Users with them (SiteOperators): a fresh
                        // site's only administrator is the host, who holds Host Users alone - found driving the starter.
                        var operators = string.Equals(value, RoleNames.Admin, StringComparison.OrdinalIgnoreCase);
                        var members = (await userRoles.GetUserRolesAsync(operators ? RoleNames.Admin : value, siteId)).ToList();
                        if (operators)
                        {
                            members.AddRange(await userRoles.GetUserRolesAsync(RoleNames.Host, siteId));
                        }

                        users.AddRange(members
                            .Where(member => (member.EffectiveDate is null || member.EffectiveDate <= utcNow)
                                && (member.ExpiryDate is null || member.ExpiryDate > utcNow)
                                && member.User?.IsDeleted != true)
                            .Select(member => member.UserId));
                        break;
                }
            }

            return users.Distinct()
                .Select(id => new DispatchRecipient(NotificationRecipientKind.User, id.ToString(CultureInfo.InvariantCulture)))
                .Concat(addresses.Distinct(StringComparer.OrdinalIgnoreCase).Select(address => new DispatchRecipient(NotificationRecipientKind.Email, address)))
                .ToList();
        }

        /// <summary>
        /// The run's page on the site, as an absolute address - the Pipelines module where it is placed (the admin page first);
        /// null when the site has no such page.
        /// </summary>
        public static Task<string?> RunPageAsync(Alias? alias, IPageModuleRepository? pageModules, string runId) =>
            ModulePageAsync(alias, pageModules, "Run", "id=" + Uri.EscapeDataString(runId));

        /// <summary>An address on the site's Pipelines page for one of its views (Run, Editor), or null when no page shows it.</summary>
        public static async Task<string?> ModulePageAsync(Alias? alias, IPageModuleRepository? pageModules, string action, string query)
        {
            if (alias is null || pageModules is null)
            {
                return null;
            }

            var definition = typeof(Stomme.Modules.Pipelines.Index).ToModuleDefinitionName();
            var placed = (await pageModules.GetPageModulesAsync(alias.SiteId))
                .Where(pageModule => !pageModule.IsDeleted && pageModule.Page is { IsDeleted: false } && pageModule.Module?.ModuleDefinitionName == definition)
                .OrderBy(pageModule => pageModule.Page.Path.StartsWith("admin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(pageModule => pageModule.ModuleId)
                .FirstOrDefault();
            return placed is null
                ? null
                : alias.Protocol + alias.Name + Utilities.EditUrl("", placed.Page.Path, placed.ModuleId, action, query);
        }

        /// <summary>The notice's subject and body: what is asked, by when, and where to decide.</summary>
        public static (string Subject, string Body) Message(string? pipelineName, string? stepName, string question, DateTimeOffset? expires, string? runPage)
        {
            var subject = "Approval needed: " + (string.IsNullOrWhiteSpace(pipelineName) ? (string.IsNullOrWhiteSpace(stepName) ? "a pipeline" : stepName) : pipelineName);
            var body = question
                + (expires is null ? "" : $"\n\nTo be decided by {expires.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC.")
                + (runPage is null
                    ? "\n\nDecide on the site's Pipelines page: All runs lists the runs waiting for a decision."
                    : "\n\nDecide here: " + runPage);
            return (subject.Length > 200 ? subject[..200] : subject, body);
        }
    }
}
