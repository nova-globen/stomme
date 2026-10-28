namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Host-tunable knobs for the message bus. Transport selection itself is a separate seam (the
/// <c>transport</c> configurator passed to <see cref="MessagingServiceExtensions.AddMessaging"/>); these
/// are the cross-transport settings.
/// </summary>
public sealed class MessagingOptions
{
    /// <summary>The bus's own input queue (the address it receives on).</summary>
    public string InputQueue { get; set; } = "stomme";

    /// <summary>
    /// Config-selected broker transport when the host does not supply one explicitly: <c>rabbitmq</c>,
    /// <c>azureservicebus</c>, or <c>inmemory</c> (default; also used for null/empty). The RavenDB transport
    /// is selected by the host passing its configurator (it needs the shared document store) — the Stomme
    /// host's <c>AddStommeMessaging</c> maps <c>Messaging:Transport = "ravendb"</c> to that configurator.
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>Connection string for a broker transport (e.g. the RabbitMQ AMQP URI).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Where messages that exhaust their delivery attempts are moved (the dead-letter queue).</summary>
    public string ErrorQueue { get; set; } = "stomme.error";

    /// <summary>
    /// Immediate delivery attempts before a message is dead-lettered. The delayed-retry schedule
    /// (second-level retries) is layered on separately.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>Worker threads. Defaults to <see cref="Environment.ProcessorCount"/> (minimum 2).</summary>
    public int? NumberOfWorkers { get; set; }

    /// <summary>Max in-flight messages across workers. Defaults to the worker count.</summary>
    public int? MaxParallelism { get; set; }

    /// <summary>
    /// Maximum number of messages held in the in-memory transport-fallback buffer while the broker is
    /// unreachable. Once the buffer is full, <see cref="ResilientEventBus"/> rejects new messages with an
    /// exception. Default: 1000. The buffer is in-process only — messages are lost on host restart.
    /// </summary>
    public int BufferCapacity { get; set; } = 1000;

    internal int EffectiveWorkers => Math.Max(2, NumberOfWorkers ?? Environment.ProcessorCount);

    internal int EffectiveParallelism => Math.Max(2, MaxParallelism ?? EffectiveWorkers);
}
