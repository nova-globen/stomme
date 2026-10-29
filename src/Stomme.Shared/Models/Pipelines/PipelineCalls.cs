#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A run that waits for another system to call it (n8n's Wait node resumed "on webhook call"): "Call address" gives
    /// the run an address - a random 256-bit token after <see cref="Path"/> - to hand to that system, and "Wait for a call" parks
    /// the run until a POST reaches it; the call's body is the result. The token is kept on the run; the bookmark holds only its
    /// SHA-256 (<see cref="Hash"/>), and the endpoint hashes the token it is given into the same stimulus - so nothing has to be
    /// decrypted and an address survives a restart. A call that comes before the run reaches its wait is queued by the engine
    /// for a minute (its bookmark queue).
    /// </summary>
    public static class PipelineCalls
    {
        public const string WaitStepType = "Stomme.WaitForCall";

        public const string AddressStepType = "Stomme.CallAddress";

        public const string Called = "Called";

        public const string TimedOut = "Timed out";

        public const string TimeOutInput = "TimeOutAfter";

        /// <summary>The path a call address ends with, before its token (the site's own address before it).</summary>
        public const string Path = "api/Pipelines/calls/";

        /// <summary>What a call is resumed with - for that one resumption, never stored as the run's input.</summary>
        public const string BodyKey = "stomme:call:body";

        public const string ContentTypeKey = "stomme:call:contentType";

        public const string TimedOutKey = "stomme:call:timedOut";

        /// <summary>The length of a token: 32 random bytes, base64url without padding.</summary>
        public const int TokenLength = 43;

        /// <summary>A new token for a run's call address.</summary>
        public static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Whether the text can be a token at all - checked before anything is looked up.</summary>
        public static bool IsToken(string? token) =>
            token is { Length: TokenLength } && token.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_');

        /// <summary>What the bookmark keeps of a token: its SHA-256, in lower-case hex.</summary>
        public static string Hash(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
