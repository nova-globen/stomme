using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime.Stimuli;

/// <summary>
/// Contains information created by <see cref="RunTask"/>.  
/// </summary>
public record RunTaskStimulus(string TaskId, [property: ExcludeFromHash]string TaskName);