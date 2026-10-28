using System;
using Stomme.Infrastructure.Security.Cryptography.Authenticator;
using Stomme.Infrastructure.Security.Cryptography.Encoding;
using Xunit;

namespace Stomme.Tests;

/// <summary>Guards the lifted-intact RFC 6238 TOTP authenticator and its RFC 4648 Base32 codec.</summary>
public class TotpAuthenticatorTests
{
    private readonly TotpAuthenticator _totp = new();

    [Fact]
    public void A_generated_key_is_decodable_base32_of_the_recommended_160_bits()
    {
        var key = _totp.GenerateKey();

        Assert.Equal(20, Base32.Decode(key).Length);
    }

    [Fact]
    public void Two_generated_keys_differ()
    {
        Assert.NotEqual(_totp.GenerateKey(), _totp.GenerateKey());
    }

    [Fact]
    public void The_current_pin_verifies()
    {
        var key = _totp.GenerateKey();
        var pin = _totp.GeneratePin(key, out var remaining);

        Assert.Equal(6, pin.Length);
        Assert.InRange(remaining, 1, 30);
        Assert.True(_totp.VerifyCode(key, pin));
    }

    [Fact]
    public void A_wrong_code_does_not_verify()
    {
        var key = _totp.GenerateKey();

        Assert.False(_totp.VerifyCode(key, "000000"));
    }

    [Fact]
    public void A_pin_from_another_key_does_not_verify()
    {
        var key = _totp.GenerateKey();
        var otherPin = _totp.GeneratePin(_totp.GenerateKey(), out _);

        Assert.False(_totp.VerifyCode(key, otherPin));
    }

    [Fact]
    public void The_provisioning_uri_carries_the_issuer_account_and_secret()
    {
        var uri = _totp.GenerateProvisioningUri("ada@example.com", "JBSWY3DPEHPK3PXP", "Stomme");

        Assert.StartsWith("otpauth://totp/Stomme:", uri, StringComparison.Ordinal);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Stomme", uri, StringComparison.Ordinal);
        // The account is URL-escaped so an '@' cannot break the URI.
        Assert.Contains("ada%40example.com", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Base32_round_trips_arbitrary_bytes()
    {
        byte[] data = [0x00, 0x01, 0x7f, 0x80, 0xff, 0x42, 0x13];

        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
    }

    [Fact]
    public void Base32_matches_the_rfc4648_test_vector()
    {
        // RFC 4648 section 10: BASE32("foobar") = "MZXW6YTBOI======"
        Assert.Equal("MZXW6YTBOI======", Base32.Encode(System.Text.Encoding.ASCII.GetBytes("foobar")));
        Assert.Equal("foobar", System.Text.Encoding.ASCII.GetString(Base32.Decode("MZXW6YTBOI======")));
    }

    [Fact]
    public void Base32_rejects_an_invalid_character()
    {
        Assert.Throws<FormatException>(() => Base32.Decode("MZXW6YTB01"));
    }
}
