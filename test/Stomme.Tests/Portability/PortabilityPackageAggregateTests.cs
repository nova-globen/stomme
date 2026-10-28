#nullable enable
using System;
using Stomme.Modules.Portability.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Portability
{
    /// <summary>BL-82 - the package's own invariants: who a package is about, and which transitions exist.</summary>
    public class PortabilityPackageAggregateTests
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_subject_package_names_its_subject_and_a_config_package_has_none()
        {
            Assert.Throws<ArgumentException>(() => PortabilityPackageAggregate.StartBuild(TenantId.Default, 1, PortabilityPackageKind.SubjectData, 3, null, Now));
            Assert.Throws<ArgumentException>(() => PortabilityPackageAggregate.StartBuild(TenantId.Default, 1, PortabilityPackageKind.ConfigTransfer, 3, 42, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => PortabilityPackageAggregate.StartBuild(TenantId.Default, 0, PortabilityPackageKind.ConfigTransfer, 3, null, Now));
        }

        [Fact]
        public void Only_a_building_package_becomes_ready_or_failed()
        {
            var package = PortabilityPackageAggregate.StartBuild(TenantId.Default, 1, PortabilityPackageKind.ConfigTransfer, 3, null, Now);
            package.MarkReady(9, "1.1", 1, "[]", Now, Now.AddDays(7));

            Assert.Throws<InvalidOperationException>(() => package.MarkReady(10, "1.1", 1, "[]", Now, Now.AddDays(7)));
            Assert.Throws<InvalidOperationException>(() => package.MarkFailed("X", Now, Now.AddDays(7)));
        }

        [Fact]
        public void A_failure_reason_is_bounded()
        {
            var package = PortabilityPackageAggregate.StartBuild(TenantId.Default, 1, PortabilityPackageKind.ConfigTransfer, 3, null, Now);
            package.MarkFailed(new string('x', 5000), Now, Now.AddDays(7));

            Assert.Equal(PortabilityPackageAggregate.MaximumFailureLength, package.FailureText.Length);
        }
    }
}
