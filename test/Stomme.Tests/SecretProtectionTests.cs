using Stomme.Infrastructure.Security.Cryptography;
using Stomme.Security.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests;

/// <summary>
/// Guards <c>AddSecretProtection()</c> and the Data Protection-backed <see cref="ISecretProtector"/> in
/// <c>Stomme.Infrastructure.Security.Cryptography</c>. Before the lifted Stomme.Identity.* model was retired,
/// the only cover was <c>IdentityRelationalStoreTests.The_mfa_secret_is_stored_protected_and_gates_sign_in</c>
/// in the retired <c>Stomme.Stores.EfCore.Identity.Tests</c>; the project survives because the shared
/// site secret store wires it, so its cover survives too.
/// </summary>
public class SecretProtectionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSecretProtection();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddSecretProtection_registers_a_secret_protector_that_round_trips()
    {
        using var provider = Build();
        var protector = provider.GetRequiredService<ISecretProtector>();

        var ciphertext = protector.Protect("JBSWY3DPEHPK3PXP");

        Assert.NotEqual("JBSWY3DPEHPK3PXP", ciphertext);
        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(ciphertext));
    }

    [Fact]
    public void The_protector_purpose_is_pinned_so_existing_ciphertext_still_unprotects()
    {
        // Renaming the purpose string would orphan every secret already protected with it.
        using var provider = Build();
        var ciphertext = provider.GetRequiredService<ISecretProtector>().Protect("s3cret");

        var samePurpose = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Stomme.Identity.Secrets.v1");

        Assert.Equal("s3cret", samePurpose.Unprotect(ciphertext));
    }

    [Fact]
    public void A_protector_with_another_purpose_cannot_unprotect_the_secret()
    {
        using var provider = Build();
        var ciphertext = provider.GetRequiredService<ISecretProtector>().Protect("s3cret");

        var otherPurpose = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Some.Other.Purpose");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => otherPurpose.Unprotect(ciphertext));
    }
}