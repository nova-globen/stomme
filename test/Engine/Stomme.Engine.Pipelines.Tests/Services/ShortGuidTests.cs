using Xunit;

namespace Stomme.Engine.Pipelines.Tests.Services;

/// <summary>
/// Pins the id FORMAT of the in-house <see cref="ShortGuid"/> that replaced the ShortGuid package (its 2.0.1
/// nuspec declares no licence, so the permissive-licence rule cannot carry it). Every golden vector below was MEASURED
/// against DEDrake.ShortGuid 2.0.1 before the package was removed - if one of these changes, ids the
/// engine has already handed out stop round-tripping.
/// </summary>
public class ShortGuidTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("11223344-5566-7788-99aa-bbccddeeff00", "RDMiEWZViHeZqrvM3e7_AA")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "_____________________w")]
    [InlineData("3f3e3d3c-3b3a-3938-3736-353433323130", "PD0-Pzo7ODk3NjU0MzIxMA")]
    [InlineData("fbef0000-0000-0000-0000-0000000000fb", "AADv-wAAAAAAAAAAAAAA-w")]
    public void Encode_MatchesTheShortGuidPackageFormat(string guid, string expected)
    {
        Assert.Equal(expected, ShortGuid.Encode(Guid.Parse(guid)));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("11223344-5566-7788-99aa-bbccddeeff00", "RDMiEWZViHeZqrvM3e7_AA")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "_____________________w")]
    [InlineData("3f3e3d3c-3b3a-3938-3736-353433323130", "PD0-Pzo7ODk3NjU0MzIxMA")]
    [InlineData("fbef0000-0000-0000-0000-0000000000fb", "AADv-wAAAAAAAAAAAAAA-w")]
    public void Decode_ReadsBackTheEncodedGuid(string guid, string encoded)
    {
        Assert.Equal(Guid.Parse(guid), ShortGuid.Decode(encoded));
    }

    [Fact]
    public void Encode_IsUrlSafeAndUnpadded()
    {
        for (var i = 0; i < 1000; i++)
        {
            var value = Guid.NewGuid();
            var encoded = ShortGuid.Encode(value);

            Assert.Equal(22, encoded.Length);
            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.DoesNotContain('=', encoded);
            Assert.Equal(value, ShortGuid.Decode(encoded));
        }
    }

    [Fact]
    public void Decode_RejectsAnIdentifierOfTheWrongLength()
    {
        Assert.Throws<FormatException>(() => ShortGuid.Decode("RDMiEWZViHeZqrvM3e7_A"));
    }

    [Fact]
    public void GenerateId_ProducesADecodableShortGuid()
    {
        var generator = new ShortGuidIdentityGenerator();

        var first = generator.GenerateId();
        var second = generator.GenerateId();

        Assert.Equal(22, first.Length);
        Assert.NotEqual(first, second);
        Assert.NotEqual(Guid.Empty, ShortGuid.Decode(first));
    }
}
