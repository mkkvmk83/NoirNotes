// ════════════════════════════════════════════════════════════════════════════
// ITool.cs — Tool contract
// ════════════════════════════════════════════════════════════════════════════
using System.Windows.Media;
using NoirNotes.Models;

namespace NoirNotes.Tools
{
    /// <summary>
    /// Every drawing tool implements this interface.
    /// The tool is responsible for configuring the <see cref="VectorStroke"/>
    /// that will be built from tablet input.
    /// </summary>
    public interface ITool
    {
        ToolType ToolType    { get; }
        string   DisplayName { get; }
        double   BaseWidth   { get; set; }
        Color    StrokeColor { get; set; }
        double   Opacity     { get; set; }

        /// <summary>
        /// Factory: produce a new VectorStroke pre-configured for this tool.
        /// </summary>
        VectorStroke CreateStroke();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// FountainPenTool.cs
// A round-nib pen whose stroke width scales smoothly with pressure.
// Width also attenuates slightly at high velocity for that hair-line speed line.
// ════════════════════════════════════════════════════════════════════════════
namespace NoirNotes.Tools
{
    using System.Windows.Media;
    using NoirNotes.Models;

    public sealed class FountainPenTool : ITool
    {
        public ToolType ToolType    => ToolType.FountainPen;
        public string   DisplayName => "Fountain Pen";
        public double   BaseWidth   { get; set; } = 5.0;
        public Color    StrokeColor { get; set; } = Color.FromRgb(0xF4, 0xF0, 0xE8); // Paper white
        public double   Opacity     { get; set; } = 1.0;

        public VectorStroke CreateStroke() => new VectorStroke
        {
            Tool        = ToolType.FountainPen,
            BaseWidth   = BaseWidth,
            StrokeColor = StrokeColor,
            Opacity     = Opacity
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ChiselPenTool.cs
// A flat calligraphy nib.  Width = baseWidth × |sin(strokeAngle − chiselAngle)|
// giving thick downstrokes, thin crossstrokes, exactly like a broad-edge pen.
// Tilt data from the XP-Pen biases the nib angle dynamically.
// ════════════════════════════════════════════════════════════════════════════
namespace NoirNotes.Tools
{
    using System;
    using System.Windows.Media;
    using NoirNotes.Models;

    public sealed class ChiselPenTool : ITool
    {
        public ToolType ToolType    => ToolType.ChiselPen;
        public string   DisplayName => "Chisel Pen";
        public double   BaseWidth   { get; set; } = 14.0;   // Wider base for calligraphy drama.
        public Color    StrokeColor { get; set; } = Color.FromRgb(0xF4, 0xF0, 0xE8);
        public double   Opacity     { get; set; } = 1.0;

        /// <summary>
        /// The resting angle of the nib in radians.
        /// π/4 = 45° gives the classic broad-pen calligraphic proportion.
        /// </summary>
        public double ChiselAngle   { get; set; } = Math.PI / 4.0;

        public VectorStroke CreateStroke() => new VectorStroke
        {
            Tool        = ToolType.ChiselPen,
            BaseWidth   = BaseWidth,
            StrokeColor = StrokeColor,
            Opacity     = Opacity,
            ChiselAngle = ChiselAngle
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// HighlighterTool.cs
// A wide, semi-transparent stroke that "inks" over the dark canvas.
// On a #141414 background, a warm yellow at ~30% opacity produces an
// editorial-quality highlight that feels like it belongs on a proof galley.
// ════════════════════════════════════════════════════════════════════════════
namespace NoirNotes.Tools
{
    using System.Windows.Media;
    using NoirNotes.Models;

    public sealed class HighlighterTool : ITool
    {
        public ToolType ToolType    => ToolType.Highlighter;
        public string   DisplayName => "Highlighter";
        public double   BaseWidth   { get; set; } = 22.0;
        // Warm amber — The New Yorker's marginal annotation colour.
        public Color    StrokeColor { get; set; } = Color.FromRgb(0xC9, 0xA8, 0x4C);
        public double   Opacity     { get; set; } = 0.35;

        public VectorStroke CreateStroke() => new VectorStroke
        {
            Tool        = ToolType.Highlighter,
            BaseWidth   = BaseWidth,
            StrokeColor = StrokeColor,
            Opacity     = Opacity
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// EraserTool.cs
// The eraser traces a path and removes any committed strokes whose bounding
// geometry overlaps it.  The removal is recorded as a VectorStroke with
// IsEraserRecord = true so that Undo can reverse it.
// ════════════════════════════════════════════════════════════════════════════
namespace NoirNotes.Tools
{
    using System.Windows.Media;
    using NoirNotes.Models;

    public sealed class EraserTool : ITool
    {
        public ToolType ToolType    => ToolType.Eraser;
        public string   DisplayName => "Eraser";
        public double   BaseWidth   { get; set; } = 20.0;  // Eraser radius in canvas px.
        public Color    StrokeColor { get; set; } = Colors.Transparent;
        public double   Opacity     { get; set; } = 0.0;

        public VectorStroke CreateStroke() => new VectorStroke
        {
            Tool      = ToolType.Eraser,
            BaseWidth = BaseWidth
        };
    }
}
