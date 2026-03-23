# Noir Notes
### A premium vector note-taking application for Windows 8.1 + XP-Pen Deco

---

```
 ██████╗ ██╗      █████╗  ██████╗██╗  ██╗
 ██╔══██╗██║     ██╔══██╗██╔════╝██║ ██╔╝
 ██████╔╝██║     ███████║██║     █████╔╝ 
 ██╔══██╗██║     ██╔══██║██║     ██╔═██╗ 
 ██████╔╝███████╗██║  ██║╚██████╗██║  ██╗
 ╚═════╝ ╚══════╝╚═╝  ╚═╝ ╚═════╝╚═╝  ╚═╝
                    NOTES
```

---

## Overview

Noir Notes is a native Windows desktop note-taking / digital drawing application
built with **C# + WPF targeting .NET 4.5.2** — the framework that ships with
Windows 8.1 out of the box, requiring no additional runtime download.

It is designed as a premium editorial tool: the aesthetic is deliberately sparse
and typographically sophisticated, drawing on the colour palette and authority
of great print journalism.

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| Windows | **8.1** or later |
| .NET Framework | 4.5.2 (pre-installed on Win 8.1) |
| Visual Studio | 2013 / 2015 / 2017 / 2019 / 2022 (any works) |
| XP-Pen drivers | Install from the XP-Pen website — installs wintab32.dll |
| PdfSharp | Installed via NuGet (see below) |
| Cormorant Garamond | TTF files in `/Resources/` (see Font Setup) |

---

## Build Instructions

### 1. Clone / download the project

```bash
git clone https://github.com/yourname/NoirNotes.git
cd NoirNotes
```

### 2. Restore NuGet packages

```
Tools → NuGet Package Manager → Package Manager Console:

  Install-Package PdfSharp -Version 1.50.5147
```

### 3. Add Cormorant Garamond fonts

Download from Google Fonts: https://fonts.google.com/specimen/Cormorant+Garamond

Copy these three TTF files into `NoirNotes/Resources/`:
- `CormorantGaramond-Regular.ttf`
- `CormorantGaramond-Italic.ttf`
- `CormorantGaramond-Bold.ttf`

In Solution Explorer, set their **Build Action** to **Resource**.

The application will gracefully fall back to **Georgia** if the font files
are not present.

### 4. Build and Run

```
Build → Build Solution  (Ctrl+Shift+B)
Debug → Start Debugging  (F5)
```

---

## Tablet Setup (XP-Pen Deco)

### Primary path: Windows Ink (WPF StylusPlugIn)

This works **automatically** if you install the XP-Pen drivers. The driver
registers the tablet as a Windows 8.1-compatible pen device, and WPF's built-in
`StylusPlugIn` API receives pressure and tilt data with no extra configuration.

### Secondary path: Wintab (lower latency, raw data)

The XP-Pen Windows driver also installs `wintab32.dll` into System32. Noir Notes
detects this DLL at startup and activates `WintabManager` if found, giving access
to:

- 2048-level pressure resolution (vs ~1024 via Windows Ink)
- Raw XY coordinates at the tablet's full polling rate (up to 266 Hz)
- Azimuth and altitude tilt as reported by the hardware

The status indicator in the bottom-right of the window shows which path is active:

- 🟢 Green dot = Wintab active (XP-Pen driver detected)
- 🟡 Amber dot = Windows Ink fallback (driver not detected, or Wintab unavailable)
- 🔴 Red dot = No tablet detected (mouse-only mode)

---

## Architecture

```
NoirNotes/
├── App.xaml / App.xaml.cs           Application entry point, global resources
├── MainWindow.xaml / .cs            Main window: toolbar, session strip, overlays
│
├── Models/
│   ├── StrokePoint.cs               A single tablet sample (XY, pressure, tilt)
│   ├── VectorStroke.cs              A complete vector stroke (resolution-independent)
│   └── AppPage.cs                   A saved A4 page (list of strokes + viewport)
│
├── Engine/
│   ├── InfiniteCanvas.cs            DrawingVisual-based rendering engine
│   │                                  · Infinite dot grid
│   │                                  · Pan (Space+drag) and zoom (scroll wheel)
│   │                                  · WPF Stylus + Wintab input routing
│   │                                  · Undo / stroke flush for PDF
│   ├── StrokeGeometryBuilder.cs     Converts StrokePoints → filled StreamGeometry
│   │                                  · Variable-width polygon with round caps
│   │                                  · Pressure, velocity, tilt, tool-type logic
│   └── WintabManager.cs             P/Invoke bridge to wintab32.dll
│
├── Tools/
│   └── Tools.cs                     ITool interface + all four tool implementations
│                                      · FountainPenTool  (round nib, velocity-sensitive)
│                                      · ChiselPenTool    (flat nib, angle-sensitive)
│                                      · HighlighterTool  (semi-transparent overlay)
│                                      · EraserTool       (removes whole strokes)
│
├── Controls/
│   ├── FloatingToolPanel.xaml       Draggable pill UI: tools, colours, width, opacity
│   └── FloatingToolPanel.xaml.cs   Drag logic + tool/colour event routing
│
├── Services/
│   └── PdfExportService.cs          Rasterises AppPages → A4 JPEG → PdfSharp PDF
│
└── Resources/
    └── CormorantGaramond-*.ttf      Embedded editorial serif typeface
```

