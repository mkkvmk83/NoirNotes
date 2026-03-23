using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NoirNotes.Models;

namespace NoirNotes.Engine
{
    /// <summary>
    /// Converts a list of <see cref="StrokePoint"/> samples into a filled
    /// <see cref="StreamGeometry"/> polygon that represents a pressure-sensitive,
    /// variable-width vector stroke.
    ///
    /// Algorithm overview
    /// ══════════════════
    /// At each sample point we compute:
    ///   1. The instantaneous stroke direction vector (forward difference).
    ///   2. The perpendicular (normal) vector.
    ///   3. A half-width = f(pressure, tilt, tool, velocity).
    ///   4. Left and right offset points along the normal.
    ///
    /// The final geometry is a closed polygon:
    ///   left[0] → left[n]  (rounded start cap) → right[n] → right[0] (rounded end cap)
    ///
    /// We use quadratic Bézier control points to smooth the polygon edges, which
    /// eliminates the faceting visible when using plain line segments.
    /// </summary>
    public static class StrokeGeometryBuilder
    {
        // Smoothing: how many points ahead to look when computing direction.
        // Higher = smoother direction, lower = more responsive to quick curves.
        private const int LookAhead = 2;

        // Minimum half-width in canvas pixels so the stroke is always visible.
        private const double MinHalfWidth = 0.4;

        /// <summary>
        /// Build a filled path geometry for the given stroke data.
        /// Returns null if there are fewer than 2 points (can't form a path).
        /// </summary>
        public static StreamGeometry? Build(VectorStroke stroke)
        {
            var pts = stroke.Points;
            if (pts.Count < 2) return null;

            var leftEdge  = new List<Point>(pts.Count);
            var rightEdge = new List<Point>(pts.Count);

            // ── 1. Compute velocity for width modulation ───────────────────────
            double[] velocities = ComputeVelocities(pts);

            // ── 2. Compute per-point widths ────────────────────────────────────
            for (int i = 0; i < pts.Count; i++)
            {
                double halfW = ComputeHalfWidth(stroke, pts, velocities, i);

                Vector dir = GetDirection(pts, i);
                // Perpendicular (rotate 90°)
                Vector normal = new Vector(-dir.Y, dir.X);

                leftEdge.Add(pts[i].Position + normal * halfW);
                rightEdge.Add(pts[i].Position - normal * halfW);
            }

            // ── 3. Build geometry ──────────────────────────────────────────────
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(leftEdge[0], isFilled: true, isClosed: true);

                // Forward pass — left edge (smooth Béziers)
                DrawSmoothedPolyline(ctx, leftEdge, 1);

                // Rounded end cap (semicircle approximated by two cubics)
                DrawRoundCap(ctx, pts[pts.Count - 1].Position,
                             GetDirection(pts, pts.Count - 1),
                             ComputeHalfWidth(stroke, pts, velocities, pts.Count - 1),
                             clockwise: true);

                // Backward pass — right edge
                DrawSmoothedPolylineReverse(ctx, rightEdge);

                // Rounded start cap (back at pts[0])
                DrawRoundCap(ctx, pts[0].Position,
                             GetDirection(pts, 0),
                             ComputeHalfWidth(stroke, pts, velocities, 0),
                             clockwise: false);
            }

            return sg;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static double ComputeHalfWidth(VectorStroke stroke,
                                               List<StrokePoint> pts,
                                               double[] velocities,
                                               int i)
        {
            double p  = pts[i].Pressure;  // 0..1
            double bw = stroke.BaseWidth;

            double hw;
            switch (stroke.Tool)
            {
                case ToolType.FountainPen:
                {
                    // Velocity attenuation: faster strokes are thinner (like a real dip pen).
                    double velFactor = 1.0 - Math.Min(velocities[i] / 600.0, 0.6);
                    hw = bw * p * velFactor;
                    break;
                }

                case ToolType.ChiselPen:
                {
                    // The chisel nib width depends on the angle between the stroke
                    // direction and the nib orientation.  Perpendicular to the nib
                    // = thick; parallel = very thin (like the thin cross-stroke of a
                    // calligraphic capital 'H').
                    Vector dir = GetDirection(pts, i);
                    double strokeAngle = Math.Atan2(dir.Y, dir.X);
                    double delta       = strokeAngle - stroke.ChiselAngle;

                    // |sin(delta)| maps 0 (parallel) → 0.0 and 90° → 1.0.
                    double widthFactor = Math.Abs(Math.Sin(delta));
                    // Reserve a minimum thin crossbar width (0.08 * baseWidth).
                    widthFactor = 0.08 + 0.92 * widthFactor;

                    // Tilt modulation: physical tilt of the pen biases the nib angle.
                    double tiltBias = 1.0 + 0.3 * pts[i].TiltX;

                    hw = bw * p * widthFactor * tiltBias;
                    break;
                }

                case ToolType.Highlighter:
                {
                    // Highlighter: mostly constant width, very slight pressure response.
                    hw = bw * (0.8 + 0.2 * p);
                    break;
                }

                default:
                    hw = bw * p;
                    break;
            }

            return Math.Max(hw, MinHalfWidth);
        }

