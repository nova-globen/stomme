using Stomme.Security.Abstractions.Authenticator;
using Stomme.Infrastructure.Security.Cryptography.Authenticator;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Security.Cryptography;

public static class SecurityCryptographyServiceExtensions
{
    /// <summary>Registers the RFC 6238 TOTP authenticator (<see cref="IOtpAuthenticator"/>).</summary>
    public static IServiceCollection AddSecurityCryptography(this IServiceCollection services)
    {
        services.AddScoped<IOtpAuthenticator, TotpAuthenticator>();
        return services;
    }
}
