#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What <see cref="Crypto"/> makes.</summary>
    public enum CryptoOperation
    {
        /// <summary>A hash of the value.</summary>
        Hash,

        /// <summary>A keyed hash (HMAC) of the value - what a webhook's signature is.</summary>
        Hmac,

        /// <summary>A new random UUID.</summary>
        Uuid,

        /// <summary>New random text, URL-safe, of the length asked.</summary>
        RandomText,
    }

    /// <summary>The hash a Hash or an HMAC is made with.</summary>
    public enum CryptoAlgorithm
    {
        Sha256,
        Sha512,
        Sha1,
        Md5,
    }

    /// <summary>How a hash is written.</summary>
    public enum CryptoEncoding
    {
        Hex,
        Base64,
    }

    /// <summary>
    /// Crypto (BL-229, n8n's Crypto node): a hash of a value (SHA-256 by default; SHA-512, and SHA-1 and MD5 for the checksums
    /// older systems still ask for), an HMAC of it under a key - how a call to another system is signed, as this site's own
    /// webhooks are - a random UUID, or random URL-safe text. The value is UTF-8 text, the previous step's result when empty;
    /// the key is an input that may hold a secret, so a run's data shows it hidden - better still, a variable a Connection's
    /// step fills. The result is the hash, hex (lower case) or base64.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Hash a value, sign it with an HMAC under a key, or make a random UUID or text.", DisplayName = "Crypto")]
    public class Crypto : DataStep
    {
        [Input(Description = "Hash | Hmac | Uuid | RandomText.", DefaultValue = CryptoOperation.Hash, Order = 1)]
        public Input<CryptoOperation> Operation { get; set; } = new(new Literal<CryptoOperation>(CryptoOperation.Hash));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Hash and Hmac: the text. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Value { get; set; }

        [Input(Description = "Hash and Hmac: Sha256 | Sha512 | Sha1 | Md5.", DefaultValue = CryptoAlgorithm.Sha256, Order = 3)]
        public Input<CryptoAlgorithm> Algorithm { get; set; } = new(new Literal<CryptoAlgorithm>(CryptoAlgorithm.Sha256));

        [Input(Description = "Hmac: the key. Shown hidden in a run's data.", CanContainSecrets = true, Order = 4)]
        public Input<string?>? Key { get; set; }

        [Input(Description = "Hash and Hmac: Hex | Base64.", DefaultValue = CryptoEncoding.Hex, Order = 5)]
        public Input<CryptoEncoding> Encoding { get; set; } = new(new Literal<CryptoEncoding>(CryptoEncoding.Hex));

        [Input(Description = "RandomText: how many characters.", DefaultValue = 32, Order = 6)]
        public Input<int> Length { get; set; } = new(new Literal<int>(32));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            switch (Operation.GetOrDefault(context))
            {
                case CryptoOperation.Uuid:
                    await SucceedAsync(context, Guid.NewGuid().ToString()).ConfigureAwait(false);
                    return;
                case CryptoOperation.RandomText:
                    var length = Length.GetOrDefault(context);
                    if (length is < 1 or > 1024)
                    {
                        await FailAsync(context, "CRYPTO_INVALID", "Length: from 1 to 1024 characters.").ConfigureAwait(false);
                        return;
                    }

                    var text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                    await SucceedAsync(context, text[..length]).ConfigureAwait(false);
                    return;
            }

            var data = System.Text.Encoding.UTF8.GetBytes(TextOrLastResult(context, Value) ?? string.Empty);
            var algorithm = Algorithm.GetOrDefault(context);
            byte[] hash;
            if (Operation.GetOrDefault(context) == CryptoOperation.Hmac)
            {
                var key = Key?.GetOrDefault(context);
                if (string.IsNullOrEmpty(key))
                {
                    await FailAsync(context, "CRYPTO_INVALID", "Hmac needs a Key.").ConfigureAwait(false);
                    return;
                }

                var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
                hash = algorithm switch
                {
                    CryptoAlgorithm.Sha512 => HMACSHA512.HashData(keyBytes, data),
                    CryptoAlgorithm.Sha1 => HMACSHA1.HashData(keyBytes, data),
                    CryptoAlgorithm.Md5 => HMACMD5.HashData(keyBytes, data),
                    _ => HMACSHA256.HashData(keyBytes, data),
                };
            }
            else
            {
                hash = algorithm switch
                {
                    CryptoAlgorithm.Sha512 => SHA512.HashData(data),
                    CryptoAlgorithm.Sha1 => SHA1.HashData(data),
                    CryptoAlgorithm.Md5 => MD5.HashData(data),
                    _ => SHA256.HashData(data),
                };
            }

            var written = Encoding.GetOrDefault(context) == CryptoEncoding.Base64 ? Convert.ToBase64String(hash) : Convert.ToHexStringLower(hash);
            await SucceedAsync(context, written).ConfigureAwait(false);
        }
    }
}
