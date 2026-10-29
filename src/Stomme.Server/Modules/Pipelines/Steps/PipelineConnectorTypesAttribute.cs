#nullable enable
using System;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The connector types a step's connection input can use (<c>slack</c>, <c>telegram</c>...). The step catalog says
    /// them (<see cref="Stomme.Models.Pipelines.StepInputView.ConnectorTypes"/>), so the editor's picker offers only those
    /// connections, a connection made from the step is one of them, and a connection found in the step search is offered with
    /// this step only when it is one. The step still judges the connection it is given when it runs.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true)]
    public sealed class PipelineConnectorTypesAttribute(params string[] types) : Attribute
    {
        public string[] Types { get; } = types;
    }
}
