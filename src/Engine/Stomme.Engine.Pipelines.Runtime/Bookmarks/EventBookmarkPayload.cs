using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime.Bookmarks;

[Obsolete("Use EventStimulus instead.")]
[ForwardedType(typeof(EventStimulus))]
public record EventBookmarkPayload(string EventName);