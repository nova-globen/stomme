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
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// A run's data (<see cref="PipelineRunData"/>), kept in the engine's key-value store under the SITE and the run (a
    /// key-value key is one row per installation - the schedules' lesson), one JSON object per run. Read for one run, or for every
    /// run of the site at once (the runs list and its search); deleted with the run.
    /// </summary>
    public static class PipelineRunDataStore
    {
        public const string KeyPrefix = "pipelines:rundata:";

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string runId) => Prefix(tenantId) + runId;

        /// <summary>Adds the pairs to the run's data (a key given again replaces its value); refused, with the reason, past the limits.</summary>
        public static async Task<Dictionary<string, string>> MergeAsync(IKeyValueStore keyValues, string? tenantId, string runId, IReadOnlyDictionary<string, string> pairs, CancellationToken ct)
        {
            var data = await ReadAsync(keyValues, tenantId, runId, ct);
            foreach (var pair in pairs)
            {
                data[pair.Key] = pair.Value;
            }

            if (!PipelineRunData.Check(data, out var error))
            {
                throw new ArgumentException(error);
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId, runId), SerializedValue = JsonSerializer.Serialize(data) }, ct);
            return data;
        }

        public static async Task<Dictionary<string, string>> ReadAsync(IKeyValueStore keyValues, string? tenantId, string runId, CancellationToken ct) =>
            Read((await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, runId) }, ct))?.SerializedValue);

        /// <summary>Every run of the site that has data: its id and its data.</summary>
        public static async Task<Dictionary<string, Dictionary<string, string>>> ReadAllAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = Prefix(tenantId);
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct))
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    all[pair.Key[prefix.Length..]] = Read(pair.SerializedValue);
                }
            }

            return all;
        }

        /// <summary>Deletes what the runs kept about themselves - and what people said about them (their reviews) with it.</summary>
        public static async Task DeleteAsync(IKeyValueStore keyValues, string? tenantId, IEnumerable<string> runIds, CancellationToken ct)
        {
            var ids = runIds.ToList();
            foreach (var runId in ids)
            {
                await keyValues.DeleteAsync(Key(tenantId, runId), ct);
            }

            await PipelineRunReviewStore.DeleteAsync(keyValues, tenantId, ids, ct);
        }

        // Written by this class only; anything unreadable is no data rather than a broken page.
        private static Dictionary<string, string> Read(string? json)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}") ?? new Dictionary<string, string>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
