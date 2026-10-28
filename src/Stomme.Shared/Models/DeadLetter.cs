using System;
using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// The dead-letter admin surface's status — answers even when the bus is off, so the UI can
    /// distinguish "messaging disabled" from "this transport keeps no dead-letter queue" from "a real
    /// queue with N messages" (mirrors the OpenIddict status pattern).
    /// </summary>
    public class DeadLetterStatus
    {
        /// <summary>
        /// Whether the message bus is enabled for this install (Messaging:Enabled)
        /// </summary>
        public bool MessagingEnabled { get; set; }

        /// <summary>
        /// Whether a real dead-letter queue backs this install (false = the Null port; the in-memory,
        /// RabbitMQ and Azure Service Bus transports keep no admin-visible DLQ today)
        /// </summary>
        public bool HasDeadLetterQueue { get; set; }

        /// <summary>
        /// The backing implementation's type name, for display
        /// </summary>
        public string Backend { get; set; }

        /// <summary>
        /// Messages currently dead-lettered (0 when no real queue)
        /// </summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// A message that exhausted its delivery attempts — the client-side shape of the server's
    /// <c>Stomme.EventBus.DeadLetterMessage</c>.
    /// </summary>
    public class DeadLetterItem
    {
        /// <summary>
        /// Transport identifier used to retry or discard the message
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// The message's .NET type name, if known
        /// </summary>
        public string MessageType { get; set; }

        /// <summary>
        /// The captured failure details (last error), if any
        /// </summary>
        public string ErrorDetails { get; set; }

        /// <summary>
        /// When the message entered the dead-letter queue (UTC)
        /// </summary>
        public DateTimeOffset EnqueuedUtc { get; set; }

        /// <summary>
        /// The message's transport headers
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }
}
