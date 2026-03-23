using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NoirNotes.Models;
using NoirNotes.Tools;

namespace NoirNotes.Engine
{
    /// <summary>
    /// The core rendering surface.
    ///
    /// Architecture
    /// ════════════
    /// InfiniteCanvas is a FrameworkElement subclass that owns a VisualCollection
    /// of DrawingVisual objects — the lightest-weight WPF rendering primitive.
    /// This bypasses the layout system and avoids UIElement overhead, giving us
    /// GPU-composited rendering with essentially zero CPU cost per frame.
    ///
    /// Visual stack (bottom → top):
    ///   [0] _gridVisual        — infinite dot/line grid, updated on transform change
    ///   [1] _strokesVisual     — all committed strokes (re-drawn only when stroke list changes)
    ///   [2] _activeVisual      — the stroke currently being drawn (updated per tablet sample)
    ///
    /// Transform
    /// ─────────
    /// Pan and zoom are implemented as a single TransformGroup on the canvas element.
    /// All coordinates stored in VectorStroke are in *canvas space* (untransformed).
    /// The render transform is applied by WPF's compositor on the GPU.
    ///
    /// Tablet Input
    /// ────────────
    /// Primary path: WPF StylusPlugIn (Windows Ink) — works with XP-Pen out of the box.
    /// Secondary path: Wintab (lower-level) — enabled in MainWindow if WintabManager.IsAvailable.
    /// </summary>
    public sealed class InfiniteCanvas : FrameworkElement
    {
        // ── Visual layer stack ────────────────────────────────────────────────
        private readonly VisualCollection _visuals;
        private readonly DrawingVisual    _gridVisual;
        private readonly DrawingVisual    _strokesVisual;
        private readonly DrawingVisual    _activeStrokeVisual;

        // ── Transform ─────────────────────────────────────────────────────────
        private readonly TranslateTransform _pan    = new TranslateTransform();
        private readonly ScaleTransform     _zoom   = new ScaleTransform(1, 1);
        private readonly TransformGroup     _xform  = new TransformGroup();

        // Current viewport parameters (in canvas coordinates).
        private double _scale    = 1.0;
        private double _offsetX  = 0.0;
        private double _offsetY  = 0.0;

        // ── Stroke storage ────────────────────────────────────────────────────
        private readonly List<VectorStroke> _strokes    = new List<VectorStroke>(512);
        private readonly Stack<VectorStroke> _undoStack  = new Stack<VectorStroke>();
        private VectorStroke?               _active;    // stroke in progress

        // ── Interaction state ─────────────────────────────────────────────────
        private bool    _isPanning;
        private Point   _panStart;

        // ── Tool ──────────────────────────────────────────────────────────────
        public ITool? ActiveTool { get; set; }

        // ── Grid appearance ───────────────────────────────────────────────────
        private static readonly Color GridDotColor =
            Color.FromArgb(50, 0xF4, 0xF0, 0xE8);   // Paper at 20% opacity

        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler? StrokeCommitted;

        // ─────────────────────────────────────────────────────────────────────
        // Construction
        // ─────────────────────────────────────────────────────────────────────

        public InfiniteCanvas()
        {
            // Build transform: zoom first (around origin), then pan.
            _xform.Children.Add(_zoom);
            _xform.Children.Add(_pan);

            // Apply to rendering transform of this element.
            RenderTransform = _xform;
            // The transform origin stays at (0,0) — we translate explicitly.

            // Initialise visual layers.
            _gridVisual         = new DrawingVisual();
            _strokesVisual      = new DrawingVisual();
            _activeStrokeVisual = new DrawingVisual();

            _visuals = new VisualCollection(this)
            {
                _gridVisual,
                _strokesVisual,
                _activeStrokeVisual
            };

            // Enable all input events.
            Focusable         = true;
            IsHitTestVisible  = true;

            // Draw initial grid.
            Loaded += (_, __) => RedrawGrid();
        }

        // ── FrameworkElement overrides ────────────────────────────────────────

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            RedrawGrid();
        }

