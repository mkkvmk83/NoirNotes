using System;
using System.Collections.Generic;
using System.Windows;

namespace NoirNotes.Models
{
    /// <summary>
    /// A single saved A4 page in the session.
    /// When the user presses "Save as A4 Page" the current canvas is snapshotted
    /// into one of these and the canvas is cleared.
    ///
    /// An AppPage stores strokes in canvas-space so the PDF renderer can
    /// re-draw them at any DPI without loss of quality.
    /// </summary>
    public sealed class AppPage
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public Guid   Id          { get; } = Guid.NewGuid();
        public int    PageNumber  { get; set; }
        public string Title       { get; set; } = string.Empty;
        public DateTime SavedAt   { get; set; } = DateTime.Now;

        // ── Content ───────────────────────────────────────────────────────────
        /// <summary>All strokes visible on this page, in draw order.</summary>
        public List<VectorStroke> Strokes { get; } = new List<VectorStroke>();

        // ── Viewport metadata ─────────────────────────────────────────────────
        /// <summary>
        /// The canvas-space rect that maps to an A4 sheet at 96 dpi equivalent.
        /// Width × Height = 794 × 1123 points (A4 at 96 ppi).
        /// Stored so the PDF exporter knows exactly which region to render.
        /// </summary>
        public Rect A4Viewport { get; set; }

        /// <summary>Background colour of the canvas at save time.</summary>
        public System.Windows.Media.Color BackgroundColor { get; set; }
            = System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x14);

        // ── Thumbnail (optional preview in session panel) ──────────────────────
        /// <summary>128×181 px PNG bytes for the session strip — generated lazily.</summary>
        public byte[]? ThumbnailPng { get; set; }
    }
}
