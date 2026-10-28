#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Help;

namespace Stomme.Modules.ContextualHelp.Infrastructure
{
    /// <summary>A contributed topic together with the ONE contributor that owns it - the id a seeded row remembers.</summary>
    public sealed record HelpContributedTopic(string ContributorId, HelpTopicContribution Contribution);

    /// <summary>
    /// One contribution that was REJECTED, and why - what the boot log records at Error and the admin page lists.
    /// </summary>
    /// <param name="Kind">"topic" or "contributor".</param>
    /// <param name="Name">The <c>anchor|locale</c> key, or the contributor id, the conflict is about.</param>
    /// <param name="Contributors">Every contributor involved. For a collision, ALL of them - none of them won.</param>
    /// <param name="Reason">Operator-facing, and it says what was dropped rather than what was kept.</param>
    public sealed record HelpContributionConflict(string Kind, string Name, IReadOnlyList<string> Contributors, string Reason);

    /// <summary>
    /// Every topic this install's contributors ship, each bound to the contributor that owns it (BL-84, D103 item 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A key published twice is REJECTED - on every side - and the host starts</b> (D103 refutation 8; BL-105 made it
    /// the rule for every contribution seam). Discovery matches ANY assembly in <c>/bin</c> whose name contains
    /// <c>Stomme</c>, including a package <c>InstallationManager</c> unpacked there, so a fatal check would let a third
    /// party stop the host with no admin UI left to remove it.
    /// </para>
    /// <para>
    /// <b>Why every side, and not "the framework contributor wins".</b> A seeded row belongs to the contributor that
    /// seeded it, and the reconciler updates it only for that contributor. If a package could win a key, it could take
    /// ownership of a framework topic's row on a fresh site and publish its text under the framework's anchor - help
    /// text is shown to anonymous readers, so that is a content-injection channel. If the framework always won, the
    /// rule would be one an attacker could aim at from the other side. Rejecting every side means the key simply has no
    /// contributed topic: an existing row is left exactly as it is, and nothing is created.
    /// </para>
    /// <para>
    /// <b>Nothing a contributor does throws from this constructor.</b> A blank id, a <c>Topics</c> getter that throws,
    /// a null entry - each is that contributor's bug, each could arrive in a package, so each is a rejection and not a
    /// dead install. IN-TREE duplicates are caught earlier and harder, by a reflective BUILD gate
    /// (<c>HelpContributorConflictTests</c>).
    /// </para>
    /// <para>
    /// <b>This type does not log.</b> It is SCOPED - a contributor registered explicitly may need scoped services - so
    /// logging here would repeat the same Error on every request. The module startup resolves it once while the
    /// pipeline is built and logs <see cref="Conflicts"/> there; the admin page reads the same list.
    /// </para>
    /// </remarks>
    public sealed class HelpTopicContributorRegistry
    {
        private readonly Dictionary<string, HelpContributedTopic> _topics = new(StringComparer.Ordinal);
        private readonly List<HelpContributionConflict> _conflicts = new();

