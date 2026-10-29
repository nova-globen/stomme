#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>One remembered message of an AI agent's conversation: who said it and what.</summary>
    public sealed record AgentMemoryMessage(string Role, string Text);

    /// <summary>
    /// n8n's Simple Memory for its AI Agent: the conversation an <see cref="Steps.AiAgent"/> keeps under a key - a customer,
    /// a ticket, a chat - so a later run with the same key continues it. Kept in the engine's key-value store under the SITE, the
    /// PIPELINE and the key (two pipelines never share one by accident; the schedules' lesson about keys), the newest
    /// <see cref="MaxKept"/> messages, text only (tool calls are the run's, not the conversation's).
    /// </summary>
    public static class PipelineAgentMemory
    {
        public const string KeyPrefix = "pipelines:memory:";

        public const int MaxKept = 50;

        public const int MaxKeyLength = 200;

        public static string Key(string? tenantId, string? definitionId, string key) => KeyPrefix + tenantId + ":" + definitionId + ":" + key.Trim();

        /// <summary>The newest messages remembered under the key, oldest first; none for a key never used.</summary>
        public static async Task<List<AgentMemoryMessage>> ReadAsync(IKeyValueStore keyValues, string? tenantId, string? definitionId, string key, int newest, CancellationToken ct)
        {
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, definitionId, key) }, ct);
            var messages = Read(stored?.SerializedValue);
            return messages.Skip(Math.Max(0, messages.Count - Math.Max(0, newest))).ToList();
        }

        /// <summary>Adds an exchange to what is remembered under the key, keeping the newest <see cref="MaxKept"/>.</summary>
        public static async Task AppendAsync(IKeyValueStore keyValues, string? tenantId, string? definitionId, string key, IEnumerable<AgentMemoryMessage> exchange, CancellationToken ct)
        {
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, definitionId, key) }, ct);
            var messages = Read(stored?.SerializedValue);
            messages.AddRange(exchange);
            var kept = messages.Skip(Math.Max(0, messages.Count - MaxKept)).ToList();
            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId, definitionId, key), SerializedValue = JsonSerializer.Serialize(kept) }, ct);
        }

        // Written by this class only; anything unreadable is no memory rather than a failed run.
        private static List<AgentMemoryMessage> Read(string? json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<AgentMemoryMessage>>(json ?? "[]") ?? new List<AgentMemoryMessage>();
            }
            catch (JsonException)
            {
                return new List<AgentMemoryMessage>();
            }
        }
    }
}
