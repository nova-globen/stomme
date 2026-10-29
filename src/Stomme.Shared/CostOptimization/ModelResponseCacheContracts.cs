#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Lifted from the predecessor platform: the key and the port are unchanged; BCL-only like every Shared contract.
namespace Stomme.CostOptimization
{
    /// <summary>
    /// One prompt message (role + content) contributing to a <see cref="ModelResponseCacheKey"/>. The
    /// consumer (the AI assistant, BL-97 increments 9-10) maps its provider-specific message type onto this neutral
    /// shape so the key stays provider-library free.
    /// </summary>
    /// <param name="Role">The message's chat role (e.g. <c>system</c>, <c>user</c>, <c>assistant</c>).</param>
    /// <param name="Content">The message's text content.</param>
    public sealed record ModelPromptMessage(string Role, string Content);

    /// <summary>
    /// Identity of one deterministic model call: the SHA-256 (lowercase hex) of the canonical
    /// (provider, model, messages, temperature) tuple. The hash input length-prefixes every field, so the
    /// key is order-sensitive and free of field-boundary collisions — any change to the provider, the
    /// model, a message's role or content, the message order, or the temperature yields a different key.
    /// </summary>
    public sealed record ModelResponseCacheKey
    {
        private ModelResponseCacheKey(string value) => Value = value;

        /// <summary>The 64-character lowercase-hex SHA-256 key value.</summary>
        public string Value { get; }

        /// <summary>
        /// Computes the key for one model call. <paramref name="provider"/> and <paramref name="model"/>
        /// are the effective values the call would be dispatched with (after any auto-tiering decision);
        /// <paramref name="messages"/> are the fully composed prompt messages in dispatch order.
        /// </summary>
        public static ModelResponseCacheKey Create(
            string provider,
            string model,
            IReadOnlyList<ModelPromptMessage> messages,
            float temperature)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            ArgumentNullException.ThrowIfNull(messages);

            var canonical = new StringBuilder();
            AppendField(canonical, provider);
            AppendField(canonical, model);
            AppendField(canonical, temperature.ToString("R", CultureInfo.InvariantCulture));
            foreach (var message in messages)
            {
                ArgumentNullException.ThrowIfNull(message);
                AppendField(canonical, message.Role);
                AppendField(canonical, message.Content);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
            return new ModelResponseCacheKey(Convert.ToHexStringLower(hash));
        }

        /// <summary>Length-prefixes the field (netstring style) so concatenated fields cannot collide.</summary>
        private static void AppendField(StringBuilder canonical, string value) =>
            canonical.Append(value.Length).Append(':').Append(value).Append(';');
    }

    /// <summary>
    /// The minimal faithful unit of one cached model response: the reply text the caller returns to the
    /// user plus the metering-relevant fields (the responding model and the original token counts), so a
    /// hit can be surfaced and recorded to AI Usage (35) without rehydrating a provider-library object graph.
    /// </summary>
    /// <param name="Text">The assistant's reply text.</param>
    /// <param name="Model">The model that produced the response.</param>
    /// <param name="InputTokens">Prompt tokens the original call consumed.</param>
    /// <param name="OutputTokens">Completion tokens the original call produced.</param>
    public sealed record CachedModelResponse(string Text, string Model, long InputTokens, long OutputTokens);

    /// <summary>
    /// Outcome of one <see cref="IModelResponseCache.GetOrCreateAsync"/> call: the response and whether it
    /// was served from the cache (hit — no provider call was made) or produced by the factory (miss).
    /// </summary>
    /// <param name="Response">The cached or freshly produced response.</param>
    /// <param name="FromCache">Whether the response was served from the cache.</param>
    public sealed record ModelResponseCacheResult(CachedModelResponse Response, bool FromCache);

    /// <summary>
    /// The LLM response-cache port (spec §"Response Caching", cache-aside over the cache building block,
    /// feature 06). The consumer decides <em>whether</em> a call is cacheable (deterministic-only:
    /// temperature ≤ 0) and computes the <see cref="ModelResponseCacheKey"/>; this port owns the store
    /// policy (enabled switch, TTL). A hit returns the stored response without running the factory; a miss
    /// runs the factory (which dispatches the provider call) and stores its result.
    /// </summary>
    public interface IModelResponseCache
    {
        /// <summary>Returns the cached response for <paramref name="key"/> or produces and stores one via <paramref name="factory"/>.</summary>
        Task<ModelResponseCacheResult> GetOrCreateAsync(
            ModelResponseCacheKey key,
            Func<CancellationToken, Task<CachedModelResponse>> factory,
            CancellationToken ct = default);
    }
}
