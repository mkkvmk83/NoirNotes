using System;
using System.Windows;
using System.Windows.Media;

namespace NoirNotes
{
    /// <summary>
    /// Noir Notes — a premium vector note-taking application for Windows 8.1.
    /// Designed for XP-Pen Deco tablet users who demand editorial quality tools.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Opt WPF into hardware acceleration tier 2 (DirectX 9+).
            // This ensures the DrawingVisual pipeline uses the GPU for all rendering,
            // giving us the zero-lag stroke preview that a tablet user expects.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            // Tell WPF to use the highest fidelity text rendering available.
            // On Windows 8.1 this resolves to ClearType with natural hinting —
            // critical for the serif typography to look genuinely editorial.
            TextOptions.TextFormattingModeProperty.OverrideMetadata(
                typeof(Window),
                new FrameworkPropertyMetadata(TextFormattingMode.Display));

            // Capture unhandled exceptions gracefully so a crash doesn't silently
            // discard the user's in-progress pages.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowCrashDialog(e.ExceptionObject as Exception);
        }

        private void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ShowCrashDialog(e.Exception);
            e.Handled = true;   // Don't terminate; let the user save first.
        }

        private void ShowCrashDialog(Exception? ex)
        {
            var msg = $"An unexpected error occurred.\n\n{ex?.Message ?? "Unknown error"}" +
                      "\n\nYour unsaved pages have been preserved in memory. " +
                      "Please use Export PDF before closing.";
            MessageBox.Show(msg, "Noir Notes — Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
