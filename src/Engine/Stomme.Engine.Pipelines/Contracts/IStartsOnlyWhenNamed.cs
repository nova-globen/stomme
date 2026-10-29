namespace Stomme.Engine.Pipelines;

/// <summary>
/// A step that begins a run only when the run NAMES it as its start (a dispatch's <c>TriggerStepId</c>) - never as a flowchart's
/// default start: not as the step marked to start it, not as a root, not as the first step. Stomme's evaluation trigger
/// (n8n's Evaluations) is one: a pipeline holds it beside its usual trigger, and a run started any other way must not
/// begin there.
/// </summary>
public interface IStartsOnlyWhenNamed
{
}
