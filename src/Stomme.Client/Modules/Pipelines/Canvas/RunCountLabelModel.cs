using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// n8n's "3 items" on a connection: in the run shown, how many records the step it leaves passed along it. Drawn by
    /// <see cref="RunCountLabel"/> just above the connection's middle (the outcome's name and the connection's buttons keep the
    /// middle itself); there only while a run is shown and the connection was taken in it.
    /// </summary>
    public sealed class RunCountLabelModel : LinkLabelModel
    {
        public RunCountLabelModel(BaseLinkModel parent, string content)
            : base(parent, content, 0.5, new Point(0, -16))
        {
        }
    }
}
