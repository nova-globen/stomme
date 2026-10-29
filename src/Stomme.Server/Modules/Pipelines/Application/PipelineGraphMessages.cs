#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// The step types the editor can place: the engine's registry, browsable entries only, ordered by category then name.
    /// A step with an EMBEDDED port - a loop's Body, an If's Then and Else - is offered with those ports as its
    /// <see cref="StepCatalogEntry.Bodies"/>: the editor opens each as a flowchart of its own. Until then
    /// such a step was left out, because its child slots were drawn as outcome ports and it could only be saved empty (found
    /// walking the editor in a browser, 2026-10-02); <see cref="StepCatalogEntry.Ports"/> now names the FLOW ports alone.
    /// A container whose children are a LIST (Sequence, Parallel, the flowchart itself) is still not offered - the canvas is
    /// that list - and neither is <c>Switch</c>, whose every case holds a step of its own that no body can name. The
    /// flowchart's own branching (Decision, Switch (flow), Fork, Join) stays.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListStepsQuery : IQuery<IReadOnlyList<StepCatalogEntry>>;

    public sealed class ListStepsQueryHandler(IStepRegistry registry, IRegistriesPopulator populator) : IQueryHandler<ListStepsQuery, IReadOnlyList<StepCatalogEntry>>
    {
        public async Task<IReadOnlyList<StepCatalogEntry>> Handle(ListStepsQuery request, CancellationToken ct)
        {
            // The engine populates the registry as a start-up task of each tenant; a request that beats it (or a test
            // that never activated a tenant) populates it here, once.
            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            return Catalog(registry);
        }

        /// <summary>The steps the editor offers - and what the publish's problem check reads them by.</summary>
        internal static List<StepCatalogEntry> Catalog(IStepRegistry registry) =>
            registry.ListAll()
                .Where(descriptor => descriptor.IsBrowsable && IsPlaceable(descriptor))
                .OrderBy(descriptor => descriptor.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(descriptor => descriptor.DisplayName ?? descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Entry)
                .ToList();

        private static StepCatalogEntry Entry(Stomme.Engine.Pipelines.Models.StepDescriptor descriptor)
        {
            var required = RequiredInputs(descriptor);
            return new StepCatalogEntry
                {
                    Type = descriptor.TypeName,
                    Version = descriptor.Version,
                    DisplayName = descriptor.DisplayName ?? descriptor.Name,
                    Category = descriptor.Category,
                    Description = descriptor.Description,
                    Kind = descriptor.Kind.ToString(),
                    Ports = descriptor.Ports.Where(port => port.IsBrowsable && port.Type == Stomme.Engine.Pipelines.Models.PortType.Flow).Select(port => port.Name).ToList(),
                    Bodies = Bodies(descriptor).ToList(),
                    OutcomesFrom = OutcomesFrom(descriptor),
                    Inputs = descriptor.Inputs
                        .Where(input => input.IsBrowsable != false)
                        .OrderBy(input => input.Order)
                        .Select(input => new StepInputView
                        {
                            Name = input.Name,
                            DisplayName = input.DisplayName ?? input.Name,
                            Description = input.Description,
                            TypeName = (Nullable.GetUnderlyingType(input.Type) ?? input.Type).Name,
                            UIHint = input.UIHint,
                            DefaultValue = input.DefaultValue?.ToString(),
                            // The engine's describer never copies CanContainSecrets onto the descriptor (measured on
                            // the vendored 3.7.1), so the flag is read from the property's attribute here.
                            IsSensitive = input.IsSensitive || IsSensitive(descriptor.ClrType, input.Name),
                            Category = input.Category,
                            Order = input.Order,
                            Options = (Nullable.GetUnderlyingType(input.Type) ?? input.Type) is { IsEnum: true } enumType ? Enum.GetNames(enumType).ToList() : null,
                            ConnectorTypes = ConnectorTypes(descriptor.ClrType, input.Name),
                            IsRequired = required(input.Name),
                        })
                        .ToList(),
                    Outputs = descriptor.Outputs.Select(output => new StepOutputView
                    {
                        Name = output.Name,
                        DisplayName = output.DisplayName ?? output.Name,
                        TypeName = output.Type.Name,
                    }).ToList(),
                };
        }

        /// <summary>A step the canvas can author: not a list of steps, and every step it holds sits in a body the editor opens.</summary>
        internal static bool IsPlaceable(Stomme.Engine.Pipelines.Models.StepDescriptor descriptor) =>
            !typeof(Stomme.Engine.Pipelines.Steps.Container).IsAssignableFrom(descriptor.ClrType)
            && !NotPlaceable.Contains(descriptor.TypeName);

        // Switch: each case pairs a condition with a step of its own, beside the Default port - no body names a case.
        private static readonly HashSet<string> NotPlaceable = new(StringComparer.Ordinal) { "Stomme.Switch" };

        /// <summary>
        /// The inputs a step cannot do without, read from the step itself rather than a list to keep: a step
        /// gives an input it can run without a starting value (<c>= new(new Literal&lt;int&gt;(1))</c>) or declares it nullable
        /// (<c>Input&lt;string?&gt;</c>); an input it leaves null (<c>= null!</c>) with a reference type that holds no null is one
        /// the author must fill. A step that reads an input with <c>GetOrDefault</c> and a fallback declares it nullable (Fault,
        /// Fork, Complete and Publish Event were corrected so on 2026-10-06). A step that cannot be made here has none required -
        /// the save stays the judge of it.
        /// </summary>
        internal static Func<string, bool> RequiredInputs(Stomme.Engine.Pipelines.Models.StepDescriptor descriptor)
        {
            var prototype = Prototype(descriptor.ClrType);
            if (prototype is null)
            {
                return _ => false;
            }

            var nullability = new System.Reflection.NullabilityInfoContext();
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in descriptor.Inputs)
            {
                if (!input.IsWrapped || input.IsSynthetic || input.PropertyInfo is null)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = input.ValueGetter(prototype);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value is not null)
                {
                    continue; // it starts with a value
                }

                // A value type left unset runs as its default (an untouched checkbox is false), so only a reference that may not
                // be null is one the author must give.
                var held = nullability.Create(input.PropertyInfo).GenericTypeArguments.FirstOrDefault();
                if (held is not null && !held.Type.IsValueType && held.ReadState == System.Reflection.NullabilityState.NotNull)
                {
                    required.Add(input.Name);
                }
            }

            return required.Contains;
        }

        // A step as the engine makes it from a stored definition: its [JsonConstructor] (often private - WriteLine's is), else a
        // constructor whose every parameter is optional (the engine's steps take their source file and line that way), given
        // none of them.
        private static Stomme.Engine.Pipelines.IStep? Prototype(Type type)
        {
            if (type.IsAbstract || type.ContainsGenericParameters)
            {
                return null;
            }

            var constructors = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .OrderByDescending(candidate => candidate.IsDefined(typeof(System.Text.Json.Serialization.JsonConstructorAttribute), inherit: false))
                .ThenByDescending(candidate => candidate.IsPublic)
                .ThenBy(candidate => candidate.GetParameters().Length);
            foreach (var constructor in constructors)
            {
                var parameters = constructor.GetParameters();
                if (!parameters.All(parameter => parameter.IsOptional))
                {
                    continue;
                }

                try
                {
                    return constructor.Invoke(parameters.Select(_ => Type.Missing).ToArray()) as Stomme.Engine.Pipelines.IStep;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Where a step's own outcomes come from, read off the engine's hints - a Fork (flow)'s Branches
        /// (<c>dynamic-outcomes</c>) and a Switch (flow)'s Cases (<c>flow-switch-editor</c>, a list the step holds directly).
        /// </summary>
        internal static string? OutcomesFrom(Stomme.Engine.Pipelines.Models.StepDescriptor descriptor) =>
            descriptor.Inputs.Any(input => input.Name == StepCatalogEntry.BranchesOutcomes && input.UIHint == Stomme.Engine.Pipelines.UIHints.InputUIHints.DynamicOutcomes)
                ? StepCatalogEntry.BranchesOutcomes
                : descriptor.Inputs.Any(input => input.Name == StepCatalogEntry.CaseOutcomes && input.UIHint == FlowSwitchEditor && !input.IsWrapped)
                    ? StepCatalogEntry.CaseOutcomes
                    : null;

        /// <summary>The engine's hint for a Switch (flow)'s cases.</summary>
        internal const string FlowSwitchEditor = "flow-switch-editor";

        /// <summary>The bodies a step holds others in: its browsable embedded ports.</summary>
        internal static IEnumerable<string> Bodies(Stomme.Engine.Pipelines.Models.StepDescriptor descriptor) =>
            descriptor.Ports.Where(port => port.IsBrowsable && port.Type == Stomme.Engine.Pipelines.Models.PortType.Embedded).Select(port => port.Name);

        /// <summary>The connector types a step's connection input declares (<see cref="Steps.PipelineConnectorTypesAttribute"/>); null: any.</summary>
        internal static List<string>? ConnectorTypes(Type stepType, string inputName) =>
            stepType.GetProperty(inputName)?.GetCustomAttributes(typeof(Steps.PipelineConnectorTypesAttribute), inherit: true)
                .OfType<Steps.PipelineConnectorTypesAttribute>().FirstOrDefault()?.Types.ToList();

        internal static bool IsSensitive(Type stepType, string inputName) =>
            stepType.GetProperty(inputName)?.GetCustomAttributes(typeof(Stomme.Engine.Pipelines.Attributes.InputAttribute), inherit: true)
                .OfType<Stomme.Engine.Pipelines.Attributes.InputAttribute>()
                .Any(attribute => attribute.CanContainSecrets) == true;
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetGraphQuery(string DefinitionId) : IQuery<PipelineGraph?>;

    public sealed class GetGraphQueryHandler(IPipelineDefinitionStore definitions, IAccessGuard guard) : IQueryHandler<GetGraphQuery, PipelineGraph?>
    {
        public async Task<PipelineGraph?> Handle(GetGraphQuery request, CancellationToken ct)
        {
            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (definition is null)
            {
                return null;
            }

            // The pinned test values are an author's (they can be real data); anyone else's graph has none.
            var graph = PipelineGraphCodec.ToGraph(definition.StringData);
            return await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct) ? graph : PipelineGraphCodec.WithoutTestValues(graph);
        }
    }

    /// <summary>
    /// The graph of ONE version of a pipeline - the editor opens it read-only, compares it with the canvas,
    /// and restores it onto the canvas as an ordinary change (saved only when the author saves; undone like any change).
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetVersionGraphQuery(string DefinitionId, int Version) : IQuery<PipelineGraph?>;

    public sealed class GetVersionGraphQueryHandler(IPipelineDefinitionStore definitions, IAccessGuard guard) : IQueryHandler<GetVersionGraphQuery, PipelineGraph?>
    {
        public async Task<PipelineGraph?> Handle(GetVersionGraphQuery request, CancellationToken ct)
        {
            var definition = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.SpecificVersion(request.Version) }, ct);
            if (definition is null)
            {
                return null;
            }

            var graph = PipelineGraphCodec.ToGraph(definition.StringData);
            return await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct) ? graph : PipelineGraphCodec.WithoutTestValues(graph);
        }
    }

    /// <summary>Saves the editor's graph as the latest draft's body (a new draft version over a published one, the publisher's rule).</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SaveGraphCommand(string DefinitionId, PipelineGraph Graph) : ICommand<PipelineView?>;

    public sealed class SaveGraphCommandHandler(IPipelineDefinitionPublisher publisher, IStepRegistry registry, IRegistriesPopulator populator, IIdentityGenerator identities, IStepSerializer steps,
        Stomme.Modules.Collaboration.Realtime.ICollaborationNotifier notifier, ICurrentUser currentUser)
        : ICommandHandler<SaveGraphCommand, PipelineView?>
    {
        public async Task<PipelineView?> Handle(SaveGraphCommand request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request.Graph);
            var draft = await publisher.GetDraftAsync(request.DefinitionId, VersionOptions.Latest, ct);
            if (draft is null)
            {
                return null;
            }

            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            draft.StringData = PipelineGraphJudge.Body(request.Graph, draft.StringData, registry, identities, steps);
            PipelineRunPolicy.Apply(draft, request.Graph.RunsAtOnce, request.Graph.SaveProgress);
            PipelineWire.StampSavedBy(draft, currentUser.UserName, currentUser.UserId);
            draft = await publisher.SaveDraftAsync(draft, ct);
            await notifier.NotifyAsync(Stomme.Collaboration.CollaborativeResourceTypes.Pipeline, request.DefinitionId, Stomme.Collaboration.CollaborationNoticeKinds.Saved, currentUser.UserId ?? 0, ct);
            return PipelineWire.ToView(draft);
        }
    }

    /// <summary>
    /// The editor's save while it collaborates: the graph measured against the graph the editor
    /// LOADED, reconciled against what is stored now by the conflict resolver. Nothing changed underneath - the graph is saved
    /// as <see cref="SaveGraphCommand"/> would; another editor saved meanwhile - their changes and these are merged field by
    /// field, a multi-line input or a position overwritten last-writer-wins with a warning; a name, a setting, a step or a
    /// connection both changed, or a step one removed that the other still used - REJECTED, nothing stored. Authoring rights,
    /// like the save. The plain save is unchanged, so an editor not collaborating behaves as it always has.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SaveGraphMergedCommand(string DefinitionId, PipelineGraph Graph, PipelineGraph Base) : ICommand<GraphSaveResult?>;

    public sealed class SaveGraphMergedCommandHandler(
        IPipelineDefinitionPublisher publisher, IStepRegistry registry, IRegistriesPopulator populator, IIdentityGenerator identities, IStepSerializer steps,
        Stomme.Collaboration.IConflictResolver resolver, Stomme.Modules.Collaboration.Application.CollaborationGate gate, ICurrentUser currentUser,
        Stomme.Modules.Collaboration.Realtime.ICollaborationNotifier notifier)
        : ICommandHandler<SaveGraphMergedCommand, GraphSaveResult?>
    {
        public async Task<GraphSaveResult?> Handle(SaveGraphMergedCommand request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request.Graph);
            ArgumentNullException.ThrowIfNull(request.Base);
            var author = currentUser.UserId is { } userId && userId > 0
                ? userId
                : throw new InvalidOperationException("A collaborating save names its author: sign in.");
            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            // The load-resolve-store of one pipeline is serialised in this process (the engine's draft save carries no version
            // to refuse a stale write with); across nodes the same ADVISORY limit as the locks applies.
            using (await gate.EnterAsync("pipeline-graph:" + request.DefinitionId, ct))
            {
                var draft = await publisher.GetDraftAsync(request.DefinitionId, VersionOptions.Latest, ct);
                if (draft is null)
                {
                    return null;
                }

                var stored = PipelineGraphCodec.ToGraph(draft.StringData);
                var baseFields = PipelineGraphFields.ToFields(request.Base);
                var currentFields = PipelineGraphFields.ToFields(stored);
                var resolution = resolver.Resolve(
                    Stomme.Collaboration.ResourceSnapshot.Of(PipelineGraphFields.Version(baseFields), baseFields),
                    Stomme.Collaboration.ResourceSnapshot.Of(PipelineGraphFields.Version(currentFields), currentFields),
                    PipelineGraphFields.Changes(request.Base, request.Graph, IsTextInput),
                    author);
                var conflicts = resolution.Status == Stomme.Collaboration.ConflictResolutionStatus.Rejected
                    ? resolution.Conflicts
                    : PipelineGraphFields.Dangling(resolution.MergedFields, currentFields);
                if (conflicts.Count > 0)
                {
                    return new GraphSaveResult
                    {
                        Status = nameof(Stomme.Collaboration.ConflictResolutionStatus.Rejected),
                        Pipeline = PipelineWire.ToView(draft),
                        Graph = stored,
                        Conflicts = conflicts.ToList(),
                    };
                }

                // Nothing underneath: the graph exactly as the editor drew it. A merge: the graph the merged fields describe.
                var graph = resolution.Status == Stomme.Collaboration.ConflictResolutionStatus.Applied
                    ? request.Graph
                    : PipelineGraphFields.ToGraph(resolution.MergedFields, request.Graph, stored);
                draft.StringData = PipelineGraphJudge.Body(graph, draft.StringData, registry, identities, steps);
                PipelineRunPolicy.Apply(draft, graph.RunsAtOnce, graph.SaveProgress);
                PipelineWire.StampSavedBy(draft, currentUser.UserName, author);
                draft = await publisher.SaveDraftAsync(draft, ct);
                await notifier.NotifyAsync(Stomme.Collaboration.CollaborativeResourceTypes.Pipeline, request.DefinitionId, Stomme.Collaboration.CollaborationNoticeKinds.Saved, author, ct);
                return new GraphSaveResult
                {
                    Status = resolution.Status.ToString(),
                    Pipeline = PipelineWire.ToView(draft),
                    Graph = PipelineGraphCodec.ToGraph(draft.StringData),
                    Warnings = resolution.Warnings.ToList(),
                };
            }
        }

        // A multi-line input or a code editor holds free text (a script, a body, a mapping): last-writer-wins with a warning.
        private bool IsTextInput(string type, string input) =>
            registry.ListAll().FirstOrDefault(descriptor => descriptor.TypeName == type)?.Inputs.FirstOrDefault(candidate => candidate.Name == input)?.UIHint
                is Stomme.Engine.Pipelines.UIHints.InputUIHints.MultiLine or Stomme.Engine.Pipelines.UIHints.InputUIHints.CodeEditor;
    }

    /// <summary>
    /// The save's judgement of a graph with NOTHING stored: the assistant's pipeline proposals are held
    /// to exactly the rule the editor's save applies. The reasons it refuses, or none. Authoring rights, like the save.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ValidateGraphQuery(PipelineGraph Graph) : IQuery<IReadOnlyList<string>>;

    public sealed class ValidateGraphQueryHandler(IStepRegistry registry, IRegistriesPopulator populator, IIdentityGenerator identities, IStepSerializer steps)
        : IQueryHandler<ValidateGraphQuery, IReadOnlyList<string>>
    {
        public async Task<IReadOnlyList<string>> Handle(ValidateGraphQuery request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request.Graph);
            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            try
            {
                PipelineGraphJudge.Body(request.Graph, null, registry, identities, steps);
                return Array.Empty<string>();
            }
            catch (ArgumentException ex)
            {
                return new[] { ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal) };
            }
        }
    }

    /// <summary>
    /// A new pipeline WITH a body: a file imported, or a starter. The graph is judged by the save's own rule
    /// BEFORE anything is stored, so a refused file leaves no empty pipeline behind; the draft is stored once, with its body and
    /// who saved it. Authoring rights, like the save.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ImportPipelineCommand(string Name, string? Description, PipelineGraph Graph) : ICommand<PipelineView>;

    public sealed class ImportPipelineCommandHandler(IPipelineDefinitionPublisher publisher, IStepRegistry registry, IRegistriesPopulator populator, IIdentityGenerator identities,
        IStepSerializer steps, ICurrentUser currentUser) : ICommandHandler<ImportPipelineCommand, PipelineView>
    {
        public async Task<PipelineView> Handle(ImportPipelineCommand request, CancellationToken ct)
        {
            var name = Names.Require(request.Name);
            if (request.Graph is null)
            {
                throw new ArgumentException("The pipeline to import has no graph.", nameof(request.Graph));
            }

            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            string body;
            try
            {
                body = PipelineGraphJudge.Body(request.Graph, null, registry, identities, steps);
            }
            catch (ArgumentException ex) when (ex.ParamName is not null)
            {
                // The reason as the person reads it, without the parameter's name the framework appends.
                throw new PipelineGraphRefusedException(ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal), null, ex);
            }

            var definition = await publisher.NewAsync(new Stomme.Engine.Pipelines.Steps.Sequence(), ct);
            definition.Name = name;
            definition.Description = request.Description?.Trim();
            definition.StringData = body;
            PipelineRunPolicy.Apply(definition, request.Graph.RunsAtOnce, request.Graph.SaveProgress);
            PipelineWire.StampSavedBy(definition, currentUser.UserName, currentUser.UserId);
            definition = await publisher.SaveDraftAsync(definition, ct);
            return PipelineWire.ToView(definition);
        }
    }

    /// <summary>
    /// A graph the codec or the engine refuses - the editor's answer, not a fault (every refused save was logged at
    /// fail). Still an <see cref="ArgumentException"/>, so every caller that answers 400 for one is unchanged.
    /// </summary>
    public sealed class PipelineGraphRefusedException(string message, string? paramName, Exception? inner)
        : ArgumentException(message, paramName, inner), Stomme.Mediator.IExpectedRefusal;

    /// <summary>The body for a graph, judged by the engine - the ONE rule the save and the validation share.</summary>
    internal static class PipelineGraphJudge
    {
        /// <exception cref="PipelineGraphRefusedException">The codec or the engine refuses the graph; the message says why.</exception>
        public static string Body(PipelineGraph graph, string? currentBody, IStepRegistry registry, IIdentityGenerator identities, IStepSerializer steps)
        {
            string body;
            try
            {
                body = PipelineGraphCodec.ToBody(graph, currentBody, registry, identities);
            }
            catch (ArgumentException ex) when (ex is not PipelineGraphRefusedException)
            {
                // The codec's own refusal (an unknown step, a bad connection), its parameter name kept for the callers that read it.
                throw new PipelineGraphRefusedException(ex.Message, ex.ParamName, ex);
            }

            try
            {
                // The engine is the judge of its own document: what it cannot read is refused before it is stored.
                steps.Deserialize(body);
            }
            catch (Exception ex)
            {
                throw new PipelineGraphRefusedException("The graph does not make a definition the engine can read: " + ex.Message, nameof(graph), ex);
            }

            return body;
        }
    }
}
