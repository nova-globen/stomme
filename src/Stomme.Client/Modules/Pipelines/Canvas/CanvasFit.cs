using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// "Fit": the zoom and pan that show every step, centred. The canvas library's own ZoomToFit throws
    /// when the canvas has no size yet (a tab not drawn - it took the whole editor down when driven), and it zooms a
    /// one-step pipeline in to fill the window; this never does either.
    /// </summary>
    public static class CanvasFit
    {
        /// <summary>The space kept between the steps and the canvas's edge, in screen pixels.</summary>
        public const double Margin = 50;

        /// <summary>Fit never zooms in past the steps' own size.</summary>
        public const double MaxZoom = 1;

        /// <summary>Nor out so far that a step is a speck.</summary>
        public const double MinZoom = 0.1;

        /// <summary>What a step that has not been measured yet is taken to occupy.</summary>
        public const double DefaultStepWidth = 220;

        public const double DefaultStepHeight = 100;

        /// <summary>
        /// The zoom and the pan (the screen position of the canvas's origin) that fit <paramref name="steps"/> - each its
        /// left, top, width and height - into a canvas of <paramref name="width"/> by <paramref name="height"/>; null when
        /// there is nothing to fit or nothing to fit it into.
        /// </summary>
        public static (double Zoom, double PanX, double PanY)? Compute(double width, double height, IEnumerable<(double X, double Y, double? Width, double? Height)> steps)
        {
            var boxes = steps?.ToList();
            if (boxes is null || boxes.Count == 0 || !(width > 0) || !(height > 0))
            {
                return null;
            }

            var left = boxes.Min(box => box.X);
            var top = boxes.Min(box => box.Y);
            var right = boxes.Max(box => box.X + Size(box.Width, DefaultStepWidth));
            var bottom = boxes.Max(box => box.Y + Size(box.Height, DefaultStepHeight));

            var room = (Width: Math.Max(width - 2 * Margin, 1), Height: Math.Max(height - 2 * Margin, 1));
            var zoom = Math.Min(room.Width / Math.Max(right - left, 1), room.Height / Math.Max(bottom - top, 1));
            zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

            // Centre the steps: the middle of their bounds lands on the middle of the canvas.
            var panX = width / 2 - (left + right) / 2 * zoom;
            var panY = height / 2 - (top + bottom) / 2 * zoom;
            return (zoom, panX, panY);
        }

        /// <summary>
        /// Whether every step lies wholly inside a canvas of <paramref name="width"/> by <paramref name="height"/> at the zoom and pan
        /// it is shown with - what decides, on opening a pipeline, whether it is fitted (n8n's zoom to fit on open).
        /// </summary>
        public static bool Shows(double width, double height, double zoom, double panX, double panY, IEnumerable<(double X, double Y, double? Width, double? Height)> steps) =>
            (steps ?? Enumerable.Empty<(double, double, double?, double?)>()).All(box =>
                box.X * zoom + panX >= 0
                && box.Y * zoom + panY >= 0
                && (box.X + Size(box.Width, DefaultStepWidth)) * zoom + panX <= width
                && (box.Y + Size(box.Height, DefaultStepHeight)) * zoom + panY <= height);

        private static double Size(double? measured, double fallback) => measured is > 0 ? measured.Value : fallback;
    }
}
