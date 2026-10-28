namespace Stomme.Persistence.RavenDb.Messaging;

/// <summary>
/// A queued transport message persisted as a RavenDB document. <see cref="Queue"/> is the logical
/// destination/input queue, <see cref="VisibleUtc"/> supports native deferral (a message stays
/// invisible until then), and <see cref="LeasedUntilTicks"/> is the lease a receiver holds while it
/// processes the message — a crashed receiver's lease auto-expires so the message is redelivered
/// (at-least-once).
/// </summary>
internal sealed class RavenTransportMessage
{
    public string Id { get; set; } = default!;

    public string Queue { get; set; } = default!;

    public Dictionary<string, string> Headers { get; set; } = [];

    public byte[] Body { get; set; } = [];

    public DateTime VisibleUtc { get; set; }

    public long LeasedUntilTicks { get; set; }
}

/// <summary>A persisted Rebus subscription: one subscriber's interest in one topic.</summary>
internal sealed class RavenSubscription
{
    public string Id { get; set; } = default!;

    public string Topic { get; set; } = default!;

    public string Address { get; set; } = default!;
}
