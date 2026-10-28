using System;
using System.Collections.Generic;

namespace Stomme.Repository.Identity
{
    /// <summary>
    /// The STANDALONE RavenDB document for an ASP.NET Identity <c>IdentityUser</c> (D54b). This is the
    /// document-store home of the <c>AspNetUsers</c> row plus its external logins and passkeys — the data EF
    /// keeps across <c>AspNetUsers</c>/<c>AspNetUserLogins</c>/<c>AspNetUserPasskeys</c> on the relational path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately NOT the seam's <c>UserAggregate</c>.</b> That aggregate maps Stomme's OWN <c>User</c>
    /// model (a different CLR type, a different table), and its remarks are explicit that stock Identity keeps
    /// its own <c>IdentityUser</c> entity disjoint from it, correlated only by username. Reusing it would
    /// conflate the two. This type is Identity's private persistence shape and nothing else reads it.
    /// </para>
    /// <para>
    /// Not an <c>AggregateRoot&lt;TId&gt;</c> and not on the framework seam: Identity is a separate concern with
    /// its own <c>IUserStore</c> lifecycle (create/update by the framework, not through the seam's stores), so it
    /// is a plain document addressed by an explicit id (<c>RavenIdentityUsers/{Id}</c>). The store persists it
    /// through the lazy <see cref="Raven.Client.Documents.IDocumentStore"/>, never through
    /// <c>IFrameworkStoreSessionFactory</c>.
    /// </para>
    /// </remarks>
    public sealed class RavenIdentityUser
    {
        /// <summary>The IdentityUser id (a string GUID assigned by <c>IdentityUser</c>'s constructor).</summary>
        public string Id { get; set; }

        public string UserName { get; set; }

        public string NormalizedUserName { get; set; }

        public string Email { get; set; }

        public string NormalizedEmail { get; set; }

        public bool EmailConfirmed { get; set; }

        public string PasswordHash { get; set; }

        public string SecurityStamp { get; set; }

        public string ConcurrencyStamp { get; set; }

        public string PhoneNumber { get; set; }

        public bool PhoneNumberConfirmed { get; set; }

        /// <summary>When the lockout ends; null/past means not locked (<c>IUserLockoutStore</c>, BL-6). The
        /// document twin of AspNetUsers.LockoutEnd.</summary>
        public DateTimeOffset? LockoutEnd { get; set; }

        /// <summary>Whether lockout applies to this user at all — <c>UserManager.IsLockedOutAsync</c> consults
        /// this BEFORE <see cref="LockoutEnd"/> (<c>IUserLockoutStore</c>, BL-6).</summary>
        public bool LockoutEnabled { get; set; }

        /// <summary>Consecutive failed access attempts; reset to zero when the threshold trips the lockout
        /// (<c>IUserLockoutStore</c>, BL-6).</summary>
        public int AccessFailedCount { get; set; }

        /// <summary>The user's two-factor toggle (<c>IUserTwoFactorStore</c>, BL-6).</summary>
        public bool TwoFactorEnabled { get; set; }

        /// <summary>External OAuth/OIDC logins (<c>IUserLoginStore</c>). External login is live in this host.</summary>
        public List<RavenStoredLogin> Logins { get; set; } = new();

        /// <summary>Passkey credentials (<c>IUserPasskeyStore</c>, .NET 10).</summary>
        public List<RavenStoredPasskey> Passkeys { get; set; } = new();
    }

    /// <summary>A single external login for <see cref="RavenIdentityUser"/> — the (provider, key) shape of
    /// <c>UserLoginInfo</c>, flattened for the document store.</summary>
    public sealed class RavenStoredLogin
    {
        public string LoginProvider { get; set; }

        public string ProviderKey { get; set; }

        public string ProviderDisplayName { get; set; }
    }

    /// <summary>
    /// A single passkey for <see cref="RavenIdentityUser"/>. A plain DTO rather than the framework's sealed
    /// <c>UserPasskeyInfo</c> (which has only a large positional constructor and get-only members) so the
    /// document (de)serializes without a custom converter; <c>RavenIdentityUserStore</c> maps between the two.
    /// The credential id is kept base64 so equality is a simple string compare in the store.
    /// </summary>
    public sealed class RavenStoredPasskey
    {
        public string CredentialId { get; set; }

        public byte[] PublicKey { get; set; }

        public string Name { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public uint SignCount { get; set; }

        public string[] Transports { get; set; }

        public bool IsUserVerified { get; set; }

        public bool IsBackupEligible { get; set; }

        public bool IsBackedUp { get; set; }

        public byte[] AttestationObject { get; set; }

        public byte[] ClientDataJson { get; set; }
    }
}
