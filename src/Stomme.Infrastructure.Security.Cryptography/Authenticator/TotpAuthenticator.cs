using System.Globalization;
using System.Security.Cryptography;
using Stomme.Infrastructure.Security.Cryptography.Encoding;
using Stomme.Security.Abstractions.Authenticator;

namespace Stomme.Infrastructure.Security.Cryptography.Authenticator;

/// <summary>
/// RFC 6238 TOTP authenticator. Generates and verifies time-based one-time passwords.
/// Consolidates the previous GoogleAuthenticator, OtpAuthenticator, and TimeBasedOTP
/// into a single, correct implementation.
/// </summary>
public sealed class TotpAuthenticator : IOtpAuthenticator
{
    private const int TimeStepSeconds = 30;
    private const int Digits = 6;

    public string GenerateKey()
    {
        byte[] data = RandomNumberGenerator.GetBytes(20); // 160-bit key (RFC recommended)
        return Base32.Encode(data);
    }

    public string GeneratePin(string secretKey, out int remainingSeconds)
    {
        byte[] keyBytes = Base32.Decode(secretKey.Replace(" ", ""));
        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long counter = unixTime / TimeStepSeconds;
        remainingSeconds = (int)(TimeStepSeconds - (unixTime % TimeStepSeconds));
        return ComputeTotp(keyBytes, counter);
    }

    public string GenerateProvisioningUri(string username, string secretKey, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(username)}" +
        $"?secret={secretKey}&issuer={Uri.EscapeDataString(issuer)}";

    public bool VerifyCode(string secretKey, string code)
    {
        byte[] keyBytes = Base32.Decode(secretKey.Replace(" ", ""));
        long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long counter = unixTime / TimeStepSeconds;

        // Check current and +/- 1 time step to tolerate clock drift
        for (long i = -1; i <= 1; i++)
        {
            string totp = ComputeTotp(keyBytes, counter + i);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(totp),
                System.Text.Encoding.UTF8.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeTotp(byte[] keyBytes, long counter)
    {
        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(keyBytes);
        byte[] hash = hmac.ComputeHash(counterBytes);

        int offset = hash[^1] & 0x0f;
        int binaryCode =
            ((hash[offset] & 0x7f) << 24) |
            ((hash[offset + 1] & 0xff) << 16) |
            ((hash[offset + 2] & 0xff) << 8) |
            (hash[offset + 3] & 0xff);

        int totp = binaryCode % (int)Math.Pow(10, Digits);
        return totp.ToString(new string('0', Digits), CultureInfo.InvariantCulture);
    }
}
