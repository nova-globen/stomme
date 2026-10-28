using Stomme.Security.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Stomme.Infrastructure.Security.Cryptography.SecretProtection;

/// <summary>
/// <see cref="ISecretProtector"/> over ASP.NET Data Protection. The provider manages a rotating key ring
/// (new keys are minted periodically; retired keys are retained so existing ciphertext still unprotects),
/// which gives key rotation for free. The purpose string isolates these secrets from other protectors.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Stomme.Identity.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
