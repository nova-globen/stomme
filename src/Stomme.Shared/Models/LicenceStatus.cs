using System;
using System.Collections.Generic;

namespace Stomme.Models
{
    /// <summary>
    /// The Host-only licensing status, answered by <c>GET api/Licence</c> only under
    /// <c>Deployment:Mode=OnPrem</c> - every other mode answers 404, which the admin page reads as "licensing is not
    /// enabled". It NEVER carries a key blob.
    /// </summary>
    public class LicenceStatus
    {
        /// <summary>Always true on a 200: a disabled installation answers 404 instead of a status.</summary>
        public bool Enabled { get; set; }

        /// <summary>The id a vendor binds a key to (the signed <c>sub</c>). Empty until the install/upgrade path mints it.</summary>
        public string InstallationId { get; set; }

        /// <summary>
        /// False while <see cref="InstallationId"/> is empty. No key can then be valid, and a signed key reports
        /// <c>NoInstallationId</c> - never <c>WrongInstallation</c> (refutation 9).
        /// </summary>
        public bool InstallationInitialised { get; set; }

        public List<LicenceKeyStatus> Keys { get; set; } = new List<LicenceKeyStatus>();
    }

    /// <summary>One configured <c>Licensing:Keys:&lt;lid&gt;</c> entry. Carries no blob.</summary>
    /// <remarks>
    /// <see cref="Label"/>, <see cref="Entitlements"/>, <see cref="Limits"/> and <see cref="Expires"/> are read from the key's
    /// payload whenever it parsed, but are AUTHENTIC only when the signature verified (every <see cref="Reason"/> except
    /// <c>Malformed</c>, <c>UnknownKey</c> and <c>BadSignature</c>); only <c>Valid</c> grants anything.
    /// </remarks>
    public class LicenceKeyStatus
    {
        /// <summary>The configuration entry's own name under <c>Licensing:Keys</c> - what DELETE addresses.</summary>
        public string Name { get; set; }

        /// <summary>The signed <c>lid</c>, or the entry name parsed as a Guid when the payload did not parse, else empty.</summary>
        public Guid Lid { get; set; }

        public string Label { get; set; }

        /// <summary>The verifier's reason name: Valid, Malformed, UnknownKey, BadSignature, WrongInstallation, NoInstallationId, NotYetValid, Expired or LidMismatch.</summary>
        public string Reason { get; set; }

        public List<string> Entitlements { get; set; } = new List<string>();

        public Dictionary<string, long> Limits { get; set; } = new Dictionary<string, long>();

        public DateTimeOffset? Expires { get; set; }

        /// <summary>
        /// The configuration source whose value is in force: a settings file's name (e.g. <c>appsettings.json</c>),
        /// <c>environment</c> for an environment variable, or the provider's type name for anything else.
        /// </summary>
        public string Source { get; set; }

        /// <summary>True only when <see cref="Source"/> is the settings file this installation's ConfigManager writes.</summary>
        public bool Deletable { get; set; }
    }

    /// <summary>The body of <c>POST api/Licence</c>: one pasted <c>SLK1</c> key.</summary>
    public class LicenceKeySubmission
    {
        public string Key { get; set; }
    }
}