namespace Stomme.Security.Abstractions.Authenticator;

public interface IOtpAuthenticator
{
    string GenerateKey();
    string GeneratePin(string secretKey, out int remainingSeconds);
    string GenerateProvisioningUri(string username, string secretKey, string issuer);
    bool VerifyCode(string secretKey, string code);
}