        // ═════════════════════════════════════════════════════════════════════
        // TABLET INPUT — WPF Stylus (Windows Ink) path
        // These fire for XP-Pen via the Windows 8.1 Tablet PC components.
        // ═════════════════════════════════════════════════════════════════════

        protected override void OnStylusDown(StylusDownEventArgs e)
        {
            base.OnStylusDown(e);
            if (ActiveTool == null) return;

            CaptureStylus();
            e.Handled = true;

            var sp = e.GetStylusPoints(this)[0];
            var pt = ScreenToCanvas(sp.ToPoint());
            BeginStroke(pt, sp.PressureFactor,
                GetTilt(e, StylusPointProperties.XTiltOrientation),
                GetTilt(e, StylusPointProperties.YTiltOrientation));
        }

        protected override void OnStylusMove(StylusMoveEventArgs e)
        {
            base.OnStylusMove(e);
            if (_active == null) return;

            foreach (StylusPoint sp in e.GetStylusPoints(this))
            {
                var pt = ScreenToCanvas(sp.ToPoint());
                ContinueStroke(pt, sp.PressureFactor,
                    GetTilt(e, StylusPointProperties.XTiltOrientation),
                    GetTilt(e, StylusPointProperties.YTiltOrientation));
            }
            e.Handled = true;
        }

        protected override void OnStylusUp(StylusUpEventArgs e)
        {
            base.OnStylusUp(e);
            ReleaseStylusCapture();
            EndStroke();
            e.Handled = true;
        }

        // ── Mouse fallback (for mouse/trackpad testing) ───────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            // Don't handle mouse down if a stylus is active (avoid double events).
            if (e.StylusDevice != null) return;

            if (Keyboard.IsKeyDown(Key.Space))
            {
                _isPanning = true;
                _panStart  = e.GetPosition(this);
                CaptureMouse();
                Cursor = Cursors.SizeAll;
                return;
            }

