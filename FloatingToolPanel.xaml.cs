using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NoirNotes.Tools;

namespace NoirNotes.Controls
{
    /// <summary>
    /// A draggable floating panel that hosts tool selection, colour swatches,
    /// brush width, and opacity controls.
    ///
    /// The panel is hosted as an absolutely-positioned child inside a Canvas
    /// on the main window so it floats above the drawing surface.
    ///
    /// Dragging is implemented by tracking the offset between the mouse-down
    /// position and the panel's Canvas.Left/Top properties.
    /// </summary>
    public partial class FloatingToolPanel : UserControl
    {
        // ── Tool instances ────────────────────────────────────────────────────
        public readonly FountainPenTool  FountainPen  = new FountainPenTool();
        public readonly ChiselPenTool    ChiselPen    = new ChiselPenTool();
        public readonly HighlighterTool  Highlighter  = new HighlighterTool();
        public readonly EraserTool       Eraser       = new EraserTool();

        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<ITool>? ToolChanged;

        // ── Current state ─────────────────────────────────────────────────────
        private ITool _currentTool;
        private Color _currentColor = Color.FromRgb(0xF4, 0xF0, 0xE8);

        // Drag state.
        private bool   _isDragging;
        private Point  _dragOffset;

        // ─────────────────────────────────────────────────────────────────────

        public FloatingToolPanel()
        {
            InitializeComponent();
            _currentTool = FountainPen;
            UpdateAllTools();
        }

        // ── Tool selection ────────────────────────────────────────────────────

        private void OnToolSelected(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton btn) return;

            // Uncheck all siblings.
            BtnFountainPen.IsChecked = false;
            BtnChiselPen.IsChecked   = false;
            BtnHighlighter.IsChecked = false;
            BtnEraser.IsChecked      = false;

            btn.IsChecked = true;

            _currentTool = btn.Tag?.ToString() switch
            {
                "FountainPen" => FountainPen,
                "ChiselPen"   => ChiselPen,
                "Highlighter" => Highlighter,
                "Eraser"      => Eraser,
                _             => FountainPen
            };

            ToolChanged?.Invoke(this, _currentTool);
        }

        // ── Colour selection ──────────────────────────────────────────────────

        private void OnColorSelected(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag?.ToString() is not string hex) return;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                SetColor(color);
            }
            catch { /* Malformed tag — ignore. */ }
        }

        private void SetColor(Color c)
        {
            _currentColor = c;
            CurrentColorIndicator.Background = new SolidColorBrush(c);

            // Apply to the appropriate tool(s).
            // Eraser has no colour; Highlighter keeps its warm gold by default
            // but still respects an explicit colour change.
            FountainPen.StrokeColor = c;
            ChiselPen.StrokeColor   = c;
            Highlighter.StrokeColor = c;

            ToolChanged?.Invoke(this, _currentTool);
        }

        // ── Width & opacity ───────────────────────────────────────────────────

        private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double w = e.NewValue;
            FountainPen.BaseWidth  = w;
            ChiselPen.BaseWidth    = w * 2.8;   // Chisel needs more mass.
            Highlighter.BaseWidth  = w * 4.0;
            Eraser.BaseWidth       = w * 3.0;
            ToolChanged?.Invoke(this, _currentTool);
        }

        private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double op = e.NewValue;
            FountainPen.Opacity  = op;
            ChiselPen.Opacity    = op;
            Highlighter.Opacity  = Math.Min(op * 0.45, 0.70);  // Cap highlighter.
            ToolChanged?.Invoke(this, _currentTool);
        }

        private void UpdateAllTools()
        {
            FountainPen.StrokeColor  = _currentColor;
            ChiselPen.StrokeColor    = _currentColor;
        }

        // ── Drag-to-move ──────────────────────────────────────────────────────

        private void OnPanelDragStart(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _isDragging = true;
            _dragOffset = e.GetPosition(Parent as UIElement);
            _dragOffset.X -= Canvas.GetLeft(this);
            _dragOffset.Y -= Canvas.GetTop(this);

            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isDragging) return;

            var pos = e.GetPosition(Parent as UIElement);
            Canvas.SetLeft(this, pos.X - _dragOffset.X);
            Canvas.SetTop(this,  pos.Y - _dragOffset.Y);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
        }
    }
}
