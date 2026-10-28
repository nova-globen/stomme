#nullable enable
using System.Collections.Generic;

namespace Stomme.AiAssistant
{
    /// <summary>
    /// The assistant's keys (BL-97 increment 10b, D113). <c>assistant:use</c> - start a conversation, send, apply or discard a
    /// proposal - grants NO authority of its own: what an agent reads, and what an apply writes, is still decided by the owning
    /// module's key for that user. <c>assistant:configure</c> - the site's switch, provider, endpoint, model and API key.
    /// Administrators by default; a site grants them to more roles as it chooses.
    /// </summary>
    public static class AssistantPermissions
    {
        public const string Resource = "assistant";

        public const string Use = "assistant:use";

        public const string Configure = "assistant:configure";

        public static readonly IReadOnlyList<string> All = new[] { Use, Configure };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (Use, new[] { "Administrators" }),
            (Configure, new[] { "Administrators" }),
        };
    }

    /// <summary>A message the user sends.</summary>
    public sealed record SendAssistantMessageRequest(string Message);

    /// <summary>
    /// Why a turn or an apply was refused, safe to show: the guardrail's reason (<c>PromptInjection</c>,
    /// <c>RateLimitExceeded</c>, <c>Unauthorized</c>), the model's (<c>Disabled</c>, <c>NotConfigured</c>, <c>QuotaExceeded</c>...),
    /// <c>Conflict</c> for a conversation or proposal in the wrong state, <c>Invalid</c> for a request that does not validate.
    /// Never a model's words.
    /// </summary>
    public sealed record AssistantRefusalView(string Code, string Message);

    /// <summary>
    /// The site's assistant configuration as its administrator sees it. The API key is NEVER in it - only whether one is
    /// stored (<c>Stored</c>, <c>None</c>, or <c>Unreadable</c> when the site's secret store can no longer read it).
    /// </summary>
    public sealed record AssistantSettingsView(
        bool InstallationEnabled,
        bool Enabled,
        string Provider,
        string Endpoint,
        string? Model,
        float? Temperature,
        string KeyStatus,
        IReadOnlyList<string> Providers);

    /// <summary>
    /// A change to the site's assistant configuration. A non-empty <see cref="ApiKey"/> replaces the stored key; an empty one
    /// keeps it; <see cref="ClearApiKey"/> removes it.
    /// </summary>
    public sealed record SaveAssistantSettingsRequest(bool Enabled, string Provider, string Endpoint, string? Model, float? Temperature, string? ApiKey = null, bool ClearApiKey = false);
}
