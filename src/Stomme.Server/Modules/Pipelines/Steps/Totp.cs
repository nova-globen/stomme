#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What <see cref="Totp"/> does with a secret.</summary>
    public enum TotpOperation
    {
        /// <summary>The code an authenticator app shows now, and the seconds it has left.</summary>
        Generate,

        /// <summary>Whether a code a person gave is the current one (or one a period either side).</summary>
        Verify,
    }

    /// <summary>The hash a TOTP code is made with - SHA-1 is what nearly every authenticator app uses.</summary>
    public enum TotpAlgorithm
    {
        Sha1,
        Sha256,
        Sha512,
    }

    /// <summary>
    /// TOTP (BL-229, n8n's TOTP node, and past it): the time-based one-time code of an authenticator secret (RFC 6238) - the
    /// code an app shows now and the seconds it has left (n8n's <c>token</c> and <c>secondsRemaining</c>), for a pipeline that
    /// signs in to a system guarded by one - or whether a code a person gave is the current one, a period either side allowed
    /// (n8n has no check). The secret is base32 as an app shows it (spaces, dashes and case aside), or the <c>otpauth://</c> link
    /// a QR code holds, whose algorithm, digits and period then win. The result is JSON text: <c>{ token, secondsRemaining }</c>,
    /// or <c>{ valid, drift }</c> - the periods the code was off by. A code checked twice within its period is valid twice: this
    /// step remembers nothing. The secret may hold a secret, so a run's data shows it hidden.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Make an authenticator secret's current one-time code (TOTP), or check a code a person gave.", DisplayName = "TOTP")]
    public class Totp : DataStep
    {
        [Input(Description = "Generate | Verify.", DefaultValue = TotpOperation.Generate, Order = 1)]
        public Input<TotpOperation> Operation { get; set; } = new(new Literal<TotpOperation>(TotpOperation.Generate));

        [Input(Description = "The secret, base32 as an authenticator app shows it - or the otpauth:// link its QR code holds. Shown hidden in a run's data.", CanContainSecrets = true, Order = 2)]
        public Input<string?>? Secret { get; set; }

        [Input(Description = "Verify: the code a person gave. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Code { get; set; }

        [Input(Description = "Sha1 | Sha256 | Sha512 - SHA-1 for nearly every app.", DefaultValue = TotpAlgorithm.Sha1, Order = 4)]
        public Input<TotpAlgorithm> Algorithm { get; set; } = new(new Literal<TotpAlgorithm>(TotpAlgorithm.Sha1));

        [Input(Description = "How many digits a code has: 6 to 8.", DefaultValue = 6, Order = 5)]
        public Input<int> Digits { get; set; } = new(new Literal<int>(6));

        [Input(Description = "Seconds a code lasts: 15 to 300.", DefaultValue = 30, Order = 6)]
        public Input<int> Period { get; set; } = new(new Literal<int>(30));

        [Input(Description = "Verify: periods either side of now a code may come from, for a slow person or clock: 0 to 5.", DefaultValue = 1, Order = 7)]
        public Input<int> Window { get; set; } = new(new Literal<int>(1));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            try
            {
                var settings = TotpCodes.Read(Secret?.GetOrDefault(context), Algorithm.GetOrDefault(context), Digits.GetOrDefault(context), Period.GetOrDefault(context));
                var now = DateTimeOffset.UtcNow;
                if (Operation.GetOrDefault(context) == TotpOperation.Generate)
                {
                    var (token, left) = TotpCodes.Generate(settings, now);
                    await SucceedAsync(context, new JsonObject { ["token"] = token, ["secondsRemaining"] = left }.ToJsonString()).ConfigureAwait(false);
                    return;
                }

                var drift = TotpCodes.Verify(settings, TextOrLastResult(context, Code), now, Window.GetOrDefault(context));
                await SucceedAsync(context, new JsonObject { ["valid"] = drift is not null, ["drift"] = drift }.ToJsonString()).ConfigureAwait(false);
            }
            catch (TotpProblem problem)
            {
                await FailAsync(context, "TOTP_INVALID", problem.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why a secret or its settings cannot make a code.</summary>
    public sealed class TotpProblem(string message) : Exception(message);

    /// <summary>A secret and how its codes are made.</summary>
    public sealed record TotpSettings(byte[] Key, TotpAlgorithm Algorithm, int Digits, int Period);

    /// <summary>RFC 6238 time-based one-time codes, on RFC 4226's HMAC truncation - .NET's HMACs, no library.</summary>
    public static class TotpCodes
    {
        /// <summary>The secret and its settings - an <c>otpauth://</c> link's own winning - or the refusal.</summary>
        public static TotpSettings Read(string? secret, TotpAlgorithm algorithm, int digits, int period)
        {
            var text = (secret ?? "").Trim();
            if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                var query = QueryHelpers.ParseQuery(new Uri(text).Query);
                text = query.TryGetValue("secret", out var named) ? named.ToString() : "";
                if (query.TryGetValue("algorithm", out var hash) && !Enum.TryParse(hash.ToString(), ignoreCase: true, out algorithm))
                {
                    throw new TotpProblem($"The link's algorithm {hash} is not SHA1, SHA256 or SHA512.");
                }

                digits = query.TryGetValue("digits", out var many) && int.TryParse(many, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : digits;
                period = query.TryGetValue("period", out var seconds) && int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : period;
            }

            if (digits is < 6 or > 8)
            {
                throw new TotpProblem($"Digits: 6 to 8 - not {digits}.");
            }

            if (period is < 15 or > 300)
            {
                throw new TotpProblem($"Period: 15 to 300 seconds - not {period}.");
            }

            var key = Base32(text);
            if (key.Length < 10)
            {
                throw new TotpProblem("The secret is base32 of at least 10 bytes (16 characters), as an authenticator app shows it.");
            }

            return new TotpSettings(key, algorithm, digits, period);
        }

        /// <summary>The code for a moment, and the seconds it has left.</summary>
        public static (string Token, int SecondsRemaining) Generate(TotpSettings settings, DateTimeOffset now)
        {
            var seconds = now.ToUnixTimeSeconds();
            return (Code(settings, seconds / settings.Period), settings.Period - (int)(seconds % settings.Period));
        }

        /// <summary>The periods a code is off by when it is one of the window's - null when it is none of them.</summary>
        public static int? Verify(TotpSettings settings, string? code, DateTimeOffset now, int window)
        {
            var given = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (given.Length != settings.Digits || !given.All(char.IsAsciiDigit))
            {
                return null;
            }

            var counter = now.ToUnixTimeSeconds() / settings.Period;
            var reach = Math.Clamp(window, 0, 5);
            int? found = null;
            for (var drift = -reach; drift <= reach; drift++)
            {
                // Every period compared, in time that does not depend on which one matched.
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(settings, counter + drift)), Encoding.ASCII.GetBytes(given)) && found is null)
                {
                    found = drift;
                }
            }

            return found;
        }

        /// <summary>RFC 4226's code for a counter: the HMAC of its 8 bytes, dynamically truncated, the last digits.</summary>
        public static string Code(TotpSettings settings, long counter)
        {
            var message = new byte[8];
            for (var i = 7; i >= 0; i--, counter >>= 8)
            {
                message[i] = (byte)(counter & 0xff);
            }

            var hash = settings.Algorithm switch
            {
                TotpAlgorithm.Sha256 => HMACSHA256.HashData(settings.Key, message),
                TotpAlgorithm.Sha512 => HMACSHA512.HashData(settings.Key, message),
                _ => HMACSHA1.HashData(settings.Key, message),
            };
            var offset = hash[^1] & 0x0f;
            var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            var modulo = (int)Math.Pow(10, settings.Digits);
            return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(settings.Digits, '0');
        }

        /// <summary>Base32 (RFC 4648) as apps show it: case, spaces, dashes and padding aside - or the refusal.</summary>
        public static byte[] Base32(string text)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var clean = new string(text.ToUpperInvariant().Where(c => c is not (' ' or '-' or '=')).ToArray());
            var bytes = new byte[clean.Length * 5 / 8];
            int buffer = 0, bits = 0, at = 0;
            foreach (var c in clean)
            {
                var value = alphabet.IndexOf(c);
                if (value < 0)
                {
                    // The character is not repeated: it is part of a secret.
                    throw new TotpProblem("The secret holds a character base32 does not - it is letters A to Z and digits 2 to 7.");
                }

                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[at++] = (byte)((buffer >> bits) & 0xff);
                }
            }

            return bytes;
        }
    }
}
