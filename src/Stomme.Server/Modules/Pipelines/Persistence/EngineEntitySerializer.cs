#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Modules.Pipelines.Persistence
{
    /// <summary>
    /// The one place the engine's non-scalar members become text and come back (BL-95 increment (a)). Both
    /// the relational and the RavenDB stores hold the SAME strings, produced by the engine's OWN serializers - the
    /// pipeline state serializer for an instance's state, the payload serializer for anything polymorphic (bookmark
    /// and trigger payloads, exceptions, properties) and the safe serializer for step state and outputs, which may
    /// carry values the payload serializer cannot round-trip. So a definition or a run persisted on SQLite reads
    /// back identically on RavenDB, and a serializer change in the engine reaches every store at once.
    /// </summary>
    public sealed class EngineEntitySerializer
    {
        private readonly IPayloadSerializer _payloads;
        private readonly IPipelineStateSerializer _states;
        private readonly ISafeSerializer _safe;

        public EngineEntitySerializer(IPayloadSerializer payloads, IPipelineStateSerializer states, ISafeSerializer safe)
        {
            _payloads = payloads;
            _states = states;
            _safe = safe;
        }

        /// <summary>The non-scalar part of a definition. Options travel here only where the store has no columns for them.</summary>
        public sealed class DefinitionData
        {
            public PipelineOptions? Options { get; set; }
            public ICollection<Variable>? Variables { get; set; }
            public ICollection<InputDefinition>? Inputs { get; set; }
            public ICollection<OutputDefinition>? Outputs { get; set; }
            public ICollection<string>? Outcomes { get; set; }
            public IDictionary<string, object>? CustomProperties { get; set; }
        }

        public string DefinitionDataOf(PipelineDefinition definition, bool includeOptions) => _payloads.Serialize(new DefinitionData
        {
            Options = includeOptions ? definition.Options : null,
            Variables = definition.Variables,
            Inputs = definition.Inputs,
            Outputs = definition.Outputs,
            Outcomes = definition.Outcomes,
            CustomProperties = definition.CustomProperties,
        });

        public void ApplyDefinitionData(PipelineDefinition definition, string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            var data = _payloads.Deserialize<DefinitionData>(json);
            if (data.Options is not null)
            {
                definition.Options = data.Options;
            }

            definition.Variables = data.Variables ?? new List<Variable>();
            definition.Inputs = data.Inputs ?? new List<InputDefinition>();
            definition.Outputs = data.Outputs ?? new List<OutputDefinition>();
            definition.Outcomes = data.Outcomes ?? new List<string>();
            definition.CustomProperties = data.CustomProperties ?? new Dictionary<string, object>();
        }

        public string StateOf(PipelineInstance instance) => _states.Serialize(instance.PipelineState);

        public PipelineState StateFrom(string? json) => string.IsNullOrEmpty(json) ? new PipelineState() : _states.Deserialize(json);

        public string? PayloadOf(object? payload) => payload is null ? null : _payloads.Serialize(payload);

        public object? PayloadFrom(string? json) => string.IsNullOrEmpty(json) ? null : _payloads.Deserialize(json);

        public T? PayloadFrom<T>(string? json) where T : class => string.IsNullOrEmpty(json) ? null : _payloads.Deserialize<T>(json);

        public string? SafeOf(object? value) => value is null ? null : _safe.Serialize(value);

        public T? SafeFrom<T>(string? json) where T : class => string.IsNullOrEmpty(json) ? null : _safe.Deserialize<T>(json);
    }
}