        private static double[] ComputeVelocities(List<StrokePoint> pts)
        {
            var v = new double[pts.Count];
            v[0] = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double dt = Math.Max(pts[i].TimestampMs - pts[i - 1].TimestampMs, 1.0);
                double d  = (pts[i].Position - pts[i - 1].Position).Length;
                v[i] = d / dt * 1000.0; // pixels per second
            }
            return v;
        }

        /// <summary>Unit direction vector at point i using a look-ahead for smoothness.</summary>
        private static Vector GetDirection(List<StrokePoint> pts, int i)
        {
            int ahead  = Math.Min(i + LookAhead, pts.Count - 1);
            int behind = Math.Max(i - LookAhead, 0);

            Vector dir = pts[ahead].Position - pts[behind].Position;
            if (dir.LengthSquared < 1e-6) dir = new Vector(1, 0);
            dir.Normalize();
            return dir;
        }

        private static void DrawSmoothedPolyline(StreamGeometryContext ctx,
                                                 List<Point> pts, int startIndex)
        {
            for (int i = startIndex; i < pts.Count; i++)
            {
                // Simple quadratic Bézier: control point is the previous point,
                // endpoint is the midpoint — gives a smooth pass-through curve.
                if (i == startIndex)
                {
                    ctx.LineTo(pts[i], isStroked: false, isSmoothJoin: true);
                }
                else
                {
                    var mid = Midpoint(pts[i - 1], pts[i]);
                    ctx.QuadraticBezierTo(pts[i - 1], mid, isStroked: false, isSmoothJoin: true);
                }
            }
            // Final segment to the last point.
            if (pts.Count > startIndex)
                ctx.LineTo(pts[pts.Count - 1], isStroked: false, isSmoothJoin: true);
        }

        private static void DrawSmoothedPolylineReverse(StreamGeometryContext ctx,
                                                        List<Point> pts)
        {
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                ctx.LineTo(pts[i], isStroked: false, isSmoothJoin: true);
            }
        }

        /// <summary>
        /// Approximates a semicircular cap at the start or end of the stroke.
        /// We use two quadratic Bézier arcs (each covering a quarter circle).
        /// </summary>
        private static void DrawRoundCap(StreamGeometryContext ctx,
                                         Point center,
                                         Vector direction,
                                         double halfWidth,
                                         bool clockwise)
        {
            // The cap runs from one edge point, around the tip, to the other edge.
            Vector perp  = new Vector(-direction.Y, direction.X);
            Vector along = clockwise ? direction : -direction;

            // Approximate a semicircle with two quadratic Béziers.
            // Magic constant 0.5523 approximates π/4 for Bézier circles.
            const double K = 0.5523;

            Point  p0  = center + perp * halfWidth;
            Point  p2  = center + along * halfWidth;
            Point  cp0 = center + perp * halfWidth + along * halfWidth * K;

            Point  p3  = center - perp * halfWidth;
            Point  cp2 = center + along * halfWidth - perp * halfWidth * K;

            ctx.QuadraticBezierTo(cp0, p2, isStroked: false, isSmoothJoin: true);
            ctx.QuadraticBezierTo(cp2, p3, isStroked: false, isSmoothJoin: true);
        }

        private static Point Midpoint(Point a, Point b)
            => new Point((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
    }
}
