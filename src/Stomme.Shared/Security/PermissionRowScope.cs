using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models;

namespace Stomme.Security
{
    /// <summary>
    /// Which rows of a submitted <c>PermissionList</c> belong to the entity being written, and which would be
    /// written as SITE-WIDE rows for some other entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PermissionRepository.UpdatePermissionsAsync(siteId, entityName, entityId, rows)</c> defaults an empty
    /// <c>EntityName</c> to <c>entityName</c> and stamps <c>EntityId = -1</c> on every row whose
    /// <c>EntityName</c> then differs - an ORDINAL comparison. A "foreign" row is therefore exactly a row with a
    /// non-empty EntityName that is not ordinal-equal to the entity: <c>module</c> against <c>Module</c> is
    /// foreign, because the sink would stamp it -1 too. This type mirrors that comparison on purpose, so the
    /// controller pre-check, the repository guard and the sink can never disagree about which rows are which.
    /// </para>
    /// <para>
    /// Driven before the fix: Edit on one module, page or folder was enough to write a
    /// <c>(User, -1, Write)</c> row for All Users, after which the Host-only <c>DELETE api/User/{id}</c>
    /// answered 200 anonymously.
    /// </para>
    /// </remarks>
    public static class PermissionRowScope
    {
        /// <summary>Rows that would be written for a DIFFERENT entity than <paramref name="entityName"/> (site-wide, EntityId -1).</summary>
        public static IReadOnlyList<Permission> ForeignRows(IEnumerable<Permission> rows, string entityName)
        {
            if (rows == null)
            {
                return Array.Empty<Permission>();
            }

            return rows
                .Where(row => row != null && !string.IsNullOrEmpty(row.EntityName) && !string.Equals(row.EntityName, entityName, StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>The complement of <see cref="ForeignRows"/>: rows with no EntityName or exactly <paramref name="entityName"/>. Null-safe.</summary>
        public static List<Permission> OwnRows(IEnumerable<Permission> rows, string entityName)
        {
            if (rows == null)
            {
                return new List<Permission>();
            }

            return rows
                .Where(row => row != null && (string.IsNullOrEmpty(row.EntityName) || string.Equals(row.EntityName, entityName, StringComparison.Ordinal)))
                .ToList();
        }

        /// <summary>
        /// The PLAIN permission names of a declaration (<c>View,Edit</c>) - every comma entry that contains no
        /// <c>':'</c>, trimmed, in order; <c>""</c> when none remain. What an instance-level permission grid may
        /// offer: a <c>resource:operation:Roles</c> entry (a lifted key OR a framework policy) is a site-wide row.
        /// </summary>
        public static string PlainPermissionNames(string permissionNames)
        {
            if (string.IsNullOrWhiteSpace(permissionNames))
            {
                return "";
            }

            return string.Join(",", permissionNames
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0 && !entry.Contains(':')));
        }
    }
}