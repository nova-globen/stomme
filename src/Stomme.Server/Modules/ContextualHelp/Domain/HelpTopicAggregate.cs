#nullable enable
using System;
using Stomme.Help;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.ContextualHelp.Domain
{
    /// <summary>
    /// One contextual-help topic, for one SITE, one anchor and one locale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero value objects and zero collections, deliberately.</b> Plain <c>record</c> types posing as
    /// "value objects" would not be <c>ValueObject</c>s, so they would fall straight
    /// through to EF's conventions inside an initializer whose error is SWALLOWED - a missed reshape would read as
    /// empty rather than fail. Anchor and locale are plain normalized STRINGS, which also removes the value-object
    /// <c>==</c> query that was refused (no precedent in this repository, and three different answers across
    /// the providers).
    /// </para>
    /// <para>
    /// <b>The id is DETERMINISTIC</b> (<see cref="HelpTopicIds.For"/>), which is what makes the boot reconciler safe to
    /// run every time: there is no unique index on any provider, so a reconciler that looked a topic up by its fields
    /// and created it when it missed would accumulate a row per boot.
    /// </para>
    /// <para>
    /// <b>Ownership, and why a seed update is so narrow.</b> A seed update that gated only on
    /// <c>IsSeeded</c> and never read <c>SeedContributorId</c> would let any contributor rewrite any
    /// other's topic; and one that bumped <c>ContentVersion</c> and the timestamp on EVERY call would, every
    /// boot, rewrite every seeded row and invalidate its cache key. Here a seed update applies only while the row is
    /// STILL seeded, only to the contributor that owns it, and only when the text has actually CHANGED.
    /// </para>
    /// <para>
    /// The body is bounded in CHARACTERS and the column is not: 32 000 is not a valid <c>nvarchar</c> length on SQL
    /// Server, so there is no <c>MaxLength</c> constant here for anyone to map.
    /// </para>
    /// </remarks>
    public sealed class HelpTopicAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumTitleLength = HelpTopicContribution.MaximumTitleLength;

        private DateTime _createdOnUtc;
        private DateTime _updatedOnUtc;

        // Materialisation constructor. Not for application use.
        private HelpTopicAggregate()
        {
            TenantId = null!;
            Anchor = string.Empty;
            Locale = string.Empty;
            Title = string.Empty;
            Markdown = string.Empty;
            SeedContributorId = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>Normalized. The key half of the deterministic id, never re-derived from it.</summary>
        public string Anchor { get; private set; }

        /// <summary>Normalized BCP-47 tag. The other key half.</summary>
        public string Locale { get; private set; }

        public string Title { get; private set; }

        /// <summary>The body as MARKDOWN. HTML is produced at READ, server-side, through the sanitiser - never stored.</summary>
        public string Markdown { get; private set; }

        /// <summary>
        /// Bumped only when the text actually changes. It is part of the cache key, so bumping it on an unchanged
        /// boot would invalidate every topic's cache for nothing.
        /// </summary>
        public int ContentVersion { get; private set; }

        /// <summary>A draft is invisible to every read path but the <c>help:manage</c> list.</summary>
        public bool IsPublished { get; private set; }

        /// <summary>True while the row is still the contributor's to update. An operator edit clears it for good.</summary>
        public bool IsSeeded { get; private set; }

        /// <summary>Which contributor seeded it; empty for a hand-authored topic. A seed update must MATCH it.</summary>
        public string SeedContributorId { get; private set; }

        public DateTime CreatedOnUtc
        {
            get => HelpInstant.Stamp(_createdOnUtc);
            private set => _createdOnUtc = value;
        }

        public DateTime UpdatedOnUtc
        {
            get => HelpInstant.Stamp(_updatedOnUtc);
            private set => _updatedOnUtc = value;
        }

        /// <summary>Seeds a topic from a contribution. Published on arrival: a topic nobody can read is not help.</summary>
        public static HelpTopicAggregate Seed(TenantId tenantId, int siteId, HelpTopicContribution contribution, string contributorId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(contribution);

            if (string.IsNullOrWhiteSpace(contributorId))
            {
                throw new ArgumentException("A seeded topic names the contributor that owns it.", nameof(contributorId));
            }

            return new HelpTopicAggregate
            {
                Id = HelpTopicIds.For(tenantId.Value, siteId, contribution.Anchor, contribution.Locale),
                TenantId = tenantId,
                SiteId = siteId,
                Anchor = contribution.Anchor,
                Locale = contribution.Locale,
                Title = contribution.Title,
                Markdown = contribution.Markdown,
                ContentVersion = 1,
                IsPublished = true,
                IsSeeded = true,
                SeedContributorId = contributorId.Trim(),
                CreatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc)),
                UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc)),
            };
        }

        /// <summary>
        /// A topic an operator wrote by hand. It is a DRAFT until published, and it is not seeded, so no contributor
        /// will ever touch it.
        /// </summary>
        public static HelpTopicAggregate Author(TenantId tenantId, int siteId, string anchor, string locale, string title, string markdown, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);

            // Through the contribution's own validation, so a hand-authored topic is held to exactly the same bounds.
            var validated = new HelpTopicContribution(anchor, locale, title, markdown);

            return new HelpTopicAggregate
            {
                Id = HelpTopicIds.For(tenantId.Value, siteId, validated.Anchor, validated.Locale),
                TenantId = tenantId,
                SiteId = siteId,
                Anchor = validated.Anchor,
                Locale = validated.Locale,
                Title = validated.Title,
                Markdown = validated.Markdown,
                ContentVersion = 1,
                IsPublished = false,
                IsSeeded = false,
                SeedContributorId = string.Empty,
                CreatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc)),
                UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc)),
            };
        }

        /// <summary>
        /// Applies a contributor's newer text, and answers whether anything changed.
        /// </summary>
        /// <remarks>
        /// <b>Three conditions, each guarding a measured failure.</b> The row must still be SEEDED (an operator edit is
        /// final); the contributor must be the one that OWNS it (otherwise any
        /// contributor could rewrite any other's topic); and the text must actually DIFFER (otherwise
        /// every boot would bump the version and the timestamp, rewrite every seeded row and invalidate
        /// its cache key). Returning false means "nothing to write", and the reconciler writes nothing.
        /// </remarks>
        public bool TryApplySeedUpdate(HelpTopicContribution contribution, string contributorId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(contribution);

            if (!IsSeeded || !string.Equals(SeedContributorId, (contributorId ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(Title, contribution.Title, StringComparison.Ordinal)
                && string.Equals(Markdown, contribution.Markdown, StringComparison.Ordinal))
            {
                return false;
            }

            Title = contribution.Title;
            Markdown = contribution.Markdown;
            ContentVersion++;
            UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc));
            return true;
        }

        /// <summary>
        /// An operator's edit. It CLEARS the seed, permanently: from here the topic is the site's, and no contributor
        /// will overwrite it on a later boot.
        /// </summary>
        public void UpdateContent(string title, string markdown, DateTime nowUtc)
        {
            var validated = new HelpTopicContribution(Anchor, Locale, title, markdown);

            Title = validated.Title;
            Markdown = validated.Markdown;
            ContentVersion++;
            IsSeeded = false;
            SeedContributorId = string.Empty;
            UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc));
        }

        public void Publish(DateTime nowUtc) => SetPublished(true, nowUtc);

        public void Unpublish(DateTime nowUtc) => SetPublished(false, nowUtc);

        /// <summary>
        /// Hands an edited topic back to its contributor - the operator's explicit "reset to seed"
        /// (<c>help:manage</c>), which is the ONLY way an ownership change happens.
        /// </summary>
        public void ResetToSeed(HelpTopicContribution contribution, string contributorId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(contribution);

            if (string.IsNullOrWhiteSpace(contributorId))
            {
                throw new ArgumentException("A seeded topic names the contributor that owns it.", nameof(contributorId));
            }

            Title = contribution.Title;
            Markdown = contribution.Markdown;
            ContentVersion++;
            IsSeeded = true;
            SeedContributorId = contributorId.Trim();
            UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc));
        }

        private void SetPublished(bool published, DateTime nowUtc)
        {
            if (IsPublished == published)
            {
                // Nothing changed, so nothing is written - the same rule as a no-op seed update, for the same reason.
                return;
            }

            IsPublished = published;
            UpdatedOnUtc = HelpInstant.Require(nowUtc, nameof(nowUtc));
        }
    }

    /// <summary>
    /// The help domain's UTC rule - the MCP and export domains', for the same measured reason.
    /// </summary>
    /// <remarks>
    /// Inputs are REQUIRED to be <see cref="DateTimeKind.Utc"/>; persisted values are re-stamped on read, because a
    /// relational provider materialises a scalar <see cref="DateTime"/> with no kind while RavenDB and the File store
    /// hand back UTC - and <see cref="DateTime"/> equality ignores the kind, so the divergence passes every naive
    /// round-trip assertion and only shows when something converts the value.
    /// </remarks>
    internal static class HelpInstant
    {
        public static DateTime Require(DateTime value, string parameterName) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", parameterName);

        public static DateTime Stamp(DateTime stored) =>
            stored.Kind == DateTimeKind.Utc ? stored : DateTime.SpecifyKind(stored, DateTimeKind.Utc);
    }
}