---

## Rendering Pipeline

```
Tablet sample (WM_PACKET or StylusMove)
    │
    ▼
StrokePoint { X, Y, Pressure, TiltX, TiltY, Timestamp }
    │
    ▼ (per sample, real-time)
StrokeGeometryBuilder.Build()
    │  · Compute direction vector at each point (look-ahead smoothing)
    │  · Compute per-point half-width from Pressure × Tool algorithm
    │  · Build left/right edge point lists
    │  · Emit quadratic Bézier polyline for each edge
    │  · Add semicircular round caps at start/end
    ▼
StreamGeometry (frozen, GPU-resident)
    │
    ▼
DrawingVisual._activeStrokeVisual.RenderOpen()
    │  · DrawGeometry(brush, geometry) — GPU composited, zero-copy
    ▼
WPF compositor → Direct3D → Display
```

Committed strokes are batched into `_strokesVisual` — one `RenderOpen()`
call renders the entire history.  This means frame cost is O(1) regardless
of how many strokes exist, limited only by GPU fill rate.

---

## Tool Algorithms

### Fountain Pen
```
halfWidth = baseWidth × pressure × (1 − min(velocityPxPerSec / 600, 0.6))
```
Fast strokes are thinner (hair lines on quick swings), slow careful strokes
are thick.  Natural dip-pen behaviour.

### Chisel Pen
```
strokeAngle = atan2(dy, dx)              -- direction of the current sample
delta       = strokeAngle − chiselAngle  -- angle vs nib orientation (π/4 default)
widthFactor = 0.08 + 0.92 × |sin(delta)|
tiltBias    = 1.0 + 0.3 × TiltX         -- barrel tilt adjusts the effective nib angle
halfWidth   = baseWidth × pressure × widthFactor × tiltBias
```
Perpendicular to the nib → full width (downstrokes).
Parallel to the nib → 8% of base width (crossstrokes).

### Highlighter
```
halfWidth   = baseWidth × (0.8 + 0.2 × pressure)   -- nearly constant
drawOpacity = 0.35 (capped)
blendMode   = PushOpacity layer in DrawingContext
```

### Eraser
Traces a rectangular bounding box along the eraser path.  Any committed stroke
whose sample points fall inside that box is removed.  The removal is logged as
a `VectorStroke { IsEraserRecord = true }` so Undo can reverse it.

---

## Session & PDF Workflow

```
Write on the infinite canvas
    │
    ▼
[ ⊞ Save A4 ]
    │  · Captures current viewport as AppPage.A4Viewport
    │  · Moves all strokes to AppPage.Strokes
    │  · Generates a 72×102 px PNG thumbnail
    │  · Clears the canvas and resets the viewport
    │  · Adds a thumbnail to the session strip
    ▼
Keep writing on the fresh canvas
    │
    ▼  (repeat as needed)
    │
[ ↓ Export PDF ]
    │  · For each AppPage:
    │      – RenderToRasterBitmap at 300 DPI (2480 × 3508 px, A4)
    │      – Encode to JPEG at quality 92
    │      – Embed into a PdfSharp PdfPage (A4 = 595.28 × 841.89 pt)
    │  · Native Windows SaveFileDialog
    │  · Saves multi-page PDF to local filesystem
    ▼
PDF on disk, all pages preserved
```

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `F` | Fountain Pen |
| `C` | Chisel Pen |
| `H` | Highlighter |
| `E` | Eraser |
| `Space` + drag | Pan canvas |
| Scroll wheel | Zoom in/out (centred on cursor) |
| `Ctrl+Z` | Undo last stroke |
| `Ctrl+S` | Save current view as A4 page |
| `Ctrl+E` | Export PDF |

---

## Colour Palette

| Name | Hex | Use |
|------|-----|-----|
| Void Black | `#141414` | Canvas background, window chrome |
| Charcoal | `#1E1E1E` | Panel surfaces |
| Iron | `#2A2A2A` | Borders, dividers |
| Ash | `#6B6B6B` | Secondary labels |
| Smoke | `#B8B4AC` | Primary UI text |
| Paper | `#F4F0E8` | Default ink colour, primary text highlights |
| Irvin Red | `#E34234` | Accent colour — used sparingly and deliberately |
| Warm Gold | `#C9A84C` | Default highlighter, editorial annotation |

---

## License

MIT — free for personal and commercial use.

---

*"The first duty of a writer is to be read."*
