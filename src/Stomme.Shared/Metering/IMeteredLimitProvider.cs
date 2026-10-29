using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Metering
{
    /// <summary>
    /// Supplies the numeric limit for a metric on a site - the entitlement channel a boolean cannot carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The BL-79 row said "entitlements read the same quotas". That is backwards, and it was the first thing the
    /// survey falsified: <c>IEntitlementProvider</c> answers a single boolean ("is this capability entitled?"), so
    /// it cannot say "500 calls a month". A licence that grants an ALLOWANCE therefore implements THIS port, and
    /// the quota engine asks it for the number.
    /// </para>
    /// <para>
    /// Three answers, and they are different: a number is the limit; <c>-1</c> means unlimited and is decoded in
    /// exactly one place, the quota limit resolver; <c>null</c> means "no opinion", and the module's own default
    /// for that metric applies. Stomme ships NO implementation - BL-2's offline licence is the first one - so
    /// with none registered every site gets the module defaults, which is the historical behaviour.
    /// </para>
    /// </remarks>
    public interface IMeteredLimitProvider
    {
        /// <summary>The limit for <paramref name="metric"/> on <paramref name="siteId"/>, -1 unlimited, null no opinion.</summary>
        Task<long?> GetLimitAsync(string metric, int siteId, CancellationToken ct = default);
    }
}
