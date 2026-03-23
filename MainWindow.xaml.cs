using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NoirNotes.Engine;
using NoirNotes.Models;
using NoirNotes.Services;
using NoirNotes.Tools;

namespace NoirNotes
{
    /// <summary>
    /// Main application window — coordinates the canvas, tool panel,
    /// session manager, Wintab integration, and keyboard shortcuts.
    /// </summary>
    public partial class MainWindow : Window
    {
        // ── Sub-systems ────────────────────────────────────────────────────────
        private WintabManager? _wintab;
        private readonly List<AppPage> _pages = new List<AppPage>();

        // ── Viewport tracking for zoom label ──────────────────────────────────
        private readonly DispatcherTimer _zoomLabelTimer;

        // ── Session strip state ───────────────────────────────────────────────
        private bool _sessionStripOpen = false;
        private const double SessionStripWidth = 104;

        // ─────────────────────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();

            // Debounce zoom label updates.
            _zoomLabelTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(150),
                DispatcherPriority.Background,
                (_, __) => UpdateZoomLabel(),
                Dispatcher);
            _zoomLabelTimer.IsEnabled = false;

            // Wire canvas events.
            Canvas.StrokeCommitted += (_, __) => UpdateZoomLabel();

            // Initial tool.
            Canvas.ActiveTool = ToolPanel.FountainPen;
        }

        // ═════════════════════════════════════════════════════════════════════
        // WINDOW LIFECYCLE
        // ═════════════════════════════════════════════════════════════════════

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Give the canvas keyboard focus so stylus/keyboard events work.
            Canvas.Focus();

            // Attempt Wintab initialisation.
            InitialiseWintab();

            // Start the zoom label.
            UpdateZoomLabel();

            // Animate the shortcut hints out after 5 seconds.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var fadeOut = new DoubleAnimation(0.4, 0.0,
                    new Duration(TimeSpan.FromSeconds(1.5)))
                {
                    BeginTime = TimeSpan.FromSeconds(4)
                };
                ShortcutHints.BeginAnimation(OpacityProperty, fadeOut);
            }));
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Warn if there are unsaved pages.
            if (_pages.Count > 0)
            {
                var r = MessageBox.Show(
                    $"You have {_pages.Count} saved page(s) that have not been exported.\n\n" +
                    "Export PDF before closing?",
                    "Noir Notes — Unsaved Session",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (r == MessageBoxResult.Yes)
                {
                    PdfExportService.ExportToPdf(_pages, Canvas);
                }
                else if (r == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _wintab?.Dispose();
        }

        // ═════════════════════════════════════════════════════════════════════
        // WINTAB
        // ═════════════════════════════════════════════════════════════════════

        private void InitialiseWintab()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _wintab = new WintabManager(hwnd);

            if (_wintab.IsAvailable && _wintab.Open())
            {
                // Wintab packets arrive on the UI thread (via WndProc hook).
                bool penDown = false;
                System.Drawing.Size tabletMax = default; // Will be set from first packet.

                _wintab.PenProximityEnter += (_, __) =>
                {
                    PenCursor.Opacity = 0.7;
                    penDown = false;
                };
                _wintab.PenProximityLeave += (_, __) =>
                {
                    PenCursor.Opacity = 0.0;
                    Canvas.OnWintabPacket(0, 0, 0, 0, 0, false,
                        new Size(tabletMax.Width, tabletMax.Height));
                };
                _wintab.PacketReceived += (_, pkt) =>
                {
                    // On first packet, capture the device extents.
                    if (tabletMax.IsEmpty)
                        tabletMax = new System.Drawing.Size(
                            SystemParameters.PrimaryScreenWidth > 0 ? 40000 : 40000,
                            40000);

                    // Track pen-down state by pressure threshold.
                    bool isDown = pkt.Pressure > 20;
                    if (isDown != penDown)
                        penDown = isDown;

                    // Move the custom pen cursor.
                    var screenPt = new Point(
                        (pkt.X / 40000.0) * SystemParameters.PrimaryScreenWidth,
                        (pkt.Y / 40000.0) * SystemParameters.PrimaryScreenHeight);
                    var localPt = Canvas.PointFromScreen(screenPt);
                    System.Windows.Controls.Canvas.SetLeft(
                        PenCursor, localPt.X - 8);
                    System.Windows.Controls.Canvas.SetTop(
                        PenCursor, localPt.Y - 8);

                    Canvas.OnWintabPacket(
                        pkt.X, pkt.Y, pkt.Pressure,
                        pkt.TiltX, pkt.TiltY,
                        isDown,
                        new Size(40000, 40000));
                };

                SetWintabStatus(true, "XP-Pen (Wintab)");
            }
            else
            {
                // Fall back to WPF Stylus API — also works with XP-Pen via Windows Ink.
                _wintab = null;
                SetWintabStatus(null, "Windows Ink (fallback)");
            }
        }

        private void SetWintabStatus(bool? connected, string label)
        {
            WintabLabel.Text = label;
            WintabDot.Fill = connected switch
            {
                true  => new SolidColorBrush(Color.FromRgb(0x4A, 0xC2, 0x6E)), // green
                false => new SolidColorBrush(Color.FromRgb(0xE3, 0x42, 0x34)), // red
                null  => new SolidColorBrush(Color.FromRgb(0xC9, 0xA8, 0x4C))  // amber
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // TOOL SELECTION
        // ═════════════════════════════════════════════════════════════════════

        private void OnToolChanged(object sender, ITool tool)
        {
            Canvas.ActiveTool = tool;
            StatusLeft.Text   = $"{tool.DisplayName}  ·  " +
                                 FormatColor(tool.StrokeColor);
        }

        private static string FormatColor(Color c)
        {
            // Name common editorial colours.
            return (c.R, c.G, c.B) switch
            {
                (0xF4, 0xF0, 0xE8) => "Paper",
                (0xE3, 0x42, 0x34) => "Irvin Red",
                (0xC9, 0xA8, 0x4C) => "Editorial Gold",
                (0x2A, 0x3F, 0x5F) => "Ink Blue",
                (0x8A, 0x8A, 0x8A) => "Graphite",
                (0x4A, 0x7C, 0x59) => "Proof Green",
                _                  => $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // SESSION MANAGEMENT
        // ═════════════════════════════════════════════════════════════════════

        private void OnSaveAsA4Page(object sender, RoutedEventArgs e)
        {
            // Use the currently visible canvas area as the A4 viewport.
            // The user should have positioned and zoomed so that their page
            // content fills the screen — similar to how Concepts works.
            var viewport = Canvas.VisibleCanvasRect;

            // Flush strokes from canvas.
            var strokes = Canvas.FlushStrokes();

            if (strokes.Count == 0)
            {
                MessageBox.Show("The canvas is empty — nothing to save.",
                    "Noir Notes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var page = new AppPage
            {
                PageNumber  = _pages.Count + 1,
                Title       = $"Page {_pages.Count + 1}",
                A4Viewport  = viewport,
                SavedAt     = DateTime.Now
            };
            page.Strokes.AddRange(strokes);

            // Generate a quick 72×102 thumbnail.
            GenerateThumbnail(page);

            _pages.Add(page);
            AddPageThumbToStrip(page);
            UpdatePageCount();

            // Brief confirmation animation on the button.
            FlashButton(BtnSavePage, "✓ Saved");

            // Reset viewport for the next page.
            Canvas.ResetViewport();
        }

        private void OnExportPdf(object sender, RoutedEventArgs e)
        {
            PdfExportService.ExportToPdf(_pages, Canvas);
        }

        private void OnUndo(object sender, RoutedEventArgs e)
        {
            Canvas.Undo();
        }

        // ── Session strip ─────────────────────────────────────────────────────

        private void OnToggleSessionStrip(object sender, RoutedEventArgs e)
        {
            _sessionStripOpen = !_sessionStripOpen;
            var anim = new GridLengthAnimation
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                From     = SessionColumn.Width,
                To       = _sessionStripOpen
                               ? new GridLength(SessionStripWidth)
                               : new GridLength(0)
            };
            SessionColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private void AddPageThumbToStrip(AppPage page)
        {
            var border = new Border
            {
                Style       = (Style)FindResource("PageThumb"),
                ToolTip     = $"{page.Title} — {page.SavedAt:HH:mm}",
                Tag         = page
            };

            // Page number label.
            border.Child = new TextBlock
            {
                Text                = page.PageNumber.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)),
                FontFamily          = (FontFamily)FindResource("SerifDisplay"),
                FontSize            = 22,
                FontStyle           = FontStyles.Italic
            };

            // Show the thumbnail bitmap if available.
            if (page.ThumbnailPng != null)
            {
                var ms  = new System.IO.MemoryStream(page.ThumbnailPng);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                border.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                border.Child      = null;
            }

            PageStripPanel.Children.Add(border);

            // Open the strip automatically on first page save.
            if (_pages.Count == 1 && !_sessionStripOpen)
                OnToggleSessionStrip(this, new RoutedEventArgs());
        }

        private void UpdatePageCount()
        {
            int n = _pages.Count;
            StatusPages.Text  = n == 0
                ? "No saved pages"
                : $"{n} page{(n == 1 ? "" : "s")} saved";
            BtnExport.IsEnabled = n > 0;
        }

        // ── Thumbnail generation ──────────────────────────────────────────────

        private void GenerateThumbnail(AppPage page)
        {
            const int W = 72, H = 102;
            try
            {
                var rtb = Canvas.RenderToRasterBitmap(
                    page.Strokes, page.A4Viewport, W, H, 96);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                page.ThumbnailPng = ms.ToArray();
            }
            catch { /* Non-fatal; the strip just shows a number. */ }
        }

        // ═════════════════════════════════════════════════════════════════════
        // KEYBOARD SHORTCUTS
        // ═════════════════════════════════════════════════════════════════════

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            // Spacebar → show pan hint (InfiniteCanvas handles the actual pan).
            if (e.Key == Key.Space && !e.IsRepeat)
            {
                PanHint.Visibility = Visibility.Visible;
                Canvas.Cursor = Cursors.SizeAll;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (e.Key == Key.Z) { Canvas.Undo(); e.Handled = true; }
                if (e.Key == Key.S) { OnSaveAsA4Page(this, new RoutedEventArgs()); e.Handled = true; }
                if (e.Key == Key.E) { OnExportPdf(this, new RoutedEventArgs()); e.Handled = true; }
            }
            else
            {
                switch (e.Key)
                {
                    case Key.F:
                        ToolPanel.BtnFountainPen.IsChecked = true;
                        ToolPanel.BtnFountainPen.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                    case Key.C:
                        ToolPanel.BtnChiselPen.IsChecked = true;
                        ToolPanel.BtnChiselPen.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                    case Key.H:
                        ToolPanel.BtnHighlighter.IsChecked = true;
                        ToolPanel.BtnHighlighter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                    case Key.E:
                        ToolPanel.BtnEraser.IsChecked = true;
                        ToolPanel.BtnEraser.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                }
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key == Key.Space)
            {
                PanHint.Visibility = Visibility.Collapsed;
                Canvas.Cursor = Cursors.Pen;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // UI HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateZoomLabel()
        {
            double pct = Canvas.CurrentScale * 100;
            ZoomLabel.Text = $"{pct:F0}%";
        }

        private void FlashButton(Button btn, string tempText)
        {
            string original = btn.Content?.ToString() ?? "";
            btn.Content = tempText;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            t.Tick += (_, __) => { btn.Content = original; t.Stop(); };
            t.Start();
        }
    }

    // ── GridLengthAnimation helper (not built into WPF 4.5) ──────────────────

    /// <summary>
    /// Animates a GridLength value (used for the session strip open/close).
    /// </summary>
    public class GridLengthAnimation : AnimationTimeline
    {
        public GridLength From { get; set; }
        public GridLength To   { get; set; }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
                                                object defaultDestinationValue,
                                                AnimationClock animationClock)
        {
            double t = animationClock.CurrentProgress ?? 0;
            // Ease in-out cubic.
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

            double from = From.Value;
            double to   = To.Value;
            return new GridLength(from + (to - from) * t);
        }
    }
}
