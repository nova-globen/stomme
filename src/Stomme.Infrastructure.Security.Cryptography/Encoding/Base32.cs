namespace Stomme.Infrastructure.Security.Cryptography.Encoding;

/// <summary>
/// RFC 4648 Base32 encoding/decoding.
/// </summary>
public static class Base32
{
    private static readonly char[] Digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public static byte[] Decode(string base32)
    {
        ArgumentNullException.ThrowIfNull(base32);

        base32 = base32.TrimEnd('=').ToUpperInvariant();
        if (base32.Length == 0) return [];

        int byteCount = base32.Length * 5 / 8;
        byte[] bytes = new byte[byteCount];

        byte curByte = 0;
        int bitsRemaining = 8;
        int arrayIndex = 0;

        foreach (char c in base32)
        {
            int cValue = CharToValue(c);

            if (bitsRemaining > 5)
            {
                curByte = (byte)(curByte | (cValue << (bitsRemaining - 5)));
                bitsRemaining -= 5;
            }
            else
            {
                curByte = (byte)(curByte | (cValue >> (5 - bitsRemaining)));
                bytes[arrayIndex++] = curByte;
                curByte = (byte)(cValue << (3 + bitsRemaining));
                bitsRemaining += 3;
            }
        }

        if (arrayIndex < byteCount)
        {
            bytes[arrayIndex] = curByte;
        }

        return bytes;
    }

    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return string.Empty;

        int charCount = (int)Math.Ceiling(data.Length / 5d) * 8;
        char[] result = new char[charCount];

        byte nextChar = 0;
        int bitsRemaining = 5;
        int arrayIndex = 0;

        foreach (byte b in data)
        {
            nextChar = (byte)(nextChar | (b >> (8 - bitsRemaining)));
            result[arrayIndex++] = Digits[nextChar];

            if (bitsRemaining < 4)
            {
                nextChar = (byte)((b >> (3 - bitsRemaining)) & 31);
                result[arrayIndex++] = Digits[nextChar];
                bitsRemaining += 5;
            }

            bitsRemaining -= 3;
            nextChar = (byte)((b << bitsRemaining) & 31);
        }

        if (arrayIndex < charCount)
        {
            result[arrayIndex++] = Digits[nextChar];
            while (arrayIndex < charCount)
            {
                result[arrayIndex++] = '=';
            }
        }

        return new string(result);
    }

    private static int CharToValue(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= '2' and <= '7' => c - '2' + 26,
        _ => throw new FormatException($"Invalid Base32 character: {c}")
    };
}
