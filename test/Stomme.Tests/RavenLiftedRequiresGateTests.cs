using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The RavenDB leg of the BL-99 driven gate: the SAME scenario
    /// (<see cref="LiftedRequiresGateTests.RunAsync"/>) against a DOCUMENT store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It runs the identical assertions rather than a paraphrase, because a parity claim only means something
    /// when both sides are asked the same question (D46). The seam's providers have diverged exactly where the
    /// compiler could not see it before - EF folds <c>-1 == -1</c> and RavenDB does not (0 of 2 rows, green on
    /// EF); bare <c>==</c> is case-SENSITIVE on EF and case-INSENSITIVE on Raven. A permission row is
    /// <c>(EntityName, EntityId = -1, PermissionName)</c>: every one of those hazards is in this decision's
    /// path, and the key halves are lower-cased strings.
    /// </para>
    /// <para>
    /// FAIL-NOT-SKIP. An unreachable RavenDB fails this test with a named message; it never quietly disappears.
    /// Point <c>RAVENDB_TEST_URL</c> at a reachable node (the shared infra UAT tier, unsecured on-LAN).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenLiftedRequiresGateTests
    {
        private readonly ITestOutputHelper _output;

        public RavenLiftedRequiresGateTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public Task A_lifted_requires_key_is_enforced_over_http_on_ravendb() =>
            LiftedRequiresGateTests.RunAsync(new RavenGateStore(), _output);
    }
}