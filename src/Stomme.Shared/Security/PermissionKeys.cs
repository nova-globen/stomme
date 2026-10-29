using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Shared;

namespace Stomme.Security
{
    /// <summary>
    /// One parsed <c>resource:operation:Roles</c> entry of a module's <c>PermissionNames</c> declaration - the
    /// form every 3-segment consumer in the framework already understands (<c>PageModuleRepository</c>'s merge,
    /// <c>PermissionGrid</c>, the dynamic authorization policy provider).
    /// </summary>
    public sealed class PermissionKeyDeclaration
    {
        public PermissionKeyDeclaration(string entry, string resource, string operation, IReadOnlyList<string> roles)
        {
            Entry = entry;
            Resource = resource;
            Operation = operation;
            Roles = roles;
        }

        /// <summary>The entry exactly as it appeared in the declaration string.</summary>
        public string Entry { get; }

        /// <summary>The first segment - the Permission row's <c>EntityName</c>.</summary>
        public string Resource { get; }

        /// <summary>The second segment - the Permission row's <c>PermissionName</c>.</summary>
        public string Operation { get; }

        /// <summary>The third segment split on ';' - the roles the entry grants to.</summary>
        public IReadOnlyList<string> Roles { get; }

        /// <summary>The two-segment key form a <c>[Requires]</c> attribute carries.</summary>
        public string Key => Resource + PermissionKeys.Separator + Operation;
    }

    /// <summary>
    /// The vocabulary of the authorization bridge: a lifted module's
    /// <c>[Requires("resource:operation")]</c> key, declared ONCE in its module definition's
    /// <c>PermissionNames</c> and stored as an ordinary Stomme Permission row
    /// <c>(SiteId, EntityName = resource, EntityId = -1, PermissionName = operation, Role|User, IsAuthorized)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these helpers exist at all.</b> A bare two-segment key written straight into <c>PermissionNames</c>
    /// is SILENTLY IGNORED by every 3-segment consumer in the framework (F2, measured) - it renders no grid
    /// column, materialises no row, and the control-panel add-module path writes the literal string as a Module
    /// permission. <see cref="Declare"/> is the one sanctioned way to turn keys into a declaration string, so
    /// that shape cannot be written by hand and mis-typed.
    /// </para>
    /// <para>
    /// <b>Lower-case is the contract, not a convenience</b> ("normalise the data, don't translate the
    /// query"): bare <c>==</c> is case-SENSITIVE on EF/PostgreSQL and case-INSENSITIVE on RavenDB, and the
    /// in-memory <c>PermissionName</c> match in <see cref="UserSecurity"/> is ordinal. A key whose row casing
    /// differs from the casing the guard looks up is a silent denial on one provider and a silent grant on
    /// another, so both halves are lower-cased at declaration.
    /// </para>
    /// <para>
    /// <b>A key is never an <see cref="EntityNames"/> constant.</b> Those name the framework's OWN entities, and
    /// the same 3-segment declaration form already carries framework API policies
    /// (<c>User:Write:Administrators</c> on the stock Users module). Keeping the two vocabularies disjoint is
    /// what lets a reader - and the reflective declaration gate - tell a lifted key from a framework policy by
    /// looking at the resource alone.
    /// </para>
    /// </remarks>
    public static class PermissionKeys
    {
        /// <summary>Separates the segments of a key and of a declaration entry.</summary>
        public const char Separator = ':';

        /// <summary>Separates role names inside a declaration entry's third segment.</summary>
        public const char RoleSeparator = ';';

        /// <summary>Separates entries in a <c>PermissionNames</c> declaration string.</summary>
        public const char EntrySeparator = ',';

        /// <summary>
        /// The bound on both halves of a key: the <c>entity_name</c> and <c>permission_name</c> columns of the
        /// permission table. Read off a LIVE migrated database, never off the entity builders, which lie.
        /// </summary>
        public const int MaxSegmentLength = 50;

        /// <summary>Every key-derived Permission row carries this EntityId: the key names a resource, not an instance.</summary>
        public const int KeyEntityId = -1;

        // Reflected once. EntityNames is a flat class of public const strings; matching by VALUE (not by member
        // name) is what the collision rule is about - "Host" is a conceptual entity with no table and still
        // reserves the word.
        private static readonly string[] FrameworkEntityNames = typeof(EntityNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue())
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();

