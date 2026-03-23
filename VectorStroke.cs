using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace NoirNotes.Models
{
    public enum ToolType
    {
        FountainPen,   // Pressure-sensitive round nib
        ChiselPen,     // Calligraphy flat nib, angle-dependent width
        Highlighter,   // Wide semi-transparent overlay
        Eraser         // Removes intersecting strokes
    }

    /// <summary>
    /// An immutable, resolution-independent vector stroke.
    /// Strokes are stored in canvas-space coordinates so that pan/zoom
    /// never degrades line quality — this is the core of the vector model.
    /// </summary>
    public sealed class VectorStroke
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public Guid Id { get; } = Guid.NewGuid();

        // ── Source data ───────────────────────────────────────────────────────
        public ToolType        Tool        { get; set; }
        public List<StrokePoint> Points    { get; } = new List<StrokePoint>(64);

        // ── Appearance ────────────────────────────────────────────────────────
        /// <summary>Base stroke width in canvas pixels at 1× zoom and full pressure.</summary>
        public double BaseWidth            { get; set; } = 4.0;

        /// <summary>Ink colour (alpha channel respected for the highlighter).</summary>
        public Color  StrokeColor          { get; set; } = Colors.White;

        /// <summary>Master opacity override (0–1). Highlighter uses ~0.30.</summary>
        public double Opacity              { get; set; } = 1.0;

        /// <summary>
        /// Angle (radians) of the chisel nib. 0 = horizontal flat nib,
        /// π/4 = 45° standard calligraphy angle.
        /// Ignored for tools other than ChiselPen.
        /// </summary>
        public double ChiselAngle          { get; set; } = Math.PI / 4.0;

        // ── Cached render geometry ────────────────────────────────────────────
        /// <summary>
        /// Lazily-built StreamGeometry for the filled outline polygon.
        /// Rebuilt when Points changes (only happens during an active stroke).
        /// After CommitStroke() is called this is frozen for thread safety.
        /// </summary>
        public StreamGeometry? CachedGeometry { get; set; }

        /// <summary>
        /// Set to true once the user lifts the pen.  Frozen geometry is safe to
        /// render from the UI thread without locking.
        /// </summary>
        public bool IsCommitted { get; private set; }

        public void Commit()
        {
            IsCommitted = true;
            CachedGeometry?.Freeze();
        }

        // ── Eraser metadata ───────────────────────────────────────────────────
        /// <summary>
        /// True when this stroke acts as a virtual eraser record so that
        /// undo correctly "un-erases" any strokes it removed.
        /// </summary>
        public bool IsEraserRecord  { get; set; }

        /// <summary>
        /// IDs of strokes deleted by this eraser record — used by undo.
        /// </summary>
        public List<Guid> ErasedStrokeIds { get; } = new List<Guid>();
    }
}
