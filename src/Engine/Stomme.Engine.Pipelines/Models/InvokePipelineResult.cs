using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Models;

public record InvokePipelineResult(PipelineState PipelineState, ICollection<Bookmark> Bookmarks);