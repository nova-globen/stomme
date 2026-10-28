#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - the site's variables (<see cref="PipelineSiteVariables"/>), one JSON object in the engine's key-value store under the
    /// SITE (a key-value key is one row per installation - the schedules' lesson).
    /// </summary>
    public static class PipelineSiteVariableStore
    {
        public const string KeyPrefix = "pipelines:vars:";

        public static string Key(string? tenantId) => KeyPrefix + tenantId;

        public static async Task<Dictionary<string, string>> ReadAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var stored = await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId) }, ct);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stored?.SerializedValue ?? "{}") ?? new Dictionary<string, string>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(); // written by this class only; unreadable is none rather than a broken run
            }
        }

        public static async Task SaveAsync(IKeyValueStore keyValues, string? tenantId, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
        {
            if (!PipelineSiteVariables.Check(variables, out var error))
            {
                throw new ArgumentException(error);
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId), SerializedValue = JsonSerializer.Serialize(variables) }, ct);
        }
    }

    /// <summary>The site's variables, read once per scope - a run reads them for every expression it evaluates, so the store answers once.</summary>
    public sealed class PipelineSiteVariablesCache(IKeyValueStore keyValues, ITenantAccessor tenants)
    {
        private Dictionary<string, string>? _read;

        public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct) =>
            _read ??= await PipelineSiteVariableStore.ReadAsync(keyValues, tenants.TenantId, ct);
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetSiteVariablesQuery : IQuery<Dictionary<string, string>>;

    public sealed class GetSiteVariablesQueryHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : IQueryHandler<GetSiteVariablesQuery, Dictionary<string, string>>
    {
        public Task<Dictionary<string, string>> Handle(GetSiteVariablesQuery request, CancellationToken ct) =>
            PipelineSiteVariableStore.ReadAsync(keyValues, tenants.TenantId, ct);
    }

    /// <summary>Replaces the site's variables; a name or a value that cannot be one is refused with the reason.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SetSiteVariablesCommand(Dictionary<string, string>? Variables) : ICommand<Dictionary<string, string>>;

    public sealed class SetSiteVariablesCommandHandler(IKeyValueStore keyValues, ITenantAccessor tenants) : ICommandHandler<SetSiteVariablesCommand, Dictionary<string, string>>
    {
        public async Task<Dictionary<string, string>> Handle(SetSiteVariablesCommand request, CancellationToken ct)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in request.Variables ?? new Dictionary<string, string>())
            {
                variables[(pair.Key ?? "").Trim()] = pair.Value ?? "";
            }

            await PipelineSiteVariableStore.SaveAsync(keyValues, tenants.TenantId, variables, ct);
            return variables;
        }
    }
}
