#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// A model that plays a SCRIPT of assistant turns - a tool call, then an answer - and lets the real function-invocation
    /// loop drive the tools in between (the predecessor platform's <c>ScriptedChatClient</c>). It records every call's model and messages.
    /// </summary>
    internal sealed class TurnScriptChatClient : IChatClient
    {
        private readonly IReadOnlyList<ChatMessage> _turns;

        public TurnScriptChatClient(params ChatMessage[] turns) => _turns = turns;

        public int Turns { get; private set; }

        public List<string?> ModelIds { get; } = new();

        public List<IReadOnlyList<ChatMessage>> Received { get; } = new();

        public List<ChatOptions?> Options { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ModelIds.Add(options?.ModelId);
            Options.Add(options);
            Received.Add(messages.ToList());
            var message = _turns[Math.Min(Turns++, _turns.Count - 1)];
            return Task.FromResult(new ChatResponse(message)
            {
                ModelId = options?.ModelId,
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public static ChatMessage Call(string callId, string function, IDictionary<string, object?> arguments) =>
            new(ChatRole.Assistant, [new FunctionCallContent(callId, function, arguments)]);

        public static ChatMessage Call<T>(string callId, string function, string parameter, T value) =>
            Call(callId, function, new Dictionary<string, object?> { [parameter] = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });

        public static ChatMessage Answer(string text) => new(ChatRole.Assistant, text);

        /// <summary>The text of every tool RESULT the loop fed back to the model, in order.</summary>
        public IReadOnlyList<string> ToolResults() =>
            Received.LastOrDefault()?.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Select(result => result.Result?.ToString() ?? string.Empty).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// The site's model as <see cref="AssistantModelAccess"/> hands it out - the REAL metered, quota-gated client around a
    /// scripted one - without a provider or an egress; or, switched off, the refusal an agent must surface.
    /// </summary>
    internal sealed class ScriptedModelAccess(IChatClient model, float? temperature = null) : IAssistantModelAccess
    {
        public bool Enabled { get; set; } = true;

        public ScriptedQuotas Quotas { get; } = new();

        public RecordingAiUsage Usage { get; } = new();

        public List<string> Opened { get; } = new();

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task<AssistantModel> OpenAsync(string agent, CancellationToken cancellationToken = default)
        {
            if (!Enabled)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.Disabled, "The assistant is switched off for this site.");
            }

            Opened.Add(agent);
            return Task.FromResult(new AssistantModel(new MeteredChatClient(model, "test", "test-model", agent, Quotas, Usage, NullLogger.Instance), "test", "test-model", temperature));
        }
    }

    /// <summary>The workspace as a user sees it: what the test put there, and a record of what was asked.</summary>
    internal sealed class RecordingQueryGateway : IAssistantQueryGateway
    {
        public List<AssistantConnectionInfo> Connections { get; } = new();

        public List<AssistantDatasetInfo> Datasets { get; } = new();

        public Dictionary<Guid, AssistantDataSummary> Summaries { get; } = new();

        public List<AssistantStepType> StepTypes { get; } = new();

        public List<AssistantHelpTopic> Help { get; } = new();

        public bool ListDatasetsCalled { get; private set; }

        public List<string> HelpQueries { get; } = new();

        public Task<IReadOnlyList<AssistantConnectionInfo>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssistantConnectionInfo>>(Connections);

        public Task<IReadOnlyList<AssistantDatasetInfo>> ListDatasetsAsync(CancellationToken cancellationToken = default)
        {
            ListDatasetsCalled = true;
            return Task.FromResult<IReadOnlyList<AssistantDatasetInfo>>(Datasets);
        }

        public Task<AssistantDataSummary?> GetDataSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Summaries.TryGetValue(datasetId, out var summary) ? summary : null);

        public Task<IReadOnlyList<AssistantStepType>> ListStepTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssistantStepType>>(StepTypes);

        public Task<IReadOnlyList<AssistantHelpTopic>> SearchHelpAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            HelpQueries.Add(query);
            return Task.FromResult<IReadOnlyList<AssistantHelpTopic>>(Help.Take(limit).ToList());
        }

        /// <summary>A dataset the user can see, with a summary of the named columns.</summary>
        public Guid AddDataset(string name, params string[] columns)
        {
            var id = Guid.NewGuid();
            Datasets.Add(new AssistantDatasetInfo(id, name, "Internal", 120));
            Summaries[id] = new AssistantDataSummary(id, columns.Select(column => new AssistantColumnSummary(column, new[] { "a", "b" }, null, null, 2, 0)).ToList(), 120);
            return id;
        }
    }

    /// <summary>Records every judgement and write; the verdict is the test's.</summary>
    internal sealed class RecordingCommandGateway : IAssistantCommandGateway
    {
        public int ValidateCount { get; private set; }

        public List<AssistantPipelineDraft> CreatedPipelines { get; } = new();

        public List<AssistantDashboardDraft> CreatedDashboards { get; } = new();

        public string CreatedPipelineId { get; } = "pipeline-" + Guid.NewGuid().ToString("N");

        public Guid CreatedDashboardId { get; } = Guid.NewGuid();

        public Func<AssistantPipelineDraft, AssistantPipelineValidationResult>? Validator { get; init; }

        public Exception? FailCreate { get; set; }

        public Task<AssistantPipelineValidationResult> ValidatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            return Task.FromResult(Validator?.Invoke(draft) ?? AssistantPipelineValidationResult.Valid);
        }

        public Task<string> CreatePipelineAsync(AssistantPipelineDraft draft, CancellationToken cancellationToken = default)
        {
            if (FailCreate is not null)
            {
                throw FailCreate;
            }

            CreatedPipelines.Add(draft);
            return Task.FromResult(CreatedPipelineId);
        }

        public Task<Guid> CreateDashboardAsync(AssistantDashboardDraft draft, CancellationToken cancellationToken = default)
        {
            if (FailCreate is not null)
            {
                throw FailCreate;
            }

            CreatedDashboards.Add(draft);
            return Task.FromResult(CreatedDashboardId);
        }
    }

    /// <summary>the predecessor platform's <c>FakeResponseCache</c>: every key asked, the entries stored, or one primed answer for any key.</summary>
    internal sealed class FakeResponseCache : IModelResponseCache
    {
        private CachedModelResponse? _primed;

        public List<string> ObservedKeys { get; } = new();

        public Dictionary<string, CachedModelResponse> Entries { get; } = new();

        public void Prime(CachedModelResponse response) => _primed = response;

        public async Task<ModelResponseCacheResult> GetOrCreateAsync(ModelResponseCacheKey key, Func<CancellationToken, Task<CachedModelResponse>> factory, CancellationToken ct = default)
        {
            ObservedKeys.Add(key.Value);
            if (_primed is not null)
            {
                return new ModelResponseCacheResult(_primed, FromCache: true);
            }

            if (Entries.TryGetValue(key.Value, out var hit))
            {
                return new ModelResponseCacheResult(hit, FromCache: true);
            }

            var created = await factory(ct);
            Entries[key.Value] = created;
            return new ModelResponseCacheResult(created, FromCache: false);
        }
    }

    internal sealed class FixedModelTierRouter(ModelTierDecision decision) : IModelTierRouter
    {
        public ModelTierRequest? LastRequest { get; private set; }

        public Task<ModelTierDecision> RouteAsync(ModelTierRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(decision);
        }
    }

    /// <summary>A specialist that answers with a fixed reply and counts its invocations - the orchestrator's routing target.</summary>
    internal sealed class RecordingSpecialist(string name, AssistantReply reply) : IAssistantSpecialist
    {
        public string Name { get; } = name;

        public string Description => "a test specialist named " + Name + ".";

        public int Invocations { get; private set; }

        public Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(reply);
        }
    }

    /// <summary>Counts the saves - where a service dispatches the domain events of what it wrote.</summary>
    internal sealed class SavingUnitOfWork : Stomme.SharedKernel.Domain.IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            Saves++;
            return Task.FromResult(0);
        }

        public void Dispose()
        {
        }
    }

    internal static class AssistantRequests
    {
        public static AssistantRequest Of(string text) => new(Guid.NewGuid(), [new AssistantMessage(ChatRole.User, text)]);

        public static TenantId Tenant { get; } = TenantId.Default;
    }
}
