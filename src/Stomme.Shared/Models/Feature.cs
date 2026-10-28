using System;
using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// A feature flag projected for the admin UI — the client-side shape of the server's
    /// <c>Stomme.FeatureManagement.Application.FeatureView</c> record (the Client cannot reference the
    /// FeatureManagement project, so this mirrors its JSON contract; keep the two in lockstep).
    /// </summary>
    public class Feature
    {
        /// <summary>
        /// The flag key, e.g. "module.htmltext.enabled"
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The flag's configured (stored) state — see <see cref="Effective"/> for what evaluation answers
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Entitlement key this flag additionally requires, if any (BL-2 territory; permissive until then)
        /// </summary>
        public string RequiredEntitlement { get; set; }

        /// <summary>
        /// Another flag this flag depends on, if any
        /// </summary>
        public string RequiredFlag { get; set; }

        /// <summary>
        /// Roles the flag is targeted to; empty = everyone
        /// </summary>
        public List<string> TargetRoles { get; set; } = new List<string>();

        /// <summary>
        /// Percentage rollout (0-100)
        /// </summary>
        public int RolloutPercentage { get; set; } = 100;

        /// <summary>
        /// The evaluator's current answer, after config-source precedence, dependencies, targeting and rollout
        /// </summary>
        public bool Effective { get; set; }

        /// <summary>
        /// Catalog description, when the flag is declared in a feature catalog
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Catalog category, when declared
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Per-user overrides, keyed by the projected feature user id (see the server's FeatureUserId)
        /// </summary>
        public Dictionary<Guid, bool> UserOverrides { get; set; } = new Dictionary<Guid, bool>();
    }

    /// <summary>
    /// An entry in a feature flag's change history — the client-side shape of the server's
    /// <c>FeatureAuditView</c>.
    /// </summary>
    public class FeatureAudit
    {
        /// <summary>
        /// The flag key
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The state the flag was changed TO
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Who made the change, when recorded
        /// </summary>
        public string ChangedBy { get; set; }

        /// <summary>
        /// When the change happened (UTC)
        /// </summary>
        public DateTimeOffset ChangedAtUtc { get; set; }
    }
}
