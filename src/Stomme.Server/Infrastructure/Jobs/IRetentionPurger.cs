using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// A contributed retention pass, drained per site by <see cref="PurgeJob"/> after the framework's own passes
    /// (D104 design point 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all.</b> Before it, a module with rows to age out had exactly two options: its own
    /// <c>HostedServiceBase</c> job - a second Job row, a second schedule, a second thing an operator can disable
    /// without knowing what they switched off - or nothing. Retention is the same shape for every module (a per-site
    /// setting in whole days, a delete older than that, a line in the notes), so it belongs to the job that already
    /// does it five times.
    /// </para>
    /// <para>
    /// <b>The contract is deliberately narrow.</b> An implementation names the SETTING it is configured by and the
    /// default that applies when the site has no value; the job owns reading, parsing and defaulting that setting,
    /// so one module cannot produce a different answer to "what does an unparseable retention value mean" than the
    /// framework does. The implementation is handed whole days and a site id and deletes; it does not read
    /// settings, does not schedule, and does not decide when it runs.
    /// </para>
    /// <para>
    /// <b>Bounds belong to the implementation.</b> The job clamps nothing beyond refusing a negative value, because
    /// a floor or a ceiling is a statement about the DATA being purged (health rollups are worthless beyond a year;
    /// an audit trail may have a legal floor) and only the owning module knows it. An implementation that needs one
    /// clamps inside <see cref="PurgeAsync"/> and says so in its notes.
    /// </para>
    /// <para>
    /// <b>No cancellation token travels.</b> <c>HostedServiceBase.ExecuteJobAsync</c> takes no token (BL-107 gave
    /// the tenant LOOP the job's token, but not the body), so callers pass <see cref="CancellationToken.None"/>.
    /// The parameter is on the signature anyway: it costs nothing now and is the only way the purgers become
    /// cancellable when the body eventually gets a token, without touching every implementation.
    /// </para>
    /// <para>
    /// Registered by the owning module's <c>ServerStartup</c> as <c>IRetentionPurger</c>; the job resolves them with
    /// <c>GetServices</c>, so a module that ships none changes nothing.
    /// </para>
    /// </remarks>
    public interface IRetentionPurger
    {
        /// <summary>
        /// The per-site <c>Setting</c> key holding this pass's retention, in whole days (for example
        /// <c>HealthRetention</c>). Read by <see cref="PurgeJob"/>, never by the implementation.
        /// </summary>
        string SettingKey { get; }

        /// <summary>The retention used when the site has no value for <see cref="SettingKey"/>.</summary>
        int DefaultDays { get; }

        /// <summary>
        /// A short name for the job notes - what was purged, in the operator's words ("Health Rollups").
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Deletes this pass's rows for <paramref name="siteId"/> older than <paramref name="days"/> whole days,
        /// and returns how many rows went. Must not throw for an ordinary empty result; the job contains a throw
        /// per purger, but a purger that throws routinely hides its own failure in the notes.
        /// </summary>
        Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default);
    }
}