        public HelpTopicContributorRegistry(IEnumerable<IHelpTopicContributor> contributors)
        {
            ArgumentNullException.ThrowIfNull(contributors);

            // ---- contributors: an id names ONE contributor ------------------------------------------------------------
            var accepted = new Dictionary<string, (IHelpTopicContributor Contributor, IReadOnlyList<HelpTopicContribution?> Topics)>(StringComparer.OrdinalIgnoreCase);
            var rejectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var contributor in contributors)
            {
                if (contributor is null)
                {
                    continue;
                }

                var type = TypeName(contributor);
                string? id;
                IReadOnlyList<HelpTopicContribution?> topics;
                try
                {
                    id = contributor.ContributorId?.Trim();
                    topics = (contributor.Topics ?? Array.Empty<HelpTopicContribution>()).ToList();
                }
                catch (Exception ex)
                {
                    // A package whose getter throws (a malformed anchor in a static initializer is the likely one) costs
                    // that package and nothing else.
                    Reject("contributor", type, new[] { type }, $"Reading its id or topics threw {ex.GetType().Name}: {ex.Message} It was dropped.");
                    continue;
                }

                if (string.IsNullOrEmpty(id))
                {
                    Reject("contributor", type, new[] { type }, "It declares no ContributorId, so no row it seeds could be attributed to it. It was dropped.");
                    continue;
                }

                if (rejectedIds.Contains(id))
                {
                    Reject("contributor", id, new[] { type }, "This contributor id was already rejected as a collision. It stays dropped.");
                    continue;
                }

                if (accepted.Remove(id, out var sameId))
                {
                    // BOTH go: keeping the first would be first-wins, and an id is what OWNS a seeded row.
                    Reject("contributor", id, new[] { TypeName(sameId.Contributor), type },
                        "Two contributors claim this id. Both were dropped; an id names ONE contributor, because it owns the rows it seeds.");
                    rejectedIds.Add(id);
                    continue;
                }

                accepted.Add(id, (contributor, topics));
            }

            // ---- topics: an (anchor, locale) belongs to ONE contribution ------------------------------------------------
            var rejectedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, (contributor, topics)) in accepted)
            {
                var type = TypeName(contributor);
                foreach (var topic in topics)
                {
                    if (topic is null)
                    {
                        Reject("topic", "(null)", new[] { type }, "The contributor shipped a null topic. It was dropped.");
                        continue;
                    }

                    var key = Key(topic.Anchor, topic.Locale);
                    if (rejectedKeys.Contains(key))
                    {
                        // A third contribution must not resurrect a key the second one already cost.
                        Reject("topic", key, new[] { type }, "This topic was already rejected as a collision. It stays dropped.");
                        continue;
                    }

                    if (_topics.Remove(key, out var existing))
                    {
                        // The same contributor twice is ambiguous too - which text is the seed? - so it is the same rule.
                        Reject("topic", key, new[] { existing.ContributorId, id },
                            "Two contributions ship this anchor and locale. BOTH were dropped, so no contributed topic exists for it: " +
                            "an existing row is left as it is, and none is created.");
                        rejectedKeys.Add(key);
                        continue;
                    }

                    _topics.Add(key, new HelpContributedTopic(id, topic));
                }
            }
        }

        /// <summary>Every accepted topic, in a stable order (anchor, then locale) - independent of discovery order.</summary>
        public IReadOnlyList<HelpContributedTopic> Topics =>
            _topics.Values
                .OrderBy(topic => topic.Contribution.Anchor, StringComparer.Ordinal)
                .ThenBy(topic => topic.Contribution.Locale, StringComparer.Ordinal)
                .ToList();

        /// <summary>What was dropped, and why. Empty on a healthy install.</summary>
        public IReadOnlyList<HelpContributionConflict> Conflicts => _conflicts;

        /// <summary>
        /// The contributed topic for an anchor and locale - what "reset to seed" restores. Non-throwing, like every read of
        /// a key that may have come from the wire.
        /// </summary>
        public bool TryGet(string? anchor, string? locale, out HelpContributedTopic topic)
        {
            topic = null!;
            return HelpKeys.TryNormalizeAnchor(anchor, out var normalizedAnchor)
                   && HelpKeys.TryNormalizeLocale(locale, out var normalizedLocale)
                   && _topics.TryGetValue(Key(normalizedAnchor, normalizedLocale), out topic!);
        }

        private static string Key(string anchor, string locale) => anchor + "|" + locale;

        private static string TypeName(object contributor) => contributor.GetType().FullName ?? contributor.GetType().Name;

        private void Reject(string kind, string name, IReadOnlyList<string> contributors, string reason) =>
            _conflicts.Add(new HelpContributionConflict(kind, name, contributors, reason));
    }
}
