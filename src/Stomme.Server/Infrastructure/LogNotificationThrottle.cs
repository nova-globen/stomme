#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// What a site's Host users are told of its logs (<see cref="LogDrainService"/>: an entry at the notify level or above -
    /// Error by default - becomes a notification, and so an e-mail) held back when it repeats. Found on UAT on 2026-10-10, the
    /// day both sites were wired to the mail relay: every request a permission key refuses is logged at Error, so an anonymous
    /// probe of an API mailed the host each time, and a crawler could spend the relay's whole day (it allows about 60 mails an
    /// hour and 300 a day) on mail nobody needs, holding back the mail that matters. The same kind of entry - its message
    /// template, at its level, on its site - is told at most once an <see cref="Window"/>, and a site is told of at most
    /// <see cref="MaxPerWindow"/> entries an hour, whatever they are; the next one told of a kind says how many were held back.
    /// Every entry is still written to the site's log - only the telling is held back. Per node: a farm tells each node's own.
    /// </summary>
    public sealed class LogNotificationThrottle
    {
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);

        public const int MaxPerWindow = 10;

        /// <summary>How many kinds are remembered before the ones told longer than <see cref="Window"/> ago are let go.</summary>
        private const int KindsBeforePruning = 1000;

        private readonly TimeProvider _clock;
        private readonly object _gate = new();
        private readonly Dictionary<(int Site, string Kind), DateTimeOffset> _lastTold = new();
        private readonly Dictionary<(int Site, string Kind), int> _heldBack = new();
        private readonly Dictionary<int, Queue<DateTimeOffset>> _told = new();

        public LogNotificationThrottle(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

        /// <summary>
        /// Whether an entry is told to the site's hosts now - and, when it is, how many entries of its kind were held back since
        /// its kind was last told.
        /// </summary>
        public bool ShouldTell(int siteId, string? level, string? kind, out int heldBack)
        {
            heldBack = 0;
            var key = (siteId, (level ?? "") + "|" + (kind ?? ""));
            var now = _clock.GetUtcNow();
            lock (_gate)
            {
                if (_lastTold.TryGetValue(key, out var last) && now - last < Window)
                {
                    _heldBack[key] = _heldBack.GetValueOrDefault(key) + 1;
                    return false;
                }

                if (!_told.TryGetValue(siteId, out var told))
                {
                    told = new Queue<DateTimeOffset>();
                    _told[siteId] = told;
                }

                while (told.Count > 0 && now - told.Peek() >= Window)
                {
                    told.Dequeue();
                }

                if (told.Count >= MaxPerWindow)
                {
                    _heldBack[key] = _heldBack.GetValueOrDefault(key) + 1;
                    return false;
                }

                told.Enqueue(now);
                _lastTold[key] = now;
                heldBack = _heldBack.Remove(key, out var held) ? held : 0;
                if (_lastTold.Count > KindsBeforePruning)
                {
                    // A kind not told for a whole window is told afresh next time anyway; its held-back count goes with it.
                    foreach (var stale in _lastTold.Where(pair => now - pair.Value >= Window).Select(pair => pair.Key).ToList())
                    {
                        _lastTold.Remove(stale);
                        _heldBack.Remove(stale);
                    }
                }

                return true;
            }
        }
    }
}