        /// <summary>The <see cref="EntityNames"/> constants, reflected - the reserved half of the vocabulary.</summary>
        public static IReadOnlyList<string> FrameworkEntities => FrameworkEntityNames;

        /// <summary>
        /// Builds the <c>PermissionNames</c> declaration for a module from its permission keys:
        /// <c>"View,Edit,pipelines:view:Administrators,pipelines:manage:Administrators"</c>.
        /// </summary>
        /// <remarks>
        /// <c>View,Edit</c> ALWAYS come first because the control-panel add-module path assumes it: it REPLACES
        /// its default <c>Edit</c> with the whole declaration and derives the new module instance's permissions
        /// from it. <see cref="RoleNames.Admin"/> is always among the roles - the seeded Administrators grant is
        /// what admits an administrator (and, through its Administrators role claim, the Host: there is no Host
        /// short-circuit in the decision, F1).
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A key is malformed - not exactly two non-empty segments, longer than <see cref="MaxSegmentLength"/>,
        /// carrying a reserved separator, or naming an <see cref="EntityNames"/> constant. Declaration is an
        /// authoring-time contract: a silently-dropped key is the defect this method exists to prevent.
        /// </exception>
        public static string Declare(IEnumerable<string> keys, params string[] defaultRoles)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            var roleSegment = RoleSegment(defaultRoles, nameof(defaultRoles));
            return Declare(keys.Select(key => (key, roleSegment)), nameof(keys));
        }

        /// <summary>
        /// Builds the <c>PermissionNames</c> declaration from keys that each carry their OWN default roles:
        /// <c>"View,Edit,notifications:view-own:Registered Users;Administrators,notifications:send:Administrators"</c>
        /// (BL-78 - a module whose keys are not all held by the same roles).
        /// </summary>
        /// <remarks>
        /// Identical rules to the uniform overload: <c>View,Edit</c> first, <see cref="RoleNames.Admin"/> appended
        /// to every entry's roles, the key lower-cased and validated, an identical entry emitted once (so with the
        /// same roles on every key the two overloads produce the same string). A key declared twice with DIFFERENT
        /// roles is rejected: which grant would win is not something a declaration should leave to order.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A key is malformed (see the uniform overload), a role carries a reserved separator, or a key is declared
        /// twice with different roles.
        /// </exception>
        public static string Declare(IEnumerable<(string Key, string[] Roles)> keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            return Declare(keys.Select(entry => (entry.Key, RoleSegment(entry.Roles, nameof(keys)))), nameof(keys));
        }

