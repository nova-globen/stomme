namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Encrypts and decrypts the <c>[Encrypt]</c>-marked string members of a persisted object in place — the
/// field-level "encryption at rest" seam. A repository calls <see cref="ProtectFields"/> before writing an
/// aggregate (so the stored document holds ciphertext) and <see cref="UnprotectFields"/> after reading one
/// (and after a write, to restore the caller's in-memory instance to plaintext). The operation is idempotent.
/// The default behaviour is a no-op; a host opts in by registering the Data Governance-driven implementation.
/// </summary>
public interface IFieldProtector
{
    /// <summary>Encrypts the object's <c>[Encrypt]</c> string members in place (idempotent).</summary>
    void ProtectFields(object? target);

    /// <summary>Decrypts the object's previously-protected <c>[Encrypt]</c> string members in place (idempotent).</summary>
    void UnprotectFields(object? target);
}
