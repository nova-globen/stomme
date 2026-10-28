using System.Threading.Channels;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// A log entry that has been fully enriched on the CALLING thread and is waiting to be written to the
    /// store by <see cref="LogDrainService"/>.
    /// </summary>
    /// <param name="Log">The log record, already enriched and level-filtered.</param>
    /// <param name="RoutingAlias">
    /// The caller's ambient alias EXACTLY as <c>SiteState</c> held it, including the "mock" alias that
    /// <c>ITenantManager.SetTenant</c> installs (<c>AliasId == -1</c>). The drain has no request scope of its
    /// own, so this is what tells it WHICH TENANT'S DATABASE to write to. Null means there was no ambient
    /// tenant and the entry cannot be routed.
    /// </param>
    /// <param name="DisplayAlias">
    /// The same alias, but null when it is a mock (<c>AliasId == -1</c>) — mirroring the guard in
    /// <c>TenantManager.GetAlias</c>. This is the one used for human-facing text (notification subject/body),
    /// where a mock alias has no meaningful <c>Name</c>.
    /// </param>
    internal sealed record LogEntry(Log Log, Alias RoutingAlias, Alias DisplayAlias);

    /// <summary>
    /// The bounded hand-off between <see cref="ILogManager.Log(Log)"/> (synchronous, on the request thread)
    /// and <see cref="LogDrainService"/> (asynchronous, writes the store).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS (D34). <c>ILogManager.Log</c> is called from ~443 sites, many of them synchronous, and
    /// its body used to write the Log table and send notifications inline. Once the repositories became async
    /// (D33), that body could only be satisfied by blocking — the exact sync-over-async shape D29 shipped a
    /// dead master with. Making <c>Log</c> async instead would have pushed an await into all 443 call sites.
    /// Buffering removes the dilemma: the sync surface no longer touches a store at all, so it is
    /// synchronous LEGITIMATELY rather than by exemption. That is the point of this class, not a loophole.
    /// </para>
    /// <para>
    /// <see cref="BoundedChannelFullMode.Wait"/> with <see cref="ChannelWriter{T}.TryWrite"/> — NOT a Drop
    /// mode. The drop modes make <c>TryWrite</c> always return true and discard silently, so a saturated
    /// queue would lose logs with nothing to show for it. <c>Wait</c> makes <c>TryWrite</c> return FALSE when
    /// the channel is full, which lets <see cref="LogManager"/> fall back to the file logger on the calling
    /// thread. Nothing blocks and nothing is silently dropped under backpressure. (<c>WriteAsync</c> is the
    /// call that would wait; we never use it.)
    /// </para>
    /// <para>
    /// Loss is therefore bounded to a HARD CRASH losing what is still in the channel — accepted, because the
    /// durable audit trail is a separate mechanism (FeatureFlagAudit/IdentityAudit via domain-event
    /// handlers), not this table. Graceful shutdown flushes; see <see cref="LogDrainService.StopAsync"/>.
    /// </para>
    /// </remarks>
    public sealed class LogQueue
    {
        // Deep enough to absorb a burst (a request storm, a job iterating tenants) without reaching the file
        // logger, small enough to bound what a hard crash can lose.
        private const int Capacity = 10_000;

        private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(Capacity)
            {
                // See the remarks: Wait is what makes TryWrite report saturation instead of dropping.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,  // exactly one LogDrainService
                SingleWriter = false, // every request thread writes
            });

        internal ChannelReader<LogEntry> Reader => _channel.Reader;

        /// <summary>
        /// Queues an entry. Returns false when the channel is saturated — the caller must NOT block; it
        /// should fall back to the file logger.
        /// </summary>
        internal bool TryWrite(LogEntry entry) => _channel.Writer.TryWrite(entry);

        /// <summary>
        /// Signals that no more entries will be queued, so the drain loop finishes once it has emptied the
        /// channel. Called on shutdown.
        /// </summary>
        internal void Complete() => _channel.Writer.TryComplete();
    }
}
