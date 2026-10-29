#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Modules.Backup.Application.Archive
{
    /// <summary>
    /// The <c>DCBAK1</c> archive. Layout, little-endian:
    /// <code>
    /// header   magic "DCBAK1" | format u16 | kdf u8 | reserved u8 | iterations u32 | salt[16] | nonce prefix[4] | chunk size u32
    /// frames   per chunk: plaintext length u32 | ciphertext | GCM tag[16]      (every part, then the manifest)
    /// footer   manifest offset u64 | manifest first counter u64 | "DCBAKEND"
    /// </code>
    /// One key per archive: PBKDF2-HMAC-SHA256 over the operator's passphrase and a fresh salt - never Data Protection, so
    /// an archive restores on a host with a different key ring. One GLOBAL chunk counter across the file makes every nonce
    /// unique under that key. Each chunk's AAD binds the whole header, the kind (part or manifest), the part index, the chunk
    /// index, the final flag and the length - so a flipped header byte, a reordered, dropped, truncated or transplanted chunk
    /// all fail GCM. The footer is NOT authenticated and needs not be: a wrong offset or counter only points at bytes that do
    /// not verify. The manifest (framework version, parts, lengths, SHA-256) is encrypted like any part.
    /// </summary>
    internal static class BackupArchiveFormat
    {
        public const ushort FormatVersion = 1;
        public const byte KdfPbkdf2Sha256 = 1;
        public const int Iterations = 210_000;
        public const int MaximumIterations = 10_000_000;
        public const int SaltLength = 16;
        public const int NoncePrefixLength = 4;
        public const int NonceLength = 12;
        public const int TagLength = 16;
        public const int KeyLength = 32;
        public const int DefaultChunkSize = 1024 * 1024;
        public const int MinimumChunkSize = 4 * 1024;
        public const int MaximumChunkSize = 16 * 1024 * 1024;
        public const int MaximumManifestLength = 16 * 1024 * 1024;
        public const int HeaderLength = 38;
        public const int FooterLength = 24;
        public const int FrameOverhead = 4 + TagLength;
        public const int AadLength = HeaderLength + 1 + 4 + 4 + 1 + 4;
        public const byte KindPart = 1;
        public const byte KindManifest = 2;

        public static ReadOnlySpan<byte> Magic => "DCBAK1"u8;

        public static ReadOnlySpan<byte> FooterMagic => "DCBAKEND"u8;

        public static byte[] WriteHeader(int iterations, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> noncePrefix, int chunkSize)
        {
            var header = new byte[HeaderLength];
            var span = header.AsSpan();
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], FormatVersion);
            span[8] = KdfPbkdf2Sha256;
            span[9] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span[10..], (uint)iterations);
            salt.CopyTo(span[14..]);
            noncePrefix.CopyTo(span[30..]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[34..], (uint)chunkSize);
            return header;
        }

        /// <summary>Parses and bounds the header BEFORE any key is derived: a crafted header cannot buy a billion PBKDF2 rounds.</summary>
        public static (int Iterations, byte[] Salt, byte[] NoncePrefix, int ChunkSize) ReadHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length != HeaderLength || !header[..6].SequenceEqual(Magic))
            {
                throw new BackupArchiveException("This is not a Nova Globen Stomme backup archive.");
            }

            var version = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            if (version != FormatVersion)
            {
                throw new BackupArchiveException($"This backup archive has format version {version}; this host reads version {FormatVersion}.");
            }

            if (header[8] != KdfPbkdf2Sha256 || header[9] != 0)
            {
                throw new BackupArchiveException("This backup archive names a key derivation this host does not use.");
            }

            var iterations = BinaryPrimitives.ReadUInt32LittleEndian(header[10..]);
            if (iterations < Iterations || iterations > MaximumIterations)
            {
                throw new BackupArchiveException($"This backup archive's key derivation cost ({iterations}) is outside {Iterations}-{MaximumIterations}.");
            }

            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header[34..]);
            if (chunkSize < MinimumChunkSize || chunkSize > MaximumChunkSize || (chunkSize & (chunkSize - 1)) != 0)
            {
                throw new BackupArchiveException($"This backup archive's chunk size ({chunkSize}) is not one this host accepts.");
            }

            return ((int)iterations, header.Slice(14, SaltLength).ToArray(), header.Slice(30, NoncePrefixLength).ToArray(), (int)chunkSize);
        }

        public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        {
            var secret = Encoding.UTF8.GetBytes(passphrase);
            try
            {
                return Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, KeyLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        public static void RequirePassphrase(string passphrase)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new ArgumentException("A backup archive needs a passphrase.", nameof(passphrase));
            }
        }
    }

    /// <summary>The one key of an archive, sealing and opening its chunks. Holds the key; zeroes it on dispose.</summary>
    internal sealed class BackupChunkCipher : IDisposable
    {
        private readonly AesGcm _aes;
        private readonly byte[] _key;
        private readonly byte[] _header;
        private readonly byte[] _noncePrefix;
        private readonly byte[] _nonce = new byte[BackupArchiveFormat.NonceLength];
        private readonly byte[] _aad = new byte[BackupArchiveFormat.AadLength];

        public BackupChunkCipher(byte[] key, byte[] header, byte[] noncePrefix)
        {
            _key = key;
            _header = header;
            _noncePrefix = noncePrefix;
            _aes = new AesGcm(key, BackupArchiveFormat.TagLength);
        }

        public void Seal(long counter, byte kind, int partIndex, int chunkIndex, bool final, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag)
        {
            Prepare(counter, kind, partIndex, chunkIndex, final, plaintext.Length);
            _aes.Encrypt(_nonce, plaintext, ciphertext, tag, _aad);
        }

        /// <summary>Opens one chunk; a chunk that does not verify throws <see cref="AuthenticationTagMismatchException"/>.</summary>
        public void Open(long counter, byte kind, int partIndex, int chunkIndex, bool final, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
        {
            Prepare(counter, kind, partIndex, chunkIndex, final, ciphertext.Length);
            _aes.Decrypt(_nonce, ciphertext, tag, plaintext, _aad);
        }

        private void Prepare(long counter, byte kind, int partIndex, int chunkIndex, bool final, int length)
        {
            _noncePrefix.CopyTo(_nonce, 0);
            BinaryPrimitives.WriteInt64BigEndian(_nonce.AsSpan(BackupArchiveFormat.NoncePrefixLength), counter);

            var aad = _aad.AsSpan();
            _header.CopyTo(aad);
            var at = BackupArchiveFormat.HeaderLength;
            aad[at] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(aad[(at + 1)..], partIndex);
            BinaryPrimitives.WriteInt32LittleEndian(aad[(at + 5)..], chunkIndex);
            aad[at + 9] = final ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(aad[(at + 10)..], length);
        }

        public void Dispose()
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    /// <summary>The archive's table of contents - written LAST, encrypted, and trusted only once GCM has verified it.</summary>
    public sealed class BackupManifest
    {
        public int FormatVersion { get; set; }

        public string FrameworkVersion { get; set; } = string.Empty;

        public DateTime CreatedOnUtc { get; set; }

        public List<int> TenantIds { get; set; } = new();

        public List<BackupManifestPart> Parts { get; set; } = new();
    }

    /// <summary>One part: where its chunks start in the file and in the counter, how many there are, and what they hold.</summary>
    public sealed class BackupManifestPart
    {
        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SchemaVersion { get; set; } = string.Empty;

        public string ModuleVersion { get; set; } = string.Empty;

        public long Length { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public long Offset { get; set; }

        public long FirstCounter { get; set; }

        public int ChunkCount { get; set; }
    }

    /// <summary>An archive that cannot be read as it claims: not an archive, the wrong passphrase, altered, or truncated.</summary>
    public sealed class BackupArchiveException : Exception
    {
        public BackupArchiveException(string message) : base(message)
        {
        }

        public BackupArchiveException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
