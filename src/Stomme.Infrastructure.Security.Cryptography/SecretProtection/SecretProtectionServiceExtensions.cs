using Stomme.Security.Abstractions;
using Stomme.Infrastructure.Security.Cryptography.SecretProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Security.Cryptography;

public static class SecretProtectionServiceExtensions
{
    /// <summary>
    /// Registers the Data Protection-backed <see cref="ISecretProtector"/> (the <c>[Encrypt]</c>-field seam
    /// for secrets at rest). Any host that resolves <see cref="ISecretProtector"/> needs this.
    /// </summary>
    public static IServiceCollection AddSecretProtection(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }
}