            if (ActiveTool == null) return;
            CaptureMouse();
            var pt = ScreenToCanvas(e.GetPosition(this));
            BeginStroke(pt, pressure: 0.6, tiltX: 0, tiltY: 0);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseMoveEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.StylusDevice != null) return;

            if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                var dx  = pos.X - _panStart.X;
                var dy  = pos.Y - _panStart.Y;
                _panStart = pos;
                Pan(dx, dy);
                return;
            }

            if (_active == null || e.LeftButton != MouseButtonState.Pressed) return;
            var cpt = ScreenToCanvas(e.GetPosition(this));
            ContinueStroke(cpt, pressure: 0.6, tiltX: 0, tiltY: 0);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (e.StylusDevice != null) return;

            if (_isPanning)
            {
                _isPanning = false;
                ReleaseMouseCapture();
                Cursor = Cursors.Pen;
                return;
            }

            ReleaseMouseCapture();
            EndStroke();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var pos = e.GetPosition(this);
            double factor = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
            ZoomAt(pos, factor);
            e.Handled = true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // WINTAB integration point
        // Called by MainWindow when WintabManager fires a PacketReceived event.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by the Wintab bridge (on the UI thread via Dispatcher) when a
        /// new raw packet arrives from the XP-Pen driver.
        /// </summary>
        public void OnWintabPacket(int rawX, int rawY, int rawPressure,
                                   int rawTiltX, int rawTiltY, bool isPenDown,
                                   Size tabletMaxCoords)
        {
            // Map Wintab screen-absolute coordinates to canvas-local coordinates.
            var screenPt = new Point(
                (rawX / tabletMaxCoords.Width) * SystemParameters.PrimaryScreenWidth,
                (rawY / tabletMaxCoords.Height) * SystemParameters.PrimaryScreenHeight);

            // Convert from screen to this element's local space.
            var localPt  = this.PointFromScreen(screenPt);
            var canvasPt = ScreenToCanvas(localPt);

            double pressure = Math.Clamp(rawPressure / (double)WintabManager.MaxPressure, 0.0, 1.0);
            double tiltX    = rawTiltX / 900.0;
            double tiltY    = rawTiltY / 900.0;

            if (isPenDown)
            {
                if (_active == null)
                    BeginStroke(canvasPt, pressure, tiltX, tiltY);
                else
                    ContinueStroke(canvasPt, pressure, tiltX, tiltY);
            }
            else
            {
                if (_active != null)
                    EndStroke();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // STROKE LIFECYCLE
        // ═════════════════════════════════════════════════════════════════════

        private long _strokeStartTick;

        private void BeginStroke(Point canvasPt, double pressure,
                                  double tiltX, double tiltY)
        {
            if (ActiveTool == null) return;

            _strokeStartTick = Environment.TickCount;
            _active = ActiveTool.CreateStroke();
            _active.Points.Add(MakePoint(canvasPt, pressure, tiltX, tiltY));
            RedrawActiveStroke();
        }

        private void ContinueStroke(Point canvasPt, double pressure,
                                     double tiltX, double tiltY)
        {
            if (_active == null) return;

            // Minimum move distance to avoid jitter from very-still hovering.
            var last = _active.Points[_active.Points.Count - 1];
            if ((canvasPt - last.Position).LengthSquared < 0.5) return;

            _active.Points.Add(MakePoint(canvasPt, pressure, tiltX, tiltY));

            // Rebuild the active stroke geometry every sample.
            _active.CachedGeometry = StrokeGeometryBuilder.Build(_active);
            RedrawActiveStroke();
        }

        private void EndStroke()
        {
            if (_active == null) return;

            if (_active.Tool == ToolType.Eraser)
            {
                // Eraser: find intersecting strokes and remove them.
                PerformErase(_active);
            }
            else if (_active.Points.Count >= 2)
            {
                _active.CachedGeometry = StrokeGeometryBuilder.Build(_active);
                _active.Commit();
                _strokes.Add(_active);
                _undoStack.Clear();     // New stroke invalidates redo history.
                RedrawAllStrokes();
            }

            _active = null;
            ClearActiveStrokeVisual();
            StrokeCommitted?.Invoke(this, EventArgs.Empty);
        }

        private StrokePoint MakePoint(Point p, double pressure,
                                       double tiltX, double tiltY)
        {
            double ms = (Environment.TickCount - _strokeStartTick);
            return new StrokePoint(p, pressure, tiltX, tiltY) { TimestampMs = ms };
        }

        // ═════════════════════════════════════════════════════════════════════
        // ERASER
        // ═════════════════════════════════════════════════════════════════════

        private void PerformErase(VectorStroke eraserStroke)
        {
            if (_active == null) return;

            // Build the eraser's bounding geometry.
            var eraserRect = GetEraserBounds(eraserStroke);

            var record = new VectorStroke
            {
                Tool            = ToolType.Eraser,
                IsEraserRecord  = true
            };

            for (int i = _strokes.Count - 1; i >= 0; i--)
            {
                if (StrokeIntersectsRect(_strokes[i], eraserRect))
                {
                    record.ErasedStrokeIds.Add(_strokes[i].Id);
                    _strokes.RemoveAt(i);
                }
            }

            if (record.ErasedStrokeIds.Count > 0)
            {
                _strokes.Add(record);   // Keep for undo.
                RedrawAllStrokes();
            }
        }

        private static Rect GetEraserBounds(VectorStroke s)
        {
            if (s.Points.Count == 0) return Rect.Empty;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in s.Points)
            {
                minX = Math.Min(minX, p.Position.X);
                minY = Math.Min(minY, p.Position.Y);
                maxX = Math.Max(maxX, p.Position.X);
                maxY = Math.Max(maxY, p.Position.Y);
            }
            double eraseRadius = s.BaseWidth;
            return new Rect(minX - eraseRadius, minY - eraseRadius,
                            maxX - minX + eraseRadius * 2,
                            maxY - minY + eraseRadius * 2);
        }

        private static bool StrokeIntersectsRect(VectorStroke s, Rect r)
        {
            foreach (var p in s.Points)
                if (r.Contains(p.Position)) return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        // UNDO / REDO
        // ═════════════════════════════════════════════════════════════════════

        public bool CanUndo => _strokes.Count > 0;

        public void Undo()
        {
            if (_strokes.Count == 0) return;

            var last = _strokes[_strokes.Count - 1];
            _strokes.RemoveAt(_strokes.Count - 1);
            _undoStack.Push(last);

            if (last.IsEraserRecord)
            {
                // TODO: Restore erased strokes from undo history (requires a
                // separate "erased strokes archive" — left as an exercise; the
                // ID list is already in ErasedStrokeIds for this purpose).
            }

            RedrawAllStrokes();
        }

        // ═════════════════════════════════════════════════════════════════════
        // PAN & ZOOM
        // ═════════════════════════════════════════════════════════════════════

        private void Pan(double dx, double dy)
        {
            _offsetX += dx;
            _offsetY += dy;
            _pan.X = _offsetX;
            _pan.Y = _offsetY;
            RedrawGrid();
        }

        private void ZoomAt(Point screenPivot, double factor)
        {
            double newScale = Math.Clamp(_scale * factor, 0.05, 32.0);
            double actualFactor = newScale / _scale;
            _scale = newScale;

            // Adjust pan so the point under the cursor stays fixed.
            _offsetX = screenPivot.X - (screenPivot.X - _offsetX) * actualFactor;
            _offsetY = screenPivot.Y - (screenPivot.Y - _offsetY) * actualFactor;

            _zoom.ScaleX  = _scale;
            _zoom.ScaleY  = _scale;
            _pan.X        = _offsetX;
            _pan.Y        = _offsetY;

            RedrawGrid();
        }

        /// <summary>Reset viewport to 1× zoom, centred on origin.</summary>
        public void ResetViewport()
        {
            _scale   = 1.0;
            _offsetX = 0;
            _offsetY = 0;
            _zoom.ScaleX = _zoom.ScaleY = 1.0;
            _pan.X = _pan.Y = 0;
            RedrawGrid();
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        /// <summary>
        /// Convert a point in screen-local (element) space to canvas (world) space.
        /// Canvas space = the coordinate system in which strokes are stored.
        /// </summary>
        public Point ScreenToCanvas(Point screenPt)
        {
            return new Point(
                (screenPt.X - _offsetX) / _scale,
                (screenPt.Y - _offsetY) / _scale);
        }

        public Point CanvasToScreen(Point canvasPt)
        {
            return new Point(
                canvasPt.X * _scale + _offsetX,
                canvasPt.Y * _scale + _offsetY);
        }

        // ═════════════════════════════════════════════════════════════════════
        // RENDERING
        // ═════════════════════════════════════════════════════════════════════

        private void RedrawGrid()
        {
            // Compute visible bounds in canvas space.
            var tl = ScreenToCanvas(new Point(0, 0));
            var br = ScreenToCanvas(new Point(ActualWidth, ActualHeight));

            // Dot spacing at current zoom — keep a meaningful density.
            double baseSpacing = 40.0;   // canvas pixels between dots
            double screenSpacing = baseSpacing * _scale;

            // Step up/down through doublings so grid stays readable.
            while (screenSpacing < 20) screenSpacing *= 2;
            while (screenSpacing > 80) screenSpacing /= 2;
            double canvasSpacing = screenSpacing / _scale;

            // Snap grid to multiple of spacing.
            double startX = Math.Floor(tl.X / canvasSpacing) * canvasSpacing;
            double startY = Math.Floor(tl.Y / canvasSpacing) * canvasSpacing;

            var dot   = new SolidColorBrush(GridDotColor);
            dot.Freeze();
            double r = 0.8 / _scale;   // Dot radius stays 0.8 screen pixels.

            using (var dc = _gridVisual.RenderOpen())
            {
                for (double cx = startX; cx <= br.X + canvasSpacing; cx += canvasSpacing)
                {
                    for (double cy = startY; cy <= br.Y + canvasSpacing; cy += canvasSpacing)
                    {
                        dc.DrawEllipse(dot, null, new Point(cx, cy), r, r);
                    }
                }
            }
        }

        private void RedrawAllStrokes()
        {
            using (var dc = _strokesVisual.RenderOpen())
            {
                foreach (var stroke in _strokes)
                {
                    if (stroke.IsEraserRecord) continue;
                    RenderStroke(dc, stroke);
                }
            }
        }

        private void RedrawActiveStroke()
        {
            if (_active?.CachedGeometry == null) return;

            using (var dc = _activeStrokeVisual.RenderOpen())
            {
                RenderStroke(dc, _active);
            }
        }

        private void ClearActiveStrokeVisual()
        {
            using (_activeStrokeVisual.RenderOpen()) { /* intentionally empty */ }
        }

        private static void RenderStroke(DrawingContext dc, VectorStroke stroke)
        {
            if (stroke.CachedGeometry == null) return;

            var color   = stroke.StrokeColor;
            var opacity = stroke.Opacity;

            // Highlighter uses a screen-like blend via a semitransparent bright fill
            // on the dark canvas — visually similar to a multiply-inverted overlay.
            if (stroke.Tool == ToolType.Highlighter)
            {
                // Push an opacity layer so the highlighter blends with everything beneath.
                dc.PushOpacity(opacity);
                dc.DrawGeometry(new SolidColorBrush(color), null, stroke.CachedGeometry);
                dc.Pop();
            }
            else
            {
                var brush = new SolidColorBrush(Color.FromArgb(
                    (byte)(255 * opacity), color.R, color.G, color.B));
                brush.Freeze();
                dc.DrawGeometry(brush, null, stroke.CachedGeometry);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // SESSION MANAGEMENT (called by MainWindow)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Return all committed strokes and clear the canvas for a new page.
        /// </summary>
        public List<VectorStroke> FlushStrokes()
        {
            var copy = new List<VectorStroke>(_strokes);
            _strokes.Clear();
            _undoStack.Clear();
            RedrawAllStrokes();
            RedrawGrid();
            return copy;
        }

        /// <summary>
        /// Render all strokes at the given DPI to a WPF RenderTargetBitmap,
        /// suitable for embedding in the PDF exporter.
        /// </summary>
        public System.Windows.Media.Imaging.RenderTargetBitmap
            RenderToRasterBitmap(List<VectorStroke> strokes,
                                  Rect a4CanvasRect,
                                  int widthPx, int heightPx,
                                  double dpi)
        {
            // We build a fresh DrawingVisual that renders only the requested region,
            // transformed so that a4CanvasRect maps to (0,0)→(widthPx,heightPx).
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Background fill.
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                    null,
                    new Rect(0, 0, widthPx, heightPx));

                // Transform: canvas space → PDF pixel space.
                double sx = widthPx  / a4CanvasRect.Width;
                double sy = heightPx / a4CanvasRect.Height;

                dc.PushTransform(new MatrixTransform(
                    sx, 0, 0, sy,
                    -a4CanvasRect.X * sx,
                    -a4CanvasRect.Y * sy));

                foreach (var stroke in strokes)
                {
                    if (stroke.IsEraserRecord) continue;
                    RenderStroke(dc, stroke);
                }

                dc.Pop();
            }

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                widthPx, heightPx, dpi, dpi,
                PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static double GetTilt(StylusEventArgs e, StylusPointProperty prop)
        {
            try
            {
                var pts = e.GetStylusPoints(null);
                if (pts.Count == 0) return 0;
                if (!pts.Description.HasProperty(prop)) return 0;
                var info = pts.Description.GetPropertyInfo(prop);
                double raw = pts[0].GetPropertyValue(prop);
                // Normalise to –1..+1.
                return 2.0 * (raw - info.Minimum) / (info.Maximum - info.Minimum) - 1.0;
            }
            catch
            {
                return 0;
            }
        }

        public double CurrentScale => _scale;

        /// <summary>
        /// The canvas-space rectangle visible at the current viewport,
        /// useful for framing the "Save as A4" snapshot.
        /// </summary>
        public Rect VisibleCanvasRect
        {
            get
            {
                var tl = ScreenToCanvas(new Point(0, 0));
                var br = ScreenToCanvas(new Point(ActualWidth, ActualHeight));
                return new Rect(tl, br);
            }
        }
    }
}
