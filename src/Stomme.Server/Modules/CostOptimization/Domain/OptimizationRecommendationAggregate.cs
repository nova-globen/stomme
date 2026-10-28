#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.CostOptimization;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.CostOptimization.Domain
{
    /// <summary>The advisor raised a new savings opportunity for a site.</summary>
    public sealed record RecommendationRaisedEvent(
        Guid RecommendationId,
        TenantId TenantId,
        int SiteId,
        RecommendationCategory Category,
        string RecommendationKey,
        decimal EstimatedSavingAmount,
        string EstimatedSavingCurrency) : DomainEvent;

    /// <summary>A recommendation was applied, recording the action taken.</summary>
    public sealed record RecommendationAppliedEvent(
        Guid RecommendationId,
        TenantId TenantId,
        int SiteId,
        string RecommendationKey,
        string ActionTaken) : DomainEvent;

    /// <summary>A recommendation was dismissed; the advisor suppresses its key until the cooldown ends.</summary>
    public sealed record RecommendationDismissedEvent(
        Guid RecommendationId,
        TenantId TenantId,
        int SiteId,
        string RecommendationKey,
        string Reason,
        DateTime DismissedUntilUtc) : DomainEvent;

    /// <summary>
    /// The projected saving of a recommendation (the predecessor platform's <c>EstimatedSaving</c>, lifted): a non-negative amount in a
    /// three-letter ISO 4217 code stored upper-case. Zero is valid - a hygiene finding is worth raising with no honest
    /// estimate. The aggregate stores its two scalars; this record is the one place their rules live.
    /// </summary>
    public sealed record EstimatedSaving
    {
        private EstimatedSaving(decimal amount, string currency)
        {
            Amount = amount;
            Currency = currency;
        }

        /// <summary>The projected saving amount; never negative.</summary>
        public decimal Amount { get; }

        /// <summary>ISO 4217 currency code, stored upper-case.</summary>
        public string Currency { get; }

        /// <summary>Creates a saving, enforcing the invariants (non-negative amount, ISO 4217 currency).</summary>
        public static EstimatedSaving Of(decimal amount, string currency)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "An estimated saving cannot be negative.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(currency);
            var trimmed = currency.Trim();
            if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
            {
                throw new ArgumentException($"Currency '{currency}' is not a three-letter ISO 4217 code.", nameof(currency));
            }

            return new EstimatedSaving(amount, trimmed.ToUpperInvariant());
        }
    }

    /// <summary>
    /// A concrete, evidence-cited savings opportunity the advisor raised (BL-97 increment 6, the predecessor platform's
    /// <c>OptimizationRecommendation</c>): it cites the observations it derives from (never none), carries a non-negative
    /// currency-qualified saving, and moves one way - Open, then Applied (the action taken recorded) or Dismissed (a reason
    /// and a cooldown, so the advisor does not nag). <see cref="RecommendationKey"/> is the finding's deterministic identity:
    /// the advisor deduplicates by key against open and cooling-down recommendations, while each raise stays its own
    /// aggregate so the decision history is kept.
    /// </summary>
    /// <remarks>
    /// <b>CHANGED from the predecessor platform:</b> the recommendation belongs to a SITE of its tenant - the signals it derives from are a
    /// site's (the cost ledger is per site, BL-97 default 2) - so a key is deduplicated per site; the saving is stored as
    /// two scalars (<see cref="EstimatedSavingAmount"/>, <see cref="EstimatedSavingCurrency"/>) validated through
    /// <see cref="EstimatedSaving"/>, because the module stores map scalars and value objects, not a nested record; and the
    /// instants are UTC <see cref="DateTime"/> values. <see cref="Evidence"/> rides the store's JSON collection column.
    /// </remarks>
    public sealed class OptimizationRecommendationAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<string> _evidence = new();

        // Materialisation constructor. Not for application use.
        private OptimizationRecommendationAggregate()
        {
            TenantId = null!;
            RecommendationKey = string.Empty;
            Summary = string.Empty;
            EstimatedSavingCurrency = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public RecommendationCategory Category { get; private set; }

        /// <summary>Deterministic identity of the finding, used to deduplicate re-raises of the same opportunity.</summary>
        public string RecommendationKey { get; private set; }

        /// <summary>One-line statement of the opportunity.</summary>
        public string Summary { get; private set; }

        /// <summary>The cited observations this recommendation derives from; never empty.</summary>
        public IReadOnlyList<string> Evidence => _evidence;

        /// <summary>The projected saving; never negative.</summary>
        public decimal EstimatedSavingAmount { get; private set; }

        /// <summary>The saving's ISO 4217 code, upper-case.</summary>
        public string EstimatedSavingCurrency { get; private set; }

        public RecommendationStatus Status { get; private set; }

        public DateTime RaisedUtc { get; private set; }

        /// <summary>The action taken when applying; set only when Applied.</summary>
        public string? AppliedAction { get; private set; }

        public DateTime? AppliedUtc { get; private set; }

        /// <summary>Why it was dismissed; set only when Dismissed.</summary>
        public string? DismissReason { get; private set; }

        public DateTime? DismissedUtc { get; private set; }

        /// <summary>End of the dismissal cooldown: the advisor does not re-raise <see cref="RecommendationKey"/> before it.</summary>
        public DateTime? DismissedUntilUtc { get; private set; }

        /// <summary>
        /// Raises a recommendation, enforcing the invariants: a site, a defined category, a non-blank key and summary, at
        /// least one non-blank evidence line, and a valid saving.
        /// </summary>
        public static OptimizationRecommendationAggregate Raise(
            Guid id,
            TenantId tenantId,
            int siteId,
            RecommendationCategory category,
            string recommendationKey,
            string summary,
            IReadOnlyList<string> evidence,
            EstimatedSaving estimatedSaving,
            DateTime raisedUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A recommendation belongs to a site.");
            }

            if (!Enum.IsDefined(category))
            {
                throw new ArgumentOutOfRangeException(nameof(category), $"'{category}' is not a known recommendation category.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(recommendationKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(summary);
            ArgumentNullException.ThrowIfNull(estimatedSaving);
            ArgumentNullException.ThrowIfNull(evidence);
            if (evidence.Count == 0 || evidence.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("A recommendation must cite at least one non-blank evidence line.", nameof(evidence));
            }

            var recommendation = new OptimizationRecommendationAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Category = category,
                RecommendationKey = recommendationKey.Trim(),
                Summary = summary.Trim(),
                EstimatedSavingAmount = estimatedSaving.Amount,
                EstimatedSavingCurrency = estimatedSaving.Currency,
                Status = RecommendationStatus.Open,
                RaisedUtc = raisedUtc,
            };
            recommendation._evidence.AddRange(evidence.Select(line => line.Trim()));
            recommendation.RaiseDomainEvent(new RecommendationRaisedEvent(
                recommendation.Id,
                recommendation.TenantId,
                recommendation.SiteId,
                recommendation.Category,
                recommendation.RecommendationKey,
                recommendation.EstimatedSavingAmount,
                recommendation.EstimatedSavingCurrency));
            return recommendation;
        }

        /// <summary>Applies an open recommendation, recording the action taken.</summary>
        public void Apply(string actionTaken, DateTime appliedUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actionTaken);
            if (Status != RecommendationStatus.Open)
            {
                throw new InvalidOperationException($"Only an open recommendation can be applied (current status: {Status}).");
            }

            Status = RecommendationStatus.Applied;
            AppliedAction = actionTaken.Trim();
            AppliedUtc = appliedUtc;
            RaiseDomainEvent(new RecommendationAppliedEvent(Id, TenantId, SiteId, RecommendationKey, AppliedAction));
        }

        /// <summary>Dismisses an open recommendation with a reason and a positive cooldown during which its key is not re-raised.</summary>
        public void Dismiss(string reason, DateTime dismissedUtc, TimeSpan cooldown)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (cooldown <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(cooldown), "A dismissal must carry a positive cooldown window.");
            }

            if (Status != RecommendationStatus.Open)
            {
                throw new InvalidOperationException($"Only an open recommendation can be dismissed (current status: {Status}).");
            }

            Status = RecommendationStatus.Dismissed;
            DismissReason = reason.Trim();
            DismissedUtc = dismissedUtc;
            DismissedUntilUtc = dismissedUtc + cooldown;
            RaiseDomainEvent(new RecommendationDismissedEvent(Id, TenantId, SiteId, RecommendationKey, DismissReason, DismissedUntilUtc.Value));
        }

        /// <summary>Whether this dismissal still suppresses re-raising <see cref="RecommendationKey"/> at <paramref name="atUtc"/>.</summary>
        public bool IsInDismissCooldown(DateTime atUtc) =>
            Status == RecommendationStatus.Dismissed && DismissedUntilUtc is { } until && atUtc < until;
    }
}
