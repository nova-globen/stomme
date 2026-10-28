using System;
using System.Linq;
using System.Reflection;
using Stomme.Metering;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// D102: the recording port must not let a caller name the subject, the site or the time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the defect the predecessor platform shipped, and it is not visible in a behaviour test: its recorder honoured the
    /// caller's tenant verbatim and accepted a user id and a timestamp, so a producer could charge another user's
    /// quota, write into another site, or backdate consumption out of the window that a repair pass would later
    /// recompute. The shape is asserted REFLECTIVELY, because the only durable guarantee is that the members do
    /// not exist at all - a rule in prose would be re-added by the next lift.
    /// </para>
    /// <para>
    /// Validated in both directions: a planted record carrying a UserId is detected by the same scan.
    /// </para>
    /// </remarks>
    public class MeteringPortShapeTests
    {
        private static readonly string[] ForbiddenMembers =
        {
            "UserId", "User", "SiteId", "Site", "TenantId", "Tenant",
            "OccurredOnUtc", "OccurredAtUtc", "Timestamp", "When", "WhenUtc", "RecordedAtUtc"
        };

        public static TheoryData<Type> PortTypes() => new TheoryData<Type>
        {
            typeof(UsageEvent),
            typeof(AiUsageEvent)
        };

        [Theory]
        [MemberData(nameof(PortTypes))]
        public void A_recording_event_carries_no_subject_no_site_and_no_clock(Type portType)
        {
            var offenders = Offenders(portType);
            Assert.True(
                offenders.Length == 0,
                $"{portType.Name} exposes {string.Join(", ", offenders)}. The server derives the user (ICurrentUser), " +
                "the site (the resolved alias) and the time (the clock); a caller may only say what was consumed.");
        }

        /// <summary>The scan is not vacuous: the same rule catches a type that does carry a subject.</summary>
        [Fact]
        public void The_scan_detects_a_forgeable_event()
        {
            var offenders = Offenders(typeof(ForgeableProbe));
            Assert.Contains("UserId", offenders);
            Assert.Contains("OccurredOnUtc", offenders);
            Assert.NotEmpty(Offenders(typeof(ForgeableProbe)));
        }

        /// <summary>The quota inspector answers about the CALLER; it takes no subject either.</summary>
        [Fact]
        public void The_quota_inspector_takes_no_subject()
        {
            var evaluate = typeof(IQuotaInspector).GetMethod(nameof(IQuotaInspector.EvaluateAsync));
            Assert.NotNull(evaluate);
            var parameters = evaluate!.GetParameters().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "metric", "quantity", "ct" }, parameters);
        }

        /// <summary>
        /// The port is dependency-free: it lives in Stomme.Shared and names nothing from the module, so a
        /// producer can reference it without taking the metering domain with it.
        /// </summary>
        [Fact]
        public void The_port_lives_in_shared_and_names_nothing_outside_it()
        {
            var portAssembly = typeof(IUsageRecorder).Assembly;
            Assert.Equal("Stomme.Shared", portAssembly.GetName().Name);

            var surface = new[] { typeof(IUsageRecorder), typeof(IAiUsageRecorder), typeof(IQuotaInspector), typeof(IMeteredLimitProvider), typeof(UsageEvent), typeof(AiUsageEvent), typeof(UsageOutcome), typeof(AiUsageOutcome), typeof(QuotaDecision) };
            foreach (var type in surface)
            {
                foreach (var member in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var referenced = member.GetParameters().Select(p => p.ParameterType).Append(member.ReturnType);
                    foreach (var dependency in referenced.Select(Unwrap))
                    {
                        Assert.True(
                            dependency.Assembly == portAssembly || dependency.Assembly == typeof(object).Assembly || dependency.Assembly.GetName().Name!.StartsWith("System"),
                            $"{type.Name}.{member.Name} exposes {dependency.FullName} from {dependency.Assembly.GetName().Name}; the port must stay dependency-free.");
                    }
                }
            }
        }

        private static string[] Offenders(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Where(name => ForbiddenMembers.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name)
                .ToArray();

        private static Type Unwrap(Type type) =>
            type.IsGenericType ? Unwrap(type.GetGenericArguments()[0]) : type;

        /// <summary>The known-dirty control: what the predecessor platform's event looked like.</summary>
        private sealed record ForgeableProbe(string Metric, long Quantity, int? UserId, DateTime? OccurredOnUtc);
    }
}
