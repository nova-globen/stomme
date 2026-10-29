#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Modules.Portability.Contributions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// Every aggregate that holds a user id is ACCOUNTED FOR in a person's data package: either a subject contributor
    /// carries it, or it is left out for a stated reason. A new aggregate with a user-id member fails this gate until
    /// someone decides which - the way a person's data stops leaking out of the package silently.
    /// </summary>
    /// <remarks>
    /// Discovery is reflective over the server assembly: every <c>AggregateRoot&lt;&gt;</c> (the clean-arch modules) and
    /// every framework seam aggregate (<c>Stomme.Repository.Stores.*Aggregate</c>), and on each the <c>int</c> /
    /// <c>int?</c> members whose name ends in <c>UserId</c>. A stale entry - one naming an aggregate that no longer
    /// exists or no longer holds a user id - fails too, so the list cannot rot.
    /// </remarks>
    public class SubjectDataCoverageGateTests(ITestOutputHelper output)
    {
        /// <summary>Aggregate type name -> the subject contributor that carries it, or "LEFT OUT: why".</summary>
        private static readonly IReadOnlyDictionary<string, string> Accounted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- carried --------------------------------------------------------------------------------------------------
            ["NotificationAggregate"] = "NotificationMetadataSubjectContributor",
            ["NotificationPreferenceAggregate"] = "NotificationPreferenceSubjectContributor",
            ["PageAggregate"] = "PersonalPagesSubjectContributor",
            ["UserRoleAggregate"] = "SiteRolesSubjectContributor",
            ["UserSessionAggregate"] = "SessionSubjectContributor",
            ["AssistantConversationAggregate"] = "AssistantConversationSubjectContributor",
            ["ResourceLockAggregate"] = "CollaborationSubjectContributor",
            ["EditSessionAggregate"] = "CollaborationSubjectContributor",
            ["IdentityAuditEntryAggregate"] = "IdentityAuditSubjectContributor",
            ["PipelineStepCommentAggregate"] = "PipelineStepCommentSubjectContributor",

            // ---- left out, each for a stated reason (the allowlist, extended to what landed since) --------------------------
            ["VisitorAggregate"] = "LEFT OUT: per-visit analytics (address, agent, url) aged out by VisitorRetention",
            ["LogAggregate"] = "LEFT OUT: the operational event log - one row can name several people and hold request internals; aged out by LogRetention",
            ["PermissionAggregate"] = "LEFT OUT: a per-user permission row is the SITE's access configuration about the person, not data they gave; its effect is what they can reach",
            ["PortabilityPackageAggregate"] = "LEFT OUT: the record of a package request itself - the person sees their own packages in the list; expired with the package",
            ["PortabilityAuditEntryAggregate"] = "LEFT OUT: the portability audit - who asked for whose data; an accountability record, not the person's data",
            ["ExportJobAggregate"] = "LEFT OUT: an export job the person requested - operational, kept 7 days by DataExport's own retention",
            ["ExportAuditEntryAggregate"] = "LEFT OUT: the export audit - an accountability record of who exported what",
            ["ExportScheduleAggregate"] = "LEFT OUT: a recurring export the person set up - the site's operational configuration of a job, which they see and manage on the DataExport page; its runs are export jobs (above), and erasure deletes it",
            ["McpAuditEntryAggregate"] = "LEFT OUT: the MCP tool-call ledger - a security record of actions taken through tools; erasure is undecided",
            ["PipelineAuditEntryAggregate"] = "LEFT OUT: the pipeline activity ledger - an accountability record of who changed or ran which pipeline, as the export and portability audits are; erasure retains it a year as a security record",
            ["UsageRecordAggregate"] = "LEFT OUT: the metering ledger - usage counted for quota and billing, not data the person provided",
            ["AiUsageRecordAggregate"] = "LEFT OUT: AI token metering for cost and quota - counts, not content",
            ["ErasureRequestAggregate"] = "LEFT OUT: the erasure request itself - it carries the subject's user id only while the run is open and clears it on completion; what remains is the receipt, the accountability record of the erasure, which names no one",
        };

        [Fact]
        public void Every_aggregate_holding_a_user_id_is_carried_or_left_out_for_a_reason()
        {
            var found = UserBearingAggregates();
            foreach (var (name, members) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"{name}: {string.Join(", ", members)}");
            }

            var unaccounted = found.Keys.Where(name => !Accounted.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var stale = Accounted.Keys.Where(name => !found.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();

            Assert.True(unaccounted.Count == 0 && stale.Count == 0,
                $"Aggregates holding a user id and not accounted for: {string.Join(", ", unaccounted)}{Environment.NewLine}" +
                $"Stale entries (no such aggregate, or no user id any more): {string.Join(", ", stale)}");
        }

        [Fact]
        public void Every_entry_names_a_real_contributor_or_gives_a_reason()
        {
            var contributors = typeof(UserAccountSubjectContributor).Assembly.GetTypes()
                .Where(type => typeof(Stomme.Portability.ISubjectDataContributor).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => type.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var (aggregate, account) in Accounted)
            {
                Assert.True(account.StartsWith("LEFT OUT: ", StringComparison.Ordinal) && account.Length > "LEFT OUT: ".Length + 10 || contributors.Contains(account),
                    $"{aggregate} -> '{account}' is neither a subject contributor nor a stated reason.");
            }
        }

        // The discovery must not find nothing: the framework's notifications and user roles hold user ids by construction.
        [Fact]
        public void Discovery_finds_the_known_user_bearing_aggregates()
        {
            var found = UserBearingAggregates();
            Assert.Contains("NotificationAggregate", found.Keys);
            Assert.Contains("UserSessionAggregate", found.Keys);
        }

        private static Dictionary<string, List<string>> UserBearingAggregates()
        {
            var server = typeof(UserAccountSubjectContributor).Assembly;
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var type in server.GetTypes().Where(IsAggregate))
            {
                var members = MembersOf(type, property => property.Name.EndsWith("UserId", StringComparison.Ordinal)
                        && (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?)))
                    .ToList();
                if (members.Count > 0)
                {
                    result[type.Name] = members;
                }
            }

            return result;
        }

        // A user id can sit ONE level down, in a child collection riding the module store's JSON column
        // (an editing session's participants). Top-level properties alone could not see it; the element type of a
        // collection property declared in this assembly is read by the same rule, named "Collection.Member".
        private static IEnumerable<string> MembersOf(Type type, Func<PropertyInfo, bool> identifying)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (identifying(property))
                {
                    yield return property.Name;
                    continue;
                }

                var element = ElementOf(property.PropertyType);
                if (element is not null && element.Assembly == type.Assembly)
                {
                    foreach (var nested in element.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(identifying))
                    {
                        yield return property.Name + "." + nested.Name;
                    }
                }
            }
        }

        private static Type? ElementOf(Type type)
        {
            if (type == typeof(string))
            {
                return null;
            }

            var enumerable = (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type : null)
                ?? type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            var element = enumerable?.GetGenericArguments()[0];
            return element is { IsClass: true } && element != typeof(string) ? element : null;
        }

        private static bool IsAggregate(Type type)
        {
            if (type.IsAbstract || !type.IsClass)
            {
                return false;
            }

            if (type.Namespace == "Stomme.Repository.Stores" && type.Name.EndsWith("Aggregate", StringComparison.Ordinal))
            {
                return true;
            }

            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition().Name.StartsWith("AggregateRoot", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
