using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// BL-100 O2 — the daily dead-letter sweep ported from the predecessor platform's Operations module: discards dead letters that
    /// became visible in the dead-letter queue more than <c>Operations:DeadLetterSweep:RetentionDays</c> (default 30) ago.
    /// The aging logic is <see cref="DeadLetterSweeper"/>; this class is its scheduling and configuration.
    /// </summary>
    /// <remarks>
    /// <para><b>Off twice by default.</b> The auto-registered Job row is <c>IsEnabled=false</c>, so a stock install
    /// runs nothing and writes no job-log rows; and even with the row enabled the pass is a no-op unless
    /// <c>Operations:DeadLetterSweep:Enabled</c> is true. Discarding is permanent bus surgery, so both switches must be on.</para>
    /// <para><b>A stated no-op unless <c>Messaging:Transport=ravendb</c>.</b> Queues arrive through
    /// <c>GetServices</c>, never <c>GetRequiredService</c>: with messaging disabled nothing is registered and a required
    /// resolve would fail every tick inside <see cref="HostedServiceBase"/>'s single catch. Every other transport carries
    /// only the null fallback. <c>RavenDeadLetterSweepLiveTests</c> (Stomme.Persistence.Raven.Tests) is the only real
    /// cover of a discard against a real queue; <c>DeadLetterSweepJobTests</c> drive this class over a scripted port.</para>
    /// <para><b>Idempotent across the per-tenant loop.</b> <see cref="HostedServiceBase"/> calls
    /// <see cref="ExecuteJobAsync"/> once per tenant; the queue is installation-wide, so the second pass discards 0.</para>
    /// <para>Configuration is read from <see cref="IConfigurationRoot"/>, not <see cref="IConfiguration"/>: only the root
    /// carries the override file (the trap <c>DeadLetterController</c> records).</para>
    /// </remarks>
    public class DeadLetterSweepJob : HostedServiceBase
    {
        // JobType = "Stomme.Infrastructure.DeadLetterSweepJob, Stomme.Server"

        public const string EnabledKey = "Operations:DeadLetterSweep:Enabled";
        public const string RetentionDaysKey = "Operations:DeadLetterSweep:RetentionDays";
        public const int DefaultRetentionDays = 30;

        public DeadLetterSweepJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Dead Letter Sweep Job";
            Frequency = "d"; // daily
            Interval = 1;
            IsEnabled = false;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var configuration = provider.GetRequiredService<IConfigurationRoot>();
            if (!configuration.GetValue(EnabledKey, false))
            {
                return $"<br />Dead-letter sweep disabled by configuration ({EnabledKey} is not true) - no-op.<br />";
            }

            var retentionDays = configuration.GetValue(RetentionDaysKey, DefaultRetentionDays);
            var result = await DeadLetterSweeper.SweepAsync(
                provider.GetServices<IDeadLetterQueue>(),
                DateTimeOffset.UtcNow,
                TimeSpan.FromDays(retentionDays));

            return "<br />" + result.Note + "<br />";
        }
    }
}