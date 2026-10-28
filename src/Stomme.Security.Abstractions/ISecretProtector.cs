namespace Stomme.Security.Abstractions;

/// <summary>
/// Protects small secrets at rest (e.g. a user's TOTP key) — the seam behind an <c>[Encrypt]</c>-style
/// field. The host implements it over a key-managed provider (ASP.NET Data Protection, which rotates keys
/// and retains old ones for unprotect). <see cref="Unprotect"/> throws when the value is tampered or the
/// key is unavailable.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
