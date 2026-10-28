#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    // ============================================================================================================
    // BL-229 - n8n's AI workflow builder, in the editor: the author says what the pipeline should do, or what should change, and
    // the site's model changes the canvas through the workbench's tools (PipelineGraphWorkbench). It PROPOSES (D113): nothing is
    // stored here - the change comes back judged by the save's own rule, the editor puts it on the canvas marked as the AI's, and
    // the author saves it or undoes it. It needs pipelines:manage AND the assistant's own key, as the Landing designer does: the
    // model is the site's, quota-gated and metered, and assistant:use is who may spend it.
    // ============================================================================================================

    /// <summary>The designer the editor's AI uses - the AI assistant's Pipeline Designer, or none when it is not composed.</summary>
    public interface IPipelineGraphDesigner
    {
        /// <summary>Whether the site's model can be asked here - cheap, no model call.</summary>
        Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Has the site's model change the workbench's pipeline as the instruction asks, through its tools - the conversation so far
        /// (<paramref name="earlier"/>, oldest first) told to it before; answers what the model says it did.
        /// </summary>
        /// <exception cref="InvalidOperationException">The model cannot be asked, or refused the request; the message says why.</exception>
        Task<string> DesignAsync(PipelineGraphWorkbench workbench, string instruction, IReadOnlyList<PipelineAiTurn>? earlier = null, CancellationToken cancellationToken = default);
    }

    /// <summary>The designer when no AI is composed into the host: the editor offers nothing, and a request says so.</summary>
    public sealed class NoPipelineGraphDesigner : IPipelineGraphDesigner
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string> DesignAsync(PipelineGraphWorkbench workbench, string instruction, IReadOnlyList<PipelineAiTurn>? earlier = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No AI is composed into this installation.");
    }

    /// <summary>
    /// The canvas changed by the site's model as the author asks - proposed, nothing stored; or, when the model changed nothing, its
    /// answer alone (a question about the pipeline answered, or one asked back). Refused (with the reason) when what it made is a
    /// pipeline the save would refuse.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DesignPipelineGraphCommand(PipelineGraph Graph, string Instruction, IReadOnlyList<PipelineAiTurn>? Earlier = null) : ICommand<PipelineGraphDesignView>;

    public sealed class DesignPipelineGraphCommandHandler(IPipelineGraphDesigner designer, IAccessGuard accessGuard, IStepRegistry registry, IRegistriesPopulator populator,
        IIdentityGenerator identities, IStepSerializer steps) : ICommandHandler<DesignPipelineGraphCommand, PipelineGraphDesignView>
    {
        public const int MaxInstructionLength = 2000;

        /// <summary>The most of a conversation told to the model: the latest exchanges, each cut to the instruction's bound.</summary>
        public const int MaxEarlierTurns = 6;

        public async Task<PipelineGraphDesignView> Handle(DesignPipelineGraphCommand request, CancellationToken ct)
        {
            var instruction = (request.Instruction ?? string.Empty).Trim();
            if (instruction.Length == 0 || instruction.Length > MaxInstructionLength)
            {
                throw new ArgumentException($"Say what the pipeline should do in 1 to {MaxInstructionLength} characters.", nameof(request));
            }

            if (request.Graph is null)
            {
                throw new ArgumentException("There is no pipeline to change.", nameof(request));
            }

            if (!await accessGuard.IsAllowedAsync(null, Stomme.AiAssistant.AssistantPermissions.Use, ct))
            {
                throw new AccessDeniedException($"Building a pipeline with AI needs {Stomme.AiAssistant.AssistantPermissions.Use} as well.");
            }

            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            var workbench = new PipelineGraphWorkbench(request.Graph, ListStepsQueryHandler.Catalog(registry), Judge);
            var earlier = (request.Earlier ?? Array.Empty<PipelineAiTurn>())
                .Where(turn => turn is not null && !string.IsNullOrWhiteSpace(turn.Asked))
                .TakeLast(MaxEarlierTurns)
                .Select(turn => new PipelineAiTurn { Asked = Bounded(turn.Asked), Said = Bounded(turn.Said) })
                .ToList();
            var said = (await designer.DesignAsync(workbench, instruction, earlier, ct))?.Trim() ?? string.Empty;
            if (!workbench.Changed)
            {
                // An answer, not a change: the canvas stays as it is, and the author reads what the model said.
                return new PipelineGraphDesignView { Graph = null, Summary = said.Length == 0 ? "The AI changed nothing." : said };
            }

            if (Judge(workbench.Graph) is { } refused)
            {
                throw new InvalidOperationException("The AI's change was not kept - the save would refuse it: " + refused);
            }

            return new PipelineGraphDesignView { Graph = workbench.Graph, Summary = said };
        }

        private static string Bounded(string? text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length <= MaxInstructionLength ? trimmed : trimmed[..MaxInstructionLength];
        }

        // The save's own judgement, nothing stored: null when it would take the graph.
        private string? Judge(PipelineGraph graph)
        {
            try
            {
                PipelineGraphJudge.Body(graph, null, registry, identities, steps);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal);
            }
        }
    }
}
