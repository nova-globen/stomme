#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.Modules.Portability.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// BL-82 (D107 item 5, refutation 3) - the portability aggregates keep the shape every module-aggregate store maps
    /// without surprises: a stamped <c>TenantId</c> beside a <c>SiteId</c>; members that are scalars, enums, UTC
    /// <c>DateTime</c>s or <see cref="IStringValueObject"/> value objects; no collections (the shared EF context has no
    /// owned-collection mapping); no <c>DateTimeOffset</c> (the UTC converter is for <c>DateTime</c>); no computed
    /// properties (a getter with no setter is a column on one provider and nothing on another).
    /// </summary>
    public class PortabilityAggregateShapeTests
    {
        public static TheoryData<Type> Aggregates() => new() { typeof(PortabilityPackageAggregate), typeof(PortabilityAuditEntryAggregate) };

        [Theory]
        [MemberData(nameof(Aggregates))]
        public void The_aggregate_has_the_mappable_shape(Type aggregate) => Assert.Empty(Violations(aggregate));

        // The scanner, validated the other way: a planted aggregate breaking every rule is caught on every rule.
        [Fact]
        public void A_non_conforming_aggregate_is_caught()
        {
            var violations = Violations(typeof(PlantedNonConformingAggregate));

            Assert.Contains(violations, v => v.Contains("TenantId", StringComparison.Ordinal));
            Assert.Contains(violations, v => v.Contains("SiteId", StringComparison.Ordinal));
            Assert.Contains(violations, v => v.Contains(nameof(PlantedNonConformingAggregate.Lines), StringComparison.Ordinal));
            Assert.Contains(violations, v => v.Contains(nameof(PlantedNonConformingAggregate.At), StringComparison.Ordinal));
            Assert.Contains(violations, v => v.Contains(nameof(PlantedNonConformingAggregate.Computed), StringComparison.Ordinal));
            Assert.Contains(violations, v => v.Contains(nameof(PlantedNonConformingAggregate.Owner), StringComparison.Ordinal));
        }

        private static List<string> Violations(Type aggregate)
        {
            var violations = new List<string>();
            var properties = aggregate.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.DeclaringType == aggregate)
                .ToList();

            if (!properties.Any(property => property.Name == "TenantId" && property.PropertyType == typeof(TenantId)))
            {
                violations.Add($"{aggregate.Name} has no TenantId member.");
            }

            if (!properties.Any(property => property.Name == "SiteId" && property.PropertyType == typeof(int)))
            {
                violations.Add($"{aggregate.Name} has no int SiteId member.");
            }

            foreach (var property in properties)
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (property.SetMethod is null)
                {
                    violations.Add($"{aggregate.Name}.{property.Name} is computed (no setter).");
                    continue;
                }

                if (type == typeof(DateTimeOffset))
                {
                    violations.Add($"{aggregate.Name}.{property.Name} is a DateTimeOffset.");
                }
                else if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
                {
                    violations.Add($"{aggregate.Name}.{property.Name} is a collection.");
                }
                else if (!(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
                    || type == typeof(Guid) || type == typeof(TenantId) || typeof(IStringValueObject).IsAssignableFrom(type)))
                {
                    violations.Add($"{aggregate.Name}.{property.Name} is a {type.Name}, which is not a scalar, an enum or an IStringValueObject.");
                }
            }

            return violations;
        }

        private sealed class PlantedNonConformingAggregate
        {
            public List<string> Lines { get; set; } = new();

            public DateTimeOffset At { get; set; }

            public int Computed => Lines.Count;

            public Uri? Owner { get; set; }
        }
    }
}
