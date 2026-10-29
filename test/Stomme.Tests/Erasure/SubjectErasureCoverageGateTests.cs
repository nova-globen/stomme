#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Stomme.Erasure;
using Stomme.Modules.Erasure.Participants;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Erasure
{
    /// <summary>
    /// BL-116 increment 3 (D112's gate) - the twin of D107's <c>SubjectDataCoverageGateTests</c>, widened from user ids
    /// to user-identifying strings: every aggregate that carries a user id, an e-mail address, a username, a display
    /// name, a network address, an identity subject or an authorship stamp is ACCOUNTED FOR under the right to erasure:
    /// either a participant DECLARES the store (Delete / Anonymise / Retain) or it is LEFT OUT for a stated reason. A
    /// new aggregate with such a member fails the build until someone decides which; a declaration that names a store
    /// no participant declares any more fails too; and every participant class must be registered, since one that is
    /// written but never registered erases nothing and reports nothing.
    /// </summary>
    /// <remarks>
    /// Discovery is reflective over the server assembly (every <c>AggregateRoot&lt;&gt;</c> and every framework seam
    /// aggregate). The declarations are read from the SOURCE (<c>ErasureDeclaration.Delete("...")</c> and its
    /// siblings under <c>src/Stomme.Server</c>), not from instances - a participant is built by the container with
    /// its stores, which a build gate does not compose. The authorship stamps (<c>CreatedBy</c>/<c>ModifiedBy</c>/
    /// <c>DeletedBy</c>, the username of the author on nearly every framework row) are one FAMILY with one entry - the
    /// reflective <c>AuthorshipStampsErasureParticipant</c> declares them as one store: an aggregate whose only
    /// identifying members are stamps falls under it, an aggregate with anything else needs an entry of its own. The evaluation is a pure function so the gate is validated in both directions with
    /// synthetic input (a planted undeclared aggregate fails, a stale entry fails, a clean set passes).
    /// </remarks>
    public class SubjectErasureCoverageGateTests(ITestOutputHelper output)
    {
        private const string Declared = "DECLARED: ";
        private const string LeftOut = "LEFT OUT: ";
        private const string StampsFamily = "(authorship stamps)";

        private static readonly HashSet<string> StampMembers = new(StringComparer.Ordinal) { "CreatedBy", "ModifiedBy", "DeletedBy" };

        /// <summary>Aggregate type name -> "DECLARED: store[; store]" (each a literal some participant declares) or "LEFT OUT: why".</summary>
        private static readonly IReadOnlyDictionary<string, string> Accounted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- the framework's stores (Modules/Erasure/Participants) ----------------------------------------------------
            ["SettingAggregate"] = Declared + "SettingAggregate (User)",
            ["NotificationAggregate"] = Declared + "NotificationAggregate (framework)",
            ["VisitorAggregate"] = Declared + "VisitorAggregate",
            ["LogAggregate"] = Declared + "LogAggregate",
            ["PageAggregate"] = Declared + "PageAggregate (personal)",
            ["UserRoleAggregate"] = Declared + "UserRoleAggregate",
            ["PermissionAggregate"] = Declared + "PermissionAggregate",

            // ---- the modules' stores ----------------------------------------------------------------------------------------
            ["UserSessionAggregate"] = Declared + "UserSessionAggregate",
            ["NotificationPreferenceAggregate"] = Declared + "NotificationPreferenceAggregate",
            ["IdentityAuditEntryAggregate"] = Declared + "IdentityAuditEntryAggregate",
            ["UsageRecordAggregate"] = Declared + "UsageRecordAggregate",
            ["AiUsageRecordAggregate"] = Declared + "AiUsageRecordAggregate",
            ["McpAuditEntryAggregate"] = Declared + "McpAuditEntryAggregate",
            ["PipelineAuditEntryAggregate"] = Declared + "PipelineAuditEntryAggregate",
            ["PipelineStepCommentAggregate"] = Declared + "PipelineStepCommentAggregate",
            ["PortabilityPackageAggregate"] = Declared + "PortabilityPackageAggregate (subject); PortabilityPackageAggregate (requester)",
            ["PortabilityAuditEntryAggregate"] = Declared + "PortabilityAuditEntryAggregate",
            ["ExportJobAggregate"] = Declared + "ExportJobAggregate",
            ["ExportAuditEntryAggregate"] = Declared + "ExportAuditEntryAggregate",
            ["ExportScheduleAggregate"] = Declared + "ExportScheduleAggregate",
            ["AssistantConversationAggregate"] = Declared + "AssistantConversationAggregate",
            ["ResourceLockAggregate"] = Declared + "ResourceLockAggregate",
            ["EditSessionAggregate"] = Declared + "EditSessionAggregate",

            // ---- left out, each for a stated reason ---------------------------------------------------------------------------
            ["UserAggregate"] = LeftOut + "the account row itself - UserManager.DeleteUser deletes it, and the ASP.NET Identity user, when the person holds no role on any other site of the install; while they do, the account is live on those sites and stays (the erasure is per site). Its DeletedBy / LastIPAddress go with the row.",
            ["ErasureRequestAggregate"] = LeftOut + "the erasure request itself - it carries the subject's user id and identity subject only while a run or a retention is open and clears them on completion; what remains is the receipt, which names no one (D112).",
            [StampsFamily] = Declared + "Authorship stamps",
            ["HtmlTextAggregate"] = Declared + "HtmlTextAggregate (stamps)",
        };

        /// <summary>Stores that are not aggregates (no reflective discovery can find them) and still hold the subject's rows.</summary>
        private static readonly IReadOnlyDictionary<string, string> NonAggregateStores = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OpenIddict tokens"] = Declared + "OpenIddict tokens",
            ["OpenIddict authorizations"] = Declared + "OpenIddict authorizations",
            ["ASP.NET Identity user (AspNetUsers)"] = LeftOut + "deleted by UserManager.DeleteUser together with the account row, under the same other-sites condition; the identity subject is captured into the ErasureSubject first, which is the only time it can be.",
            ["NotificationRequestAggregate.Recipients"] = Declared + "NotificationRequestAggregate",
        };

        [Fact]
        public void Every_aggregate_holding_a_user_id_or_an_identifying_string_is_declared_or_left_out_for_a_reason()
        {
            var found = IdentifyingAggregates();
            foreach (var (name, members) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"{name}: {string.Join(", ", members)}");
            }

            var declared = DeclaredStoreLiterals();
            output.WriteLine($"declared stores: {string.Join(" | ", declared.OrderBy(s => s, StringComparer.Ordinal))}");

            var problems = Evaluate(found, Accounted, NonAggregateStores, declared);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Every_participant_class_is_registered_in_a_startup()
        {
            var participants = typeof(UserSettingsErasureParticipant).Assembly.GetTypes()
                .Where(type => typeof(ISubjectErasureParticipant).IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            Assert.True(participants.Count >= 10, "The discovery must find the framework's and the modules' participants; found " + participants.Count);

            var registrationLines = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Stomme.Server"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(File.ReadLines)
                .Where(line => line.Contains("ISubjectErasureParticipant", StringComparison.Ordinal) && line.Contains("services.Add", StringComparison.Ordinal))
                .ToList();

            var unregistered = participants.Where(name => !registrationLines.Any(line => line.Contains(name + ">", StringComparison.Ordinal))).ToList();
            Assert.True(unregistered.Count == 0, "Participants never registered as ISubjectErasureParticipant (they erase nothing): " + string.Join(", ", unregistered));
        }

        // The discovery must not find nothing, and the widening must be real: the user row's e-mail and the log's address are strings.
        [Fact]
        public void Discovery_finds_ids_and_identifying_strings()
        {
            var found = IdentifyingAggregates();
            Assert.Contains("Email", found["UserAggregate"]);
            Assert.Contains("RemoteIPAddress", found["LogAggregate"]);
            Assert.Contains("ToUserId", found["NotificationAggregate"]);
            Assert.Contains("CreatedBy", found["AliasAggregate"]);
        }

        // ---- the gate validated in both directions, on synthetic input ---------------------------------------------------

        [Fact]
        public void A_planted_undeclared_aggregate_fails()
        {
            var found = new Dictionary<string, List<string>> { ["SettingAggregate"] = ["CreatedBy"], ["PlantedAggregate"] = ["OwnerUserId"] };
            var accounted = new Dictionary<string, string> { ["SettingAggregate"] = Declared + "SettingAggregate (User)", [StampsFamily] = LeftOut + "a synthetic reason long enough to count" };

            var problems = Evaluate(found, accounted, new Dictionary<string, string>(), ["SettingAggregate (User)"]);

            Assert.Contains(problems, p => p.Contains("PlantedAggregate", StringComparison.Ordinal));
        }

        [Fact]
        public void A_planted_stamps_only_aggregate_is_the_family_s_and_needs_no_entry()
        {
            var found = new Dictionary<string, List<string>> { ["PlantedAggregate"] = ["CreatedBy", "ModifiedBy"] };
            var accounted = new Dictionary<string, string> { [StampsFamily] = LeftOut + "a synthetic reason long enough to count" };

            Assert.Empty(Evaluate(found, accounted, new Dictionary<string, string>(), []));
        }

        [Fact]
        public void A_stale_entry_and_a_declaration_no_participant_makes_both_fail()
        {
            var found = new Dictionary<string, List<string>> { ["SettingAggregate"] = ["CreatedBy"] };
            var accounted = new Dictionary<string, string>
            {
                ["SettingAggregate"] = Declared + "SettingAggregate (Renamed)",
                ["GoneAggregate"] = LeftOut + "it was here once",
                [StampsFamily] = LeftOut + "a synthetic reason long enough to count",
            };

            var problems = Evaluate(found, accounted, new Dictionary<string, string>(), ["SettingAggregate (User)"]);

            Assert.Contains(problems, p => p.Contains("SettingAggregate (Renamed)", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.Contains("GoneAggregate", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.Contains(StampsFamily, StringComparison.Ordinal));
        }

        [Fact]
        public void A_clean_set_passes()
        {
            var found = new Dictionary<string, List<string>> { ["SettingAggregate"] = ["CreatedBy"], ["OtherAggregate"] = ["UserId"], ["StampedAggregate"] = ["ModifiedBy"] };
            var accounted = new Dictionary<string, string>
            {
                ["SettingAggregate"] = Declared + "SettingAggregate (User)",
                ["OtherAggregate"] = LeftOut + "a reason long enough to be one",
                [StampsFamily] = LeftOut + "a synthetic reason long enough to count",
            };

            Assert.Empty(Evaluate(found, accounted, new Dictionary<string, string> { ["X"] = Declared + "X store" }, ["SettingAggregate (User)", "X store"]));
        }

        // ---- the evaluation --------------------------------------------------------------------------------------------------

        private static List<string> Evaluate(IReadOnlyDictionary<string, List<string>> found, IReadOnlyDictionary<string, string> accounted, IReadOnlyDictionary<string, string> nonAggregate, IReadOnlyCollection<string> declaredLiterals)
        {
            var problems = new List<string>();
            var stampsCovered = 0;

            foreach (var (name, members) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (accounted.ContainsKey(name))
                {
                    continue;
                }

                if (members.All(StampMembers.Contains))
                {
                    if (accounted.ContainsKey(StampsFamily))
                    {
                        stampsCovered++;
                        continue;
                    }

                    problems.Add($"{name} carries only authorship stamps and there is no '{StampsFamily}' entry.");
                    continue;
                }

                problems.Add($"{name} holds {string.Join(", ", members)} and is neither declared by a participant nor left out for a reason.");
            }

            foreach (var (name, entry) in accounted.Concat(nonAggregate))
            {
                if (name == StampsFamily)
                {
                    if (stampsCovered == 0)
                    {
                        problems.Add($"'{StampsFamily}' covers nothing: no discovered aggregate carries only stamps.");
                    }
                }
                else if (!found.ContainsKey(name) && !nonAggregate.ContainsKey(name))
                {
                    problems.Add($"Stale entry: {name} is no such aggregate, or holds no identifying member any more.");
                }

                if (entry.StartsWith(Declared, StringComparison.Ordinal))
                {
                    foreach (var store in entry.Substring(Declared.Length).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!declaredLiterals.Contains(store))
                        {
                            problems.Add($"{name} -> '{store}' is declared by no participant (ErasureDeclaration.Delete/Anonymise/Retain(\"{store}\") not found in src/Stomme.Server).");
                        }
                    }
                }
                else if (!entry.StartsWith(LeftOut, StringComparison.Ordinal) || entry.Length <= LeftOut.Length + 10)
                {
                    problems.Add($"{name} -> '{entry}' is neither a declaration nor a stated reason.");
                }
            }

            return problems;
        }

        // ---- discovery -------------------------------------------------------------------------------------------------------

        private static readonly string[] StringSuffixes = ["Email", "Username", "UserName", "DisplayName", "IPAddress", "IpAddress", "RemoteAddress", "SubjectName", "IdentitySubject"];

        private static Dictionary<string, List<string>> IdentifyingAggregates()
        {
            var server = typeof(UserSettingsErasureParticipant).Assembly;
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var type in server.GetTypes().Where(IsAggregate))
            {
                var members = MembersOf(type, IsIdentifying)
                    .OrderBy(name => name, StringComparer.Ordinal)
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

        private static bool IsIdentifying(PropertyInfo property)
        {
            var isInt = property.PropertyType == typeof(int) || property.PropertyType == typeof(int?);
            var isString = property.PropertyType == typeof(string);
            if (isInt)
            {
                return property.Name.EndsWith("UserId", StringComparison.Ordinal);
            }

            if (!isString)
            {
                return false;
            }

            return StampMembers.Contains(property.Name)
                || StringSuffixes.Any(suffix => property.Name.EndsWith(suffix, StringComparison.Ordinal));
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

        private static readonly Regex DeclarationLiteral = new(@"ErasureDeclaration\.(?:Delete|Anonymise|Retain)\(\s*""([^""]+)""", RegexOptions.Compiled);

        private static HashSet<string> DeclaredStoreLiterals()
        {
            var literals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Stomme.Server"), "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in DeclarationLiteral.Matches(File.ReadAllText(file)))
                {
                    literals.Add(match.Groups[1].Value);
                }
            }

            Assert.True(literals.Count >= 15, "The source scan must find the participants' declarations; found " + literals.Count);
            return literals;
        }

        private static string? _repoRoot;

        private static string RepoRoot()
        {
            if (_repoRoot is not null)
            {
                return _repoRoot;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);
            return _repoRoot = directory!.FullName;
        }
    }
}