        private static string Declare(IEnumerable<(string Key, string RoleSegment)> keys, string parameterName)
        {
            var entries = new List<string> { PermissionNames.View, PermissionNames.Edit };
            var rolesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, roleSegment) in keys)
            {
                var normalized = Normalize(key);
                if (rolesByKey.TryGetValue(normalized, out var declared))
                {
                    if (!string.Equals(declared, roleSegment, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"The permission key '{normalized}' is declared twice with different roles ('{declared}' and '{roleSegment}').",
                            parameterName);
                    }

                    continue;
                }

                rolesByKey.Add(normalized, roleSegment);
                entries.Add(normalized + Separator + roleSegment);
            }

            return string.Join(EntrySeparator.ToString(), entries);
        }

        // The ';'-joined role segment of one entry: trimmed, blank names skipped, ordinal de-duplication, a reserved
        // separator rejected, Administrators appended when absent.
        private static string RoleSegment(IEnumerable<string> declaredRoles, string parameterName)
        {
            var roles = new List<string>();
            if (declaredRoles != null)
            {
                foreach (var role in declaredRoles)
                {
                    if (string.IsNullOrWhiteSpace(role))
                    {
                        continue;
                    }

                    var trimmed = role.Trim();
                    if (trimmed.IndexOf(Separator) >= 0 || trimmed.IndexOf(RoleSeparator) >= 0 || trimmed.IndexOf(EntrySeparator) >= 0)
                    {
                        throw new ArgumentException($"The role name '{trimmed}' contains a reserved separator.", parameterName);
                    }

                    if (!roles.Contains(trimmed, StringComparer.Ordinal))
                    {
                        roles.Add(trimmed);
                    }
                }
            }

            if (!roles.Contains(RoleNames.Admin, StringComparer.Ordinal))
            {
                roles.Add(RoleNames.Admin);
            }

            return string.Join(RoleSeparator.ToString(), roles);
        }

        /// <summary>
        /// Splits <c>resource:operation</c> into its lower-cased halves - a resource may itself be DOTTED
        /// (<c>platform.modules:manage</c>), but never carry a second ':'. Throws when the key is malformed.
        /// </summary>
        public static (string Resource, string Operation) Split(string key)
        {
            if (!TrySplit(key, out var resource, out var operation))
            {
                throw new ArgumentException($"'{key}' is not a 'resource:operation' permission key.", nameof(key));
            }

            return (resource, operation);
        }

        /// <summary>
        /// Splits <c>resource:operation</c> into its lower-cased halves. False when the key is null, has no
        /// ':', has MORE than one ':', or either half is empty - never a silent fallback to something else.
        /// </summary>
        /// <remarks>
        /// EXACTLY two segments, which is the rule <see cref="Declare"/> and <see cref="IsKey"/> enforce as
        /// well. An earlier version split at the LAST ':', so <c>a:b:c</c> was REJECTED where the row is
        /// written and ACCEPTED where it is read (as resource <c>a:b</c>) - one string with two meanings at
        /// the two ends of the bridge. A malformed key is now denied at the lookup instead of reinterpreted.
        /// </remarks>
        public static bool TrySplit(string key, out string resource, out string operation)
        {
            resource = "";
            operation = "";

            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            var segments = key.Trim().Split(Separator);
            if (segments.Length != 2)
            {
                return false;
            }

            resource = segments[0].Trim().ToLowerInvariant();
            operation = segments[1].Trim().ToLowerInvariant();
            return resource.Length > 0 && operation.Length > 0;
        }

        /// <summary>
        /// True when <paramref name="value"/> is a well-formed permission KEY: exactly two non-empty segments,
        /// each already lower-case and within <see cref="MaxSegmentLength"/>, with a resource that is not an
        /// <see cref="EntityNames"/> constant. This is the discriminator that tells a lifted key from the
        /// framework's own <c>EntityName:Permission</c> API policies.
        /// </summary>
        public static bool IsKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var segments = value.Split(Separator);
            if (segments.Length != 2)
            {
                return false;
            }

            return IsValidSegment(segments[0]) && IsValidSegment(segments[1]) && !IsFrameworkEntity(segments[0]);
        }

        /// <summary>
        /// True when a segment is a usable half of a key: non-empty, lower-case, within the column bound, and
        /// free of the separators the declaration string is built from.
        /// </summary>
        public static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment.Length > MaxSegmentLength)
            {
                return false;
            }

            if (segment.Trim() != segment)
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (character == Separator || character == RoleSeparator || character == EntrySeparator || char.IsWhiteSpace(character))
                {
                    return false;
                }

                if (char.IsUpper(character))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="resource"/> names one of the framework's own entities - CASE-INSENSITIVELY,
        /// because a lower-cased <c>user</c> resource and the framework's <c>User</c> rows would share a namespace
        /// on a case-insensitive provider (RavenDB) and not on a case-sensitive one (PostgreSQL).
        /// </summary>
        public static bool IsFrameworkEntity(string resource)
        {
            return !string.IsNullOrEmpty(resource)
                && FrameworkEntityNames.Any(name => string.Equals(name, resource, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The comma-separated entries of a declaration string, trimmed; empty when there are none.</summary>
        public static IReadOnlyList<string> Entries(string permissionNames)
        {
            if (string.IsNullOrWhiteSpace(permissionNames))
            {
                return Array.Empty<string>();
            }

            return permissionNames
                .Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Every well-formed 3-segment <c>resource:operation:Roles</c> entry of a declaration - framework API
        /// policies (<c>User:Write:Administrators</c>) included. Malformed entries are NOT returned; the
        /// reflective declaration gate is what fails them, so a bad declaration cannot become a bad row here.
        /// </summary>
        public static IReadOnlyList<PermissionKeyDeclaration> ParseEntries(string permissionNames)
        {
            var declarations = new List<PermissionKeyDeclaration>();
            foreach (var entry in Entries(permissionNames))
            {
                var segments = entry.Split(Separator);
                if (segments.Length != 3)
                {
                    continue;
                }

                var resource = segments[0].Trim();
                var operation = segments[1].Trim();
                if (resource.Length == 0 || operation.Length == 0)
                {
                    continue;
                }

                var roles = segments[2]
                    .Split(RoleSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(role => role.Trim())
                    .Where(role => role.Length > 0)
                    .ToList();

                declarations.Add(new PermissionKeyDeclaration(entry, resource, operation, roles));
            }

            return declarations;
        }

        /// <summary>
        /// The entries of a declaration that are lifted permission KEYS - i.e. whose resource is not one of the
        /// framework's own entities. These are the entries that seed and reconcile Permission rows, that get a
        /// grid column, and that the definition PUT accepts; the framework's API policies are left exactly as
        /// they are, so a stock install gains no row and its role-decided policies stay role-decided.
        /// </summary>
        /// <remarks>
        /// Both halves are NORMALISED here (lower-cased, and the <see cref="Entry"/> rebuilt from them), so a
        /// hand-written uppercase declaration cannot produce a row the guard's lower-cased lookup misses on
        /// PostgreSQL and finds on RavenDB - the same install answering two ways. An entry whose half is
        /// over <see cref="MaxSegmentLength"/> or otherwise unusable is DROPPED, and named by
        /// <see cref="InvalidKeyEntries"/>.
        /// </remarks>
        public static IReadOnlyList<PermissionKeyDeclaration> ParseKeys(string permissionNames)
        {
            var keys = new List<PermissionKeyDeclaration>();
            foreach (var entry in ParseEntries(permissionNames))
            {
                if (IsFrameworkEntity(entry.Resource))
                {
                    continue;
                }

                var resource = entry.Resource.ToLowerInvariant();
                var operation = entry.Operation.ToLowerInvariant();
                if (!IsValidSegment(resource) || !IsValidSegment(operation))
                {
                    continue; // named by InvalidKeyEntries; a row at a length PostgreSQL rejects is worse
                }

                var normalized = resource + Separator + operation + Separator + string.Join(RoleSeparator.ToString(), entry.Roles);
                keys.Add(new PermissionKeyDeclaration(normalized, resource, operation, entry.Roles));
            }

            return keys;
        }

        /// <summary>
        /// The non-framework 3-segment entries that are NOT usable keys - the declarations this parser DROPS.
        /// </summary>
        /// <remarks>
        /// <see cref="Declare"/> cannot produce one, but a module installed as a <c>.nupkg</c> writes its
        /// <c>PermissionNames</c> by hand and the reflective declaration gate only ever sees IN-TREE
        /// assemblies. A dropped entry is a key with no rows, i.e. denied on every provider - which is the
        /// fail-closed answer, but a silent one, so the reconciliation logs these by name.
        /// </remarks>
        public static IReadOnlyList<string> InvalidKeyEntries(string permissionNames)
        {
            return ParseEntries(permissionNames)
                .Where(entry => !IsFrameworkEntity(entry.Resource))
                .Where(entry => !IsValidSegment(entry.Resource.ToLowerInvariant()) || !IsValidSegment(entry.Operation.ToLowerInvariant()))
                .Select(entry => entry.Entry)
                .ToList();
        }


        private static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A permission key cannot be empty.", nameof(key));
            }

            var trimmed = key.Trim();
            if (!TrySplit(trimmed, out var resource, out var operation))
            {
                throw new ArgumentException($"'{trimmed}' is not a 'resource:operation' permission key (exactly two non-empty segments).", nameof(key));
            }

            if (!IsValidSegment(resource) || !IsValidSegment(operation))
            {
                throw new ArgumentException($"'{trimmed}' has a segment that is empty, longer than {MaxSegmentLength} characters, or carries whitespace.", nameof(key));
            }

            if (IsFrameworkEntity(resource))
            {
                throw new ArgumentException($"'{resource}' is a Stomme EntityNames constant and cannot be used as a permission key resource.", nameof(key));
            }

            return resource + Separator + operation;
        }
    }
}