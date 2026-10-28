using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// Loads the OpenIddict signing / encryption certificate from configuration so the authority survives a
    /// process restart (D66). When NOTHING is configured every method returns <c>null</c> and the caller falls
    /// back to the pre-D66 ephemeral keys — so a stock install (and every existing test) is byte-for-byte
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// <para>Two shapes are accepted per cert, checked in this order:</para>
    /// <list type="number">
    /// <item>A PKCS#12 bundle: <c>OpenIddict:SigningCertificatePath</c> (+ optional
    /// <c>:SigningCertificatePassword</c>). A single <c>.pfx</c> the installer generates.</item>
    /// <item>A PEM pair: <c>OpenIddict:SigningCertificatePemPath</c> + <c>:SigningCertificateKeyPath</c> — the
    /// <c>openssl</c>-friendly shape for a Linux VM.</item>
    /// </list>
    /// <para>The encryption cert mirrors the same keys under the <c>Encryption*</c> prefix. A single PFX that
    /// carries both <c>digitalSignature</c> and <c>keyEncipherment</c> usages may be pointed at both; the
    /// installer generates exactly that.</para>
    /// <para>Why the export-and-reload dance: <see cref="X509Certificate2.CreateFromPemFile(string,string)"/>
    /// yields a cert whose private-key handle is not usable for signing on Windows unless it is round-tripped
    /// through PKCS#12 first — the documented cross-platform fix. It is a no-op cost on a startup path.</para>
    /// </remarks>
    public static class OpenIddictCertificateLoader
    {
        public const string SigningCertificatePathKey = "OpenIddict:SigningCertificatePath";
        public const string SigningCertificatePasswordKey = "OpenIddict:SigningCertificatePassword";
        public const string SigningCertificatePemPathKey = "OpenIddict:SigningCertificatePemPath";
        public const string SigningCertificateKeyPathKey = "OpenIddict:SigningCertificateKeyPath";

        public const string EncryptionCertificatePathKey = "OpenIddict:EncryptionCertificatePath";
        public const string EncryptionCertificatePasswordKey = "OpenIddict:EncryptionCertificatePassword";
        public const string EncryptionCertificatePemPathKey = "OpenIddict:EncryptionCertificatePemPath";
        public const string EncryptionCertificateKeyPathKey = "OpenIddict:EncryptionCertificateKeyPath";

        /// <summary>
        /// The persistent signing certificate, or <c>null</c> when none is configured (ephemeral fallback).
        /// </summary>
        public static X509Certificate2 TryLoadSigningCertificate(IConfiguration configuration)
            => TryLoad(
                configuration,
                SigningCertificatePathKey, SigningCertificatePasswordKey,
                SigningCertificatePemPathKey, SigningCertificateKeyPathKey);

        /// <summary>
        /// The persistent encryption certificate, or <c>null</c> when none is configured (ephemeral fallback).
        /// </summary>
        public static X509Certificate2 TryLoadEncryptionCertificate(IConfiguration configuration)
            => TryLoad(
                configuration,
                EncryptionCertificatePathKey, EncryptionCertificatePasswordKey,
                EncryptionCertificatePemPathKey, EncryptionCertificateKeyPathKey);

        private static X509Certificate2 TryLoad(
            IConfiguration configuration,
            string pfxPathKey, string pfxPasswordKey, string pemCertKey, string pemKeyKey)
        {
            var pfxPath = configuration[pfxPathKey];
            if (!string.IsNullOrWhiteSpace(pfxPath))
            {
                if (!File.Exists(pfxPath))
                {
                    throw new FileNotFoundException(
                        $"The OpenIddict certificate configured at '{pfxPathKey}' was not found.", pfxPath);
                }

                var password = configuration[pfxPasswordKey];
                return X509CertificateLoader.LoadPkcs12FromFile(
                    pfxPath, password, X509KeyStorageFlags.EphemeralKeySet);
            }

            var pemCertPath = configuration[pemCertKey];
            var pemKeyPath = configuration[pemKeyKey];
            if (!string.IsNullOrWhiteSpace(pemCertPath))
            {
                if (string.IsNullOrWhiteSpace(pemKeyPath))
                {
                    throw new InvalidOperationException(
                        $"'{pemCertKey}' is set but '{pemKeyKey}' is not — a PEM certificate needs its private key.");
                }
                if (!File.Exists(pemCertPath))
                {
                    throw new FileNotFoundException(
                        $"The OpenIddict PEM certificate configured at '{pemCertKey}' was not found.", pemCertPath);
                }
                if (!File.Exists(pemKeyPath))
                {
                    throw new FileNotFoundException(
                        $"The OpenIddict PEM private key configured at '{pemKeyKey}' was not found.", pemKeyPath);
                }

                using var fromPem = X509Certificate2.CreateFromPemFile(pemCertPath, pemKeyPath);
                // Round-trip through PKCS#12 so the private key is usable for signing on Windows too.
                var pkcs12 = fromPem.Export(X509ContentType.Pkcs12);
                return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.EphemeralKeySet);
            }

            return null;
        }
    }
}
