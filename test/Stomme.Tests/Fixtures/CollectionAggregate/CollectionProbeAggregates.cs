using System;
using System.Collections.Generic;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

// Deliberately OUTSIDE Stomme.Modules.* so ModuleAggregateProviderNeutralityTests does not discover these
// test-only aggregates as shipped module aggregates.
namespace Stomme.Tests.Fixtures.CollectionAggregate
{
    /// <summary>The scalar element of shape (i): an enum list.</summary>
    public enum CollectionProbeKind
    {
        Email = 0,
        InApp = 1,
        Webhook = 2,
    }

    /// <summary>Shape (iii): a keyless sealed record element (NotificationParameter's shape).</summary>
    public sealed record CollectionProbeParameter(string Name, string Value);

    /// <summary>A bounded string value object (the <c>Locale</c> shape) used inside a record element.</summary>
    public sealed class CollectionProbeLocale : ValueObject, IStringValueObject
    {
        public const int MaxLength = 16;

        private CollectionProbeLocale(string value) => Value = value;

        public string Value { get; }

        public static CollectionProbeLocale Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
            {
                throw new ArgumentException("A locale is 1..16 characters.", nameof(value));
            }

            return new CollectionProbeLocale(value.Trim());
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }
    }

    /// <summary>Shape (iv): a record whose member is an IStringValueObject (LocalizedContent's shape).</summary>
    public sealed record CollectionProbeLocalizedContent(CollectionProbeLocale Locale, string Content);

    /// <summary>
    /// Shape (v): an <see cref="Entity{TId}"/> child with identity, an internal constructor, private setters and a
    /// UTC <see cref="DateTime"/> (DeliveryAttempt's shape, with DateTimeOffset replaced by UTC DateTime).
    /// </summary>
    public sealed class CollectionProbeAttempt : Entity<Guid>
    {
        // Materialisation constructor. Not for application use.
        private CollectionProbeAttempt()
        {
        }

        internal CollectionProbeAttempt(Guid id, CollectionProbeKind channel, DateTime attemptedOnUtc, string? error)
        {
            Id = id;
            Channel = channel;
            AttemptedOnUtc = attemptedOnUtc;
            Error = error;
        }

        public CollectionProbeKind Channel { get; private set; }

        public DateTime AttemptedOnUtc { get; private set; }

        public string? Error { get; private set; }
    }

    /// <summary>
    /// The collection-bearing aggregate: one property of each of the five element shapes, each exposed as
    /// <c>IReadOnlyList&lt;T&gt; X =&gt; _x;</c> over a private backing list, plus a non-string scalar
    /// (<see cref="Bucket"/>) for a provider-neutral FindAsync.
    /// </summary>
    public sealed class CollectionBearingAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<CollectionProbeKind> _kinds = [];
        private readonly List<string> _tags = [];
        private readonly List<CollectionProbeParameter> _parameters = [];
        private readonly List<CollectionProbeLocalizedContent> _contents = [];
        private readonly List<CollectionProbeAttempt> _attempts = [];

        // Materialisation constructor. Not for application use.
        private CollectionBearingAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public int Bucket { get; private set; }

        public string Title { get; private set; } = string.Empty;

        public IReadOnlyList<CollectionProbeKind> Kinds => _kinds;

        public IReadOnlyList<string> Tags => _tags;

        public IReadOnlyList<CollectionProbeParameter> Parameters => _parameters;

        public IReadOnlyList<CollectionProbeLocalizedContent> Contents => _contents;

        public IReadOnlyList<CollectionProbeAttempt> Attempts => _attempts;

        public static CollectionBearingAggregate Create(int bucket, string title)
        {
            return new CollectionBearingAggregate { Id = Guid.NewGuid(), Bucket = bucket, Title = title };
        }

        public static CollectionBearingAggregate CreateFull(int bucket, string title)
        {
            var aggregate = Create(bucket, title);
            aggregate._kinds.AddRange([CollectionProbeKind.Webhook, CollectionProbeKind.Email]);
            aggregate._tags.AddRange(["alpha", "beta, with a comma", "\"quoted\""]);
            aggregate._parameters.AddRange([new("name", "Ada"), new("html", "<script>alert(1)</script>")]);
            aggregate._contents.AddRange(
            [
                new(CollectionProbeLocale.Create("en-US"), "Hello {{ name }}"),
                new(CollectionProbeLocale.Create("sv-SE"), "Hej {{ name }}"),
            ]);
            aggregate.RecordAttempt(CollectionProbeKind.Email, new DateTime(2026, 9, 14, 8, 30, 15, 123, DateTimeKind.Utc), null);
            aggregate.RecordAttempt(CollectionProbeKind.Webhook, new DateTime(2026, 9, 14, 8, 31, 0, DateTimeKind.Utc), "HTTP 503");
            return aggregate;
        }

        public void RecordAttempt(CollectionProbeKind channel, DateTime attemptedOnUtc, string? error)
        {
            _attempts.Add(new CollectionProbeAttempt(Guid.NewGuid(), channel, attemptedOnUtc, error));
        }

        public void AddTag(string tag) => _tags.Add(tag);
    }

    // ---- Single-shape aggregates: the measurement probes. One shape each, so a model-build throw from one
    // ---- shape cannot hide what happens to another.

    public sealed class EnumListProbeAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<CollectionProbeKind> _items = [];

        private EnumListProbeAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public IReadOnlyList<CollectionProbeKind> Items => _items;

        public static EnumListProbeAggregate Create()
        {
            var aggregate = new EnumListProbeAggregate { Id = Guid.NewGuid() };
            aggregate._items.AddRange([CollectionProbeKind.Webhook, CollectionProbeKind.Email]);
            return aggregate;
        }
    }

    public sealed class StringListProbeAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<string> _items = [];

        private StringListProbeAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public IReadOnlyList<string> Items => _items;

        public static StringListProbeAggregate Create()
        {
            var aggregate = new StringListProbeAggregate { Id = Guid.NewGuid() };
            aggregate._items.AddRange(["alpha", "beta"]);
            return aggregate;
        }
    }

    public sealed class RecordListProbeAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<CollectionProbeParameter> _items = [];

        private RecordListProbeAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public IReadOnlyList<CollectionProbeParameter> Items => _items;

        public static RecordListProbeAggregate Create()
        {
            var aggregate = new RecordListProbeAggregate { Id = Guid.NewGuid() };
            aggregate._items.AddRange([new("name", "Ada"), new("topic", "runs")]);
            return aggregate;
        }
    }

    public sealed class ValueObjectRecordListProbeAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<CollectionProbeLocalizedContent> _items = [];

        private ValueObjectRecordListProbeAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public IReadOnlyList<CollectionProbeLocalizedContent> Items => _items;

        public static ValueObjectRecordListProbeAggregate Create()
        {
            var aggregate = new ValueObjectRecordListProbeAggregate { Id = Guid.NewGuid() };
            aggregate._items.AddRange(
            [
                new(CollectionProbeLocale.Create("en-US"), "Hello"),
                new(CollectionProbeLocale.Create("sv-SE"), "Hej"),
            ]);
            return aggregate;
        }
    }

    public sealed class EntityListProbeAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<CollectionProbeAttempt> _items = [];

        private EntityListProbeAggregate()
        {
        }

        public TenantId TenantId { get; private set; } = TenantId.Default;

        public IReadOnlyList<CollectionProbeAttempt> Items => _items;

        public static EntityListProbeAggregate Create()
        {
            var aggregate = new EntityListProbeAggregate { Id = Guid.NewGuid() };
            aggregate._items.Add(new CollectionProbeAttempt(
                Guid.NewGuid(), CollectionProbeKind.Email, new DateTime(2026, 9, 14, 8, 30, 15, DateTimeKind.Utc), null));
            aggregate._items.Add(new CollectionProbeAttempt(
                Guid.NewGuid(), CollectionProbeKind.Webhook, new DateTime(2026, 9, 14, 8, 31, 0, DateTimeKind.Utc), "HTTP 503"));
            return aggregate;
        }
    }
}