using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// A sticky note on the canvas: text for whoever reads the pipeline, with no ports - nothing connects
    /// to it and the engine never runs it. Moved, edited and removed like a step; saved in the flowchart's designer metadata.
    /// </summary>
    public sealed class NoteNodeModel : NodeModel
    {
        public NoteNodeModel(string noteId, string text, Point position)
            : base(position)
        {
            NoteId = noteId;
            Text = text ?? "";
        }

        public string NoteId { get; }

        public string Text { get; private set; }

        public void Write(string text)
        {
            Text = text ?? "";
            Refresh();
        }

        /// <summary>n8n's note colours: one of <see cref="PipelineNoteColors.All"/>, or null for the theme's accent.</summary>
        public string Color { get; private set; }

        public void Paint(string color)
        {
            Color = PipelineNoteColors.Normalize(color);
            Refresh();
        }
    }
}
