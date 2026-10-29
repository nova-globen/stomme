#nullable enable
using System;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>
    /// One attempt to deliver a <see cref="NotificationRequestAggregate"/> over a single <see cref="ChannelKind"/>. A
    /// CHILD ENTITY (identity, never persisted on its own) - the instant is a UTC
    /// <see cref="AttemptedOn"/>. Persisted inside the request through the shared module store's
    /// JSON collection convention, identity included.
    /// </summary>
    public sealed class DeliveryAttempt : Entity<Guid>
    {
        private DateTime _attemptedOn;

        // Materialisation constructor. Not for application use.
        private DeliveryAttempt()
        {
        }

        internal DeliveryAttempt(Guid id, ChannelKind channel, DeliveryStatus status, DateTime attemptedOnUtc, string? error)
        {
            UtcInstant.Require(attemptedOnUtc, nameof(attemptedOnUtc));
            if (status == DeliveryStatus.Failed && string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("A failed delivery attempt must record an error.", nameof(error));
            }

            if (status == DeliveryStatus.Succeeded && !string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException("A successful delivery attempt cannot carry an error.", nameof(error));
            }

            Id = id;
            Channel = channel;
            Status = status;
            _attemptedOn = attemptedOnUtc;

            // Failed -> the error; Skipped -> an optional suppression reason; Succeeded -> none.
            Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        }

        public ChannelKind Channel { get; private set; }

        public DeliveryStatus Status { get; private set; }

        /// <summary>When the attempt was made (UTC).</summary>
        public DateTime AttemptedOn
        {
            get => UtcInstant.Stamp(_attemptedOn);
            private set => _attemptedOn = value;
        }

        /// <summary>The transport failure detail when Failed, or the suppression reason when Skipped; otherwise null.</summary>
        public string? Error { get; private set; }
    }
}
