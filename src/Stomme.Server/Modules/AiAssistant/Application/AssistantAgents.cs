#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.AiAssistant.Domain;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    // ============================================================================================================
    // The agents. What holds for all of them:
    // - The model is the CURRENT SITE's, opened through IAssistantModelAccess, whose client quota-gates every call before it
    //   is made and meters it after (MeteredChatClient) - so no agent records usage itself, and a
    //   tool-calling loop is metered per MODEL CALL (each round), not as one aggregate row per turn.
    // - An unavailable model throws AssistantUnavailableException with its reason.
    // - The workspace tools read through the owning modules' gated queries as the user (IAssistantQueryGateway).
    // ============================================================================================================

    /// <summary>What every agent turn shares: the composed prompt, and the model-tier hook.</summary>
    internal static class AssistantTurn
    {
        /// <summary>Chars-per-token for the prompt-size estimate; tiering needs no tokenizer.</summary>
        private const int CharsPerToken = 4;

        /// <summary>
        /// How many tool rounds one agent turn may take; the loop then makes one closing call without invoking a tool, so a turn
        /// is at most <c>MaxToolRounds + 1</c> model calls (measured). <b>ADDED:</b> the library's default is
        /// 40, and every round is a METERED call against the site's AI quota - driven, a model that kept calling a tool ran all
        /// 40 for one request. A turn here needs a handful: reads, a proposal, an answer.
        /// </summary>
        public const int MaxToolRounds = 12;

        /// <summary>The site's client with the tool-calling loop, bounded to <paramref name="maxRounds"/> tool rounds.</summary>
        public static IChatClient WithTools(IChatClient client, int maxRounds = MaxToolRounds) =>
            client.AsBuilder().UseFunctionInvocation(configure: invocation => invocation.MaximumIterationsPerRequest = maxRounds).Build();

        public static List<ChatMessage> Compose(string systemPrompt, AssistantRequest request)
        {
            var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
            messages.AddRange(request.Messages.Select(message => new ChatMessage(message.Role, message.Content)));
            return messages;
        }

        /// <summary>
        /// The model for a turn: the tier router's when it maps one (CostOptimization), otherwise the site's own.
        /// A tier names a model of the SITE's provider - an installation whose sites use different providers leaves tiers unset.
        /// </summary>
        public static async Task<string> SelectModelAsync(IModelTierRouter? router, string siteModel, IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
        {
            if (router is null)
            {
                return siteModel;
            }

            var decision = await router.RouteAsync(
                new ModelTierRequest(messages.Sum(message => message.Text?.Length ?? 0) / CharsPerToken, options.Tools?.Count ?? 0, options.ResponseFormat is ChatResponseFormatJson),
                cancellationToken);
            return string.IsNullOrWhiteSpace(decision.Model) ? siteModel : decision.Model;
        }

        /// <summary>
        /// The response-cache gate: only a DETERMINISTIC turn (temperature at most 0 - an unset temperature
        /// samples freely) is served from the response cache, keyed by provider, model, the composed messages and temperature;
        /// everything else dispatches straight through.
        /// </summary>
        public static async Task<ModelResponseCacheResult> CachedAsync(
            IModelResponseCache? cache, string provider, string model, IReadOnlyList<ChatMessage> messages, ChatOptions options,
            Func<CancellationToken, Task<CachedModelResponse>> dispatch, CancellationToken cancellationToken)
        {
            if (cache is null || options.Temperature is not <= 0f)
            {
                return new ModelResponseCacheResult(await dispatch(cancellationToken), FromCache: false);
            }

            var key = ModelResponseCacheKey.Create(provider, model, messages.Select(message => new ModelPromptMessage(message.Role.Value, message.Text ?? string.Empty)).ToList(), options.Temperature.Value);
            return await cache.GetOrCreateAsync(key, dispatch, cancellationToken);
        }
    }

    /// <summary>
    /// The general conversational agent: one tool-calling loop over the read-only
    /// workspace tools; the model tier and the response cache are optional seams (CostOptimization).
    /// </summary>
    public sealed class ChatAssistantAgent(IAssistantModelAccess models, IAssistantQueryGateway gateway, IModelTierRouter? tierRouter = null, IModelResponseCache? responseCache = null) : IAssistantSpecialist
    {
        public const string AgentName = "assistant";

        private const string SystemPrompt =
            "You are the site's assistant. Help the user build and refine data pipelines, dashboards, and widgets. Use the " +
            "provided tools to inspect the user's connections and datasets. Only dataset summaries (column metadata and " +
            "statistics) are available - never assume access to raw rows. Be concise and accurate.";

        public string Name => AgentName;

        public string Description => "general conversation and workspace questions that are not covered by published help.";

        public async Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            using var model = await models.OpenAsync(Name, cancellationToken);
            var client = AssistantTurn.WithTools(model.Client);
            var options = new ChatOptions { Tools = [.. new AssistantQueryTools(gateway).AsTools()], Temperature = model.Temperature };
            var messages = AssistantTurn.Compose(SystemPrompt, request);
            var modelId = await AssistantTurn.SelectModelAsync(tierRouter, model.Model, messages, options, cancellationToken);
            options.ModelId = modelId;

            var result = await AssistantTurn.CachedAsync(responseCache, model.Provider, modelId, messages, options, async token =>
            {
                var response = await client.GetResponseAsync(messages, options, token);
                return new CachedModelResponse(response.Text, modelId, response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0);
            }, cancellationToken);

            // A hit made no model call, so the metered client never saw it: it is recorded here, as cached (billed at nothing).
            if (result.FromCache)
            {
                await model.RecordCachedAsync(result.Response, cancellationToken);
            }

            return AssistantReply.Text(new AssistantMessage(ChatRole.Assistant, result.Response.Text));
        }
    }

    /// <summary>
    /// The Pipeline Architect: the model reads the user's connections, datasets and
    /// the step catalog, then calls ProposePipeline, which refuses an unreachable connection and judges the draft the way the
    /// editor's save does. The accepted draft is surfaced PROPOSED; applying it is the user's act.
    /// </summary>
    public sealed class PipelineArchitectAgent(IAssistantModelAccess models, IAssistantQueryGateway queryGateway, IAssistantCommandGateway commandGateway) : IAssistantSpecialist
    {
        public const string AgentName = "pipeline-architect";

        private const string SystemPrompt =
            "You are the Pipeline Architect. Design a data pipeline that satisfies the user's request. Use ListStepTypes to " +
            "choose step types - use only types it returns and set only the inputs it lists; never set a sensitive input, the " +
            "user enters those in the editor. Use ListConnections and ListDatasets to discover what the user can access; to " +
            "read from a connection, set the step's connectionId to one returned by ListConnections. Connect steps by index, " +
            "naming the outcome port when a step has several. When the design is complete, call ProposePipeline. If it reports " +
            "the draft is unauthorized or invalid, correct it and call ProposePipeline again. Only dataset summaries are " +
            "available; never assume access to raw rows.";

        public string Name => AgentName;

        public string Description => "designs and proposes data pipelines.";

        public async Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            using var model = await models.OpenAsync(Name, cancellationToken);
            var client = AssistantTurn.WithTools(model.Client);
            var pipelineTools = new AssistantPipelineTools(queryGateway, commandGateway);
            var options = new ChatOptions { ModelId = model.Model, Tools = [.. new AssistantQueryTools(queryGateway).AsTools(), .. pipelineTools.AsTools()] };

            var response = await client.GetResponseAsync(AssistantTurn.Compose(SystemPrompt, request), options, cancellationToken);
            var message = new AssistantMessage(ChatRole.Assistant, response.Text);
            return pipelineTools.ProposedDraft is { } draft
                ? new AssistantReply(message, [new ArtifactDraft(ArtifactKind.Pipeline, draft.Name, AssistantDraftJson.Serialize(draft))])
                : AssistantReply.Text(message);
        }
    }

    /// <summary>
    /// The Dashboard Designer: the model reads the datasets, asks the Widget
    /// Generator for each chart's spec, lays the board out and calls ProposeDashboard. The proposal names the datasets it
    /// binds, so the conversation records them as its context.
    /// </summary>
    public sealed class DashboardDesignerAgent(IAssistantModelAccess models, IAssistantQueryGateway queryGateway, IWidgetSpecGenerator widgetGenerator) : IAssistantSpecialist
    {
        public const string AgentName = "dashboard-designer";

        private const string SystemPrompt =
            "You are the Dashboard Designer. Design a dashboard that answers the user's request. Use ListDatasets to discover " +
            "available data, GenerateWidgetSpec to produce a chart spec for each chart widget, and lay the widgets out on the " +
            "grid. When the design is complete, call ProposeDashboard. If it reports a problem, correct it and call " +
            "ProposeDashboard again. Only dataset summaries are available; never assume access to raw rows.";

        public string Name => AgentName;

        public string Description => "designs and proposes dashboards and their chart widgets.";

        public async Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            using var model = await models.OpenAsync(Name, cancellationToken);
            var client = AssistantTurn.WithTools(model.Client);
            var dashboardTools = new AssistantDashboardTools(queryGateway, widgetGenerator);
            var options = new ChatOptions { ModelId = model.Model, Tools = [.. new AssistantQueryTools(queryGateway).AsTools(), .. dashboardTools.AsTools()] };

            var response = await client.GetResponseAsync(AssistantTurn.Compose(SystemPrompt, request), options, cancellationToken);
            var message = new AssistantMessage(ChatRole.Assistant, response.Text);
            return dashboardTools.ProposedDraft is { } draft
                ? new AssistantReply(message, [new ArtifactDraft(ArtifactKind.Dashboard, draft.Name, AssistantDraftJson.Serialize(draft), draft.Widgets.Select(widget => widget.DatasetId).Distinct().ToList())])
                : AssistantReply.Text(message);
        }
    }

    /// <summary>
    /// The Widget Generator, the Designer's agent-as-tool: only the bounded summary
    /// reaches the model, and a spec is accepted only when theme-free with every column reference resolved - within
    /// <see cref="WidgetSpecTools.MaxAttempts"/> submissions.
    /// </summary>
    public sealed class WidgetGeneratorAgent(IAssistantModelAccess models) : IWidgetSpecGenerator
    {
        /// <summary>The agent reported to the meter; the generator is a tool, not a routable specialist.</summary>
        public const string AgentName = "widget-generator";

        private const string SystemPrompt =
            "You are the Widget Generator. Produce a single ECharts option object as JSON that visualises the dataset for the " +
            "request. The spec must be theme-free: do not set color, backgroundColor, fontFamily, fontSize, fontWeight or " +
            "textStyle - the renderer applies the active theme. Reference dataset columns only as $ColumnName, using only the " +
            "columns listed. Call SubmitChartSpec with the spec; if it reports problems, fix them and submit again.";

        public async Task<WidgetSpecResult> GenerateAsync(AssistantDataSummary summary, string request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(summary);
            using var model = await models.OpenAsync(AgentName, cancellationToken);
            var client = AssistantTurn.WithTools(model.Client);
            var tools = new WidgetSpecTools(summary);
            var options = new ChatOptions { ModelId = model.Model, Tools = [.. tools.AsTools()] };
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, $"{request}\n\n{DescribeSummary(summary)}"),
            };

            await client.GetResponseAsync(messages, options, cancellationToken);
            return tools.AcceptedSpec is { } spec
                ? WidgetSpecResult.Ok(spec)
                : WidgetSpecResult.Failed(tools.LastErrors.Count > 0 ? tools.LastErrors : ["The model did not produce a chart spec."]);
        }

        private static string DescribeSummary(AssistantDataSummary summary)
        {
            var builder = new StringBuilder();
            builder.Append("Dataset columns (reference as $ColumnName), ~").Append(summary.RowCountEstimate).AppendLine(" rows:");
            foreach (var column in summary.Columns)
            {
                builder.Append("- ").Append(column.Name).Append(" (unique=").Append(column.UniqueCount).Append(", nulls=").Append(column.NullCount);
                if (column.Min is not null || column.Max is not null)
                {
                    builder.Append(", range=").Append(column.Min ?? "?").Append("..").Append(column.Max ?? "?");
                }

                builder.AppendLine(")");
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Answers from this site's PUBLISHED help topics: the
    /// Help module owns the content, the agent only phrases an answer from it. With no matching topic it says so WITHOUT a
    /// model call. <b>ADDED:</b> the question is searched term by term (the Help module's search matches one phrase).
    /// </summary>
    public sealed class ContextualHelpAgent(IAssistantModelAccess models, IAssistantQueryGateway gateway) : IAssistantSpecialist
    {
        public const string AgentName = "contextual-help";

        public const string NotCovered = "The published help content does not cover that yet.";

        private const int MaxTopics = 5;

        private const string SystemPrompt =
            "You are the site's contextual help assistant. Answer only from the provided published help topics. If the answer " +
            "is not present in the topics, say that the help content does not cover it yet. Be concise.";

        public string Name => AgentName;

        public string Description => "answers questions from the published contextual-help topics.";

        public async Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var query = request.Messages.LastOrDefault(message => message.Role == ChatRole.User)?.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return AssistantReply.Text(new AssistantMessage(ChatRole.Assistant, "What would you like help with?"));
            }

            var topics = await gateway.SearchHelpAsync(query, MaxTopics, cancellationToken);
            if (topics.Count == 0)
            {
                return AssistantReply.Text(new AssistantMessage(ChatRole.Assistant, NotCovered));
            }

            using var model = await models.OpenAsync(Name, cancellationToken);
            var response = await model.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, BuildUserPrompt(query, topics))],
                new ChatOptions { ModelId = model.Model },
                cancellationToken);
            var answer = string.IsNullOrWhiteSpace(response.Text) ? NotCovered : response.Text.Trim();
            return AssistantReply.Text(new AssistantMessage(ChatRole.Assistant, answer));
        }

        private static string BuildUserPrompt(string query, IReadOnlyList<AssistantHelpTopic> topics)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Question:").AppendLine(query).AppendLine().AppendLine("Published help topics:");
            foreach (var topic in topics)
            {
                builder.AppendLine().Append("Anchor: ").AppendLine(topic.Anchor).Append("Title: ").AppendLine(topic.Title).AppendLine(topic.Text);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// The entry agent: the guardrail first - rate, injection, explicit references - so a
    /// refused request reaches no model; then ONE routing turn in which the model names the specialists; then each named
    /// specialist, their proposals merged. With no specialist named, general chat answers.
    /// </summary>
    public sealed class OrchestratorAgent(
        IAssistantModelAccess models,
        IAssistantGuardrail guardrail,
        IAssistantCaller caller,
        IEnumerable<IAssistantSpecialist> specialists,
        IModelTierRouter? tierRouter = null) : IAssistantAgent
    {
        public const string AgentName = "orchestrator";

        private readonly IReadOnlyDictionary<string, IAssistantSpecialist> _specialists = specialists.ToDictionary(specialist => specialist.Name, StringComparer.OrdinalIgnoreCase);

        public string Name => AgentName;

        public async Task<AssistantReply> InvokeAsync(AssistantRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var (siteId, _, userId) = await caller.ResolveAsync(cancellationToken);
            await guardrail.EnsureAllowedAsync(request, siteId, userId, cancellationToken);

            var replies = new List<AssistantReply>();
            foreach (var name in await RouteAsync(request, cancellationToken))
            {
                replies.Add(await _specialists[name].InvokeAsync(request, cancellationToken));
            }

            return Merge(replies);
        }

        private async Task<IReadOnlyList<string>> RouteAsync(AssistantRequest request, CancellationToken cancellationToken)
        {
            using var model = await models.OpenAsync(Name, cancellationToken);
            var client = AssistantTurn.WithTools(model.Client);
            var routing = new OrchestratorRoutingTools(_specialists.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

            // Never response-cached: the turn's value is the Route CALL, which a cached text would skip.
            var options = new ChatOptions { Tools = [.. routing.AsTools()], Temperature = model.Temperature };
            var messages = AssistantTurn.Compose(BuildSystemPrompt(), request);
            options.ModelId = await AssistantTurn.SelectModelAsync(tierRouter, model.Model, messages, options, cancellationToken);
            await client.GetResponseAsync(messages, options, cancellationToken);

            if (routing.SelectedSpecialists.Count > 0)
            {
                return routing.SelectedSpecialists.Select(name => _specialists[name].Name).ToList();
            }

            if (_specialists.ContainsKey(ChatAssistantAgent.AgentName))
            {
                return [ChatAssistantAgent.AgentName];
            }

            return _specialists.Count > 0 ? [_specialists.Keys.First()] : Array.Empty<string>();
        }

        private string BuildSystemPrompt()
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                "You are the assistant's orchestrator. Decide which specialist agent(s) should handle the user's request and " +
                "call the Route tool exactly once with their names. Available specialists:");
            foreach (var specialist in _specialists.Values)
            {
                builder.Append("- ").Append(specialist.Name).Append(": ").AppendLine(specialist.Description);
            }

            builder.Append(
                "Route a focused request to a single specialist; route a combined request (for example a pipeline AND a " +
                "dashboard) to several. Route published help questions to 'contextual-help' when it is available. If the " +
                "request is general conversation, route to 'assistant'.");
            return builder.ToString();
        }

        private static AssistantReply Merge(IReadOnlyList<AssistantReply> replies)
        {
            if (replies.Count == 0)
            {
                return AssistantReply.Text(new AssistantMessage(ChatRole.Assistant, "No specialist handled the request."));
            }

            if (replies.Count == 1)
            {
                return replies[0];
            }

            var text = string.Join("\n\n", replies.Select(reply => reply.Message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
            return new AssistantReply(new AssistantMessage(ChatRole.Assistant, text), replies.SelectMany(reply => reply.Artifacts).ToList());
        }
    }
}
