using System.Windows;

namespace NoirNotes.Models
{
    /// <summary>
    /// A single stylus sample captured from the tablet driver.
    /// All values are normalised to [0, 1] ranges unless documented otherwise.
    /// </summary>
    public sealed class StrokePoint
    {
        /// <summary>Canvas-space position (before any pan/zoom transform).</summary>
        public Point Position { get; set; }

        /// <summary>
        /// Pen pressure, 0.0 (hovering/lightest) → 1.0 (full press).
        /// Sourced from Wintab NormalPressure or WPF StylusPoint.PressureFactor.
        /// </summary>
        public double Pressure { get; set; } = 0.5;

        /// <summary>
        /// Horizontal tilt of the stylus barrel, –1.0 (left) → +1.0 (right).
        /// Used by the Chisel Pen to bias the nib angle.
        /// </summary>
        public double TiltX { get; set; } = 0.0;

        /// <summary>
        /// Vertical tilt of the stylus barrel, –1.0 (towards user) → +1.0 (away).
        /// </summary>
        public double TiltY { get; set; } = 0.0;

        /// <summary>
        /// Milliseconds since the stroke started; used for velocity-based width
        /// smoothing (fast strokes → thinner lines, natural calligraphic behaviour).
        /// </summary>
        public double TimestampMs { get; set; } = 0.0;

        public StrokePoint() { }

        public StrokePoint(Point position, double pressure = 0.5,
                           double tiltX = 0.0, double tiltY = 0.0)
        {
            Position    = position;
            Pressure    = pressure;
            TiltX       = tiltX;
            TiltY       = tiltY;
        }
    }
}
