#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>A pipeline that calls another: which, and by which step (Execute pipeline waits for the run, Dispatch does not).</summary>
    public sealed class PipelineCallerView
    {
        public string DefinitionId { get; set; } = "";

        public string? Name { get; set; }

        public string StepId { get; set; } = "";

        public string? StepName { get; set; }

        public bool Waits { get; set; }
    }

    /// <summary>What a sub-pipeline expects: the fields its "When called by another pipeline" declares, and the ones it requires.</summary>
    public sealed class PipelineCallContract
    {
        public List<PipelineField> Fields { get; init; } = new();

        public List<string> Required { get; init; } = new();

        /// <summary>Which pipelines it lets call it (<see cref="PipelineCallers"/>): null for any.</summary>
        public string? CalledBy { get; init; }
    }

    /// <summary>
    /// A sub-pipeline's contract and its callers held to it. What a pipeline expects is what its "When called by another
    /// pipeline" says (<see cref="PipelineFields"/>); an Execute or Dispatch pipeline whose Input - a value of its own, a JSON
    /// object - leaves out a required field, or sends a value a field cannot hold, is warned about where the author writes it,
    /// before a run fails in the called pipeline (n8n checks a sub-workflow's inputs only when it runs). An Input from an
    /// expression or a variable sends what the run has, which only the run knows.
    /// </summary>
    public static class PipelineCallContracts
    {
        public const string WhenCalledType = "Stomme.WhenCalled";

        public const string FieldsInput = "Fields";

        public const string RequiredInput = "RequiredFields";

        /// <summary>The caller's input naming the pipeline it calls.</summary>
        public const string CalledInput = "PipelineDefinitionId";

        /// <summary>The caller's input holding what it sends.</summary>
        public const string SentInput = "Input";

        public static bool IsCall(string? type) => type is "Stomme.ExecutePipeline" or "Stomme.DispatchPipeline";

        /// <summary>The contract a pipeline's graph declares - its first "When called by another pipeline" switched on, with fields; null when none.</summary>
        public static PipelineCallContract? Of(PipelineGraph? graph)
        {
            var called = graph?.Steps?.FirstOrDefault(step => step?.Type == WhenCalledType && !step.Disabled);
            if (called is null || !called.Inputs.TryGetValue(FieldsInput, out var written) || PipelineFields.Parse(written, out _) is not { Count: > 0 } fields)
            {
                return null;
            }

            var required = (called.Inputs.TryGetValue(RequiredInput, out var names) ? names ?? "" : "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => fields.Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return new PipelineCallContract { Fields = fields, Required = required, CalledBy = PipelineCallers.Normalize(graph!.CalledBy) };
        }

        /// <summary>
        /// All a caller is held to: the contract its fields make (<see cref="Of"/>) and which pipelines it lets call it - null when
        /// it declares no fields and lets any pipeline call it.
        /// </summary>
        public static PipelineCallContract? Known(PipelineGraph? graph)
        {
            if (Of(graph) is { } contract)
            {
                return contract;
            }

            return PipelineCallers.Normalize(graph?.CalledBy) is { } calledBy ? new PipelineCallContract { CalledBy = calledBy } : null;
        }

        /// <summary>Why the pipeline whose contract this is would refuse a call from <paramref name="caller"/> (its definition id), or null.</summary>
        public static string? Refusal(PipelineCallContract contract, string? caller) =>
            contract is null || PipelineCallers.Admits(contract.CalledBy, caller) ? null
            : PipelineCallers.Normalize(contract.CalledBy) == PipelineCallers.Nobody
                ? "The pipeline this calls may not be called by another pipeline - its settings say so, and the run would fail here."
                : "The pipeline this calls may be called only by the pipelines its settings name, and this one is not among them - the run would fail here.";

        /// <summary>What is wrong with what a call sends, by the called pipeline's contract - each a sentence; none for an Input the run decides.</summary>
        public static List<string> Check(PipelineGraphStep call, PipelineCallContract contract)
        {
            var problems = new List<string>();
            if (contract.Fields.Count == 0
                || call.InputExpressions.ContainsKey(SentInput) || call.InputVariables.ContainsKey(SentInput) || call.InputScripts.ContainsKey(SentInput))
            {
                return problems;
            }

            JsonObject? record = null;
            if (call.Inputs.TryGetValue(SentInput, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    record = JsonNode.Parse(text) as JsonObject;
                }
                catch (JsonException)
                {
                }

                if (record is null)
                {
                    problems.Add("Input is not a JSON object - the called pipeline reads its fields from one.");
                    return problems;
                }
            }

            JsonNode? Sent(string name) => record?.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            foreach (var name in contract.Required.Where(name => Sent(name) is null))
            {
                problems.Add($"Input sends no '{name}', which the called pipeline requires.");
            }

            foreach (var field in contract.Fields)
            {
                if (Sent(field.Name) is { } value && !PipelineSiteTables.TryConvert(value, field.Type, out _, out _))
                {
                    problems.Add($"'{PipelineSiteTables.Text(value)}' is not {PipelineSiteTables.Describe(field.Type)}, which '{field.Name}' holds - the called pipeline would refuse it.");
                }
            }

            return problems;
        }
    }
}
