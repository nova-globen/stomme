using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime.Bookmarks;

/// <summary>
/// Contains information created by <see cref="RunTask"/>.  
/// </summary>
[Obsolete("Use RunTaskStimulus instead.")]
[ForwardedType(typeof(RunTaskStimulus))]
public record RunTaskBookmarkPayload(string TaskId, [property: ExcludeFromHash] string TaskName);