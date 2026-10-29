#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Takes the <c>Everyone</c> View grant off user folders created before BL-115.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a security backfill, and the exposure is live on every install older than 2026-09-17.</b>
    /// BL-115 closed the two places that SEED a user's "My Folder", so folders created since are private. It did
    /// nothing for folders that already existed — including the one the installer creates for the host account.
    /// Those still carry a View row naming <see cref="RoleNames.Everyone"/>, and <c>UserSecurity.IsAuthorized</c>
    /// treats Everyone as granted to everybody INCLUDING an anonymous caller, so every file under
    /// <c>Users/{id}/</c> stays downloadable by anyone who guesses a sequential file id. Closing the seed without
    /// the backfill fixes only installs that do not exist yet.
    /// </para>
    /// <para>
    /// <b>It is deliberately narrow.</b> It touches a folder only when BOTH are true: the path is exactly
    /// <c>Users/{digits}/</c>, and the folder still carries an authorized View row for Everyone. The
    /// <c>Users/</c> parent is not a user folder and is left alone; a public folder anywhere else is left alone;
    /// and every other row on a matched folder — the owner's, the administrators', an operator's extra grant — is
    /// written back untouched. What it removes is precisely the row BL-115 stopped writing.
    /// </para>
    /// <para>
    /// <b>Additive and idempotent.</b> A folder with no Everyone row is skipped without a write, so the second
    /// boot does nothing and an operator who deliberately re-grants Everyone on a user folder is overruled once,
    /// loudly (it is logged), rather than silently fought on every boot — re-granting is not a supported way to
    /// share a user's private folder, and the log says so.
    /// </para>
    /// </remarks>
    public sealed class UserFolderPrivacyReconciler : ISiteContentReconciler
    {
        /// <summary>A user's own folder: <c>Users/{id}/</c> and nothing deeper.</summary>
        private static readonly Regex UserFolderPath = new(@"^Users/\d+/$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string Name => "User folder privacy backfill";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            var folders = context.Services.GetRequiredService<IFolderRepository>();
            var permissions = context.Services.GetRequiredService<IPermissionRepository>();
            var logger = context.Services.GetRequiredService<ILogger<UserFolderPrivacyReconciler>>();

            var siteId = context.Site.SiteId;
            var candidates = (await folders.GetFoldersAsync(siteId))
                .Where(folder => !string.IsNullOrEmpty(folder.Path) && UserFolderPath.IsMatch(folder.Path))
                .ToList();

            foreach (var folder in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                var rows = (await permissions.GetPermissionsAsync(siteId, EntityNames.Folder, folder.FolderId)).ToList();

                var exposed = rows
                    .Where(row => row.PermissionName == PermissionNames.View
                               && row.IsAuthorized
                               && string.Equals(row.RoleName, RoleNames.Everyone, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exposed.Count == 0)
                {
                    continue; // already private - the second boot does nothing
                }

                // Every other row is written back as it stands: the owner's, the administrators', and anything an
                // operator added. Only the Everyone View row goes.
                var kept = rows.Except(exposed).ToList();
                await permissions.UpdatePermissionsAsync(siteId, EntityNames.Folder, folder.FolderId, kept);

                logger.LogWarning(
                    "Removed the {Role} View grant from the user folder '{Path}' on site {SiteId}: a user's folder is " +
                    "private, and that row made every file under it readable by an anonymous caller.",
                    RoleNames.Everyone, folder.Path, siteId);
            }
        }
    }
}
