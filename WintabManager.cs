using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NoirNotes.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Wintab32 P/Invoke Declarations
    //  Reference: Wacom Wintab Specification 1.4
    //             https://developer-docs.wacom.com/intuos-cintiq-business-tablet/docs/wintab
    //
    //  XP-Pen drivers install wintab32.dll into System32 / SysWOW64.
    //  The WM_PACKET message fires once per tablet sample at the polling rate
    //  configured in the XP-Pen driver settings (typically 200–266 Hz).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wintab event arguments carrying a single raw tablet packet.
    /// </summary>
    public sealed class WintabPacketEventArgs : EventArgs
    {
        /// <summary>X in Wintab device units (needs mapping to screen/canvas coords).</summary>
        public int   X        { get; }
        /// <summary>Y in Wintab device units.</summary>
        public int   Y        { get; }
        /// <summary>Normal pressure, 0 – <see cref="WintabManager.MaxPressure"/>.</summary>
        public int   Pressure { get; }
        /// <summary>X-tilt, –900 to +900 (tenths of a degree).</summary>
        public int   TiltX    { get; }
        /// <summary>Y-tilt, –900 to +900.</summary>
        public int   TiltY    { get; }

        public WintabPacketEventArgs(int x, int y, int pressure, int tiltX, int tiltY)
        {
            X        = x;
            Y        = y;
            Pressure = pressure;
            TiltX    = tiltX;
            TiltY    = tiltY;
        }
    }

    /// <summary>
    /// Hooks into the Wintab API to receive high-fidelity pen data from the
    /// XP-Pen Deco tablet.
    ///
    /// On Windows 8.1 with XP-Pen drivers installed, WPF's built-in stylus API
    /// (StylusPlugIn) already provides pressure from the Windows Ink stack.
    /// This class is the lower-level alternative that bypasses the Windows Ink
    /// pipeline and reads directly from the Wintab driver — useful when you
    /// need absolute coordinates or higher polling frequency than Ink provides.
    ///
    /// Usage
    /// ─────
    ///   var wt = new WintabManager(mainWindowHandle);
    ///   wt.PacketReceived += (s, e) => { /* use e.X, e.Pressure etc. */ };
    ///   wt.Open();
    ///   // … later …
    ///   wt.Close();
    ///
    /// If Wintab is unavailable (drivers not installed), <see cref="IsAvailable"/>
    /// returns false and all methods are no-ops.  The application falls back to
    /// the WPF Stylus API automatically.
    /// </summary>
    public sealed class WintabManager : IDisposable
    {
        // ── Wintab constants ──────────────────────────────────────────────────
        private const string Wintab32Dll = "wintab32.dll";

        // WTI_DEVICES / WTI_DDCT — tablet device info category
        private const int  WTI_DEFCONTEXT = 3;     // Default digitiser context
        private const int  WTI_DEVICES    = 100;
        private const int  DVC_NPRESSURE  = 15;    // Normal pressure axis info
        private const int  DVC_ORIENTATION = 17;   // Orientation (tilt) axis info

        // Context options
        private const uint CXO_SYSTEM     = 0x0001;
        private const uint CXO_PEN        = 0x0002;
        private const uint PK_X           = 0x0040;
        private const uint PK_Y           = 0x0080;
        private const uint PK_NORMAL_PRESSURE = 0x0400;
        private const uint PK_ORIENTATION  = 0x1000;

        // WM_PACKET message posted by wintab32 to our window.
        private const int  WT_PACKET      = 0x7FF0;
        private const int  WT_PROXIMITY   = 0x7FF5;

        // Max pressure resolution — 2048 levels on XP-Pen Deco.
        public const  int  MaxPressure    = 2047;

        // ── Native structs ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct LOGCONTEXT
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)]
            public string lcName;
            public uint   lcOptions;
            public uint   lcStatus;
            public uint   lcLocks;
            public uint   lcMsgBase;
            public uint   lcDevice;
            public uint   lcPktRate;
            public uint   lcPktData;
            public uint   lcPktMode;
            public uint   lcMoveMask;
            public uint   lcBtnUpMask;
            public uint   lcBtnDnMask;
            public int    lcInOrgX, lcInOrgY, lcInOrgZ;
            public int    lcInExtX, lcInExtY, lcInExtZ;
            public int    lcOutOrgX, lcOutOrgY, lcOutOrgZ;
            public int    lcOutExtX, lcOutExtY, lcOutExtZ;
            public int    lcSensX, lcSensY, lcSensZ;
            public int    lcSysMode;
            public int    lcSysOrgX, lcSysOrgY;
            public int    lcSysExtX, lcSysExtY;
            public int    lcSysSensX, lcSysSensY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PACKET
        {
            public int pkX;
            public int pkY;
            public int pkNormalPressure;
            public int pkOrientationAzimuth;
            public int pkOrientationAltitude;
        }

        // ── P/Invoke ──────────────────────────────────────────────────────────

        [DllImport(Wintab32Dll, SetLastError = true)]
        private static extern bool WTInfo(uint wCategory, uint nIndex, IntPtr lpOutput);

        [DllImport(Wintab32Dll, SetLastError = true)]
        private static extern IntPtr WTOpen(IntPtr hWnd, ref LOGCONTEXT lpLogCtx, bool fEnable);

        [DllImport(Wintab32Dll, SetLastError = true)]
        private static extern bool WTClose(IntPtr hCtx);

        [DllImport(Wintab32Dll, SetLastError = true)]
        private static extern bool WTPacket(IntPtr hCtx, uint wSerial, ref PACKET lpPkt);

        [DllImport(Wintab32Dll, SetLastError = true)]
        private static extern bool WTEnable(IntPtr hCtx, bool fEnable);

        // ── State ─────────────────────────────────────────────────────────────
        private IntPtr          _hCtx = IntPtr.Zero;
        private HwndSource?     _hwndSource;
        private readonly IntPtr _hwnd;
        private bool            _disposed;

        public bool IsAvailable  { get; private set; }
        public bool IsOpen       => _hCtx != IntPtr.Zero;

        public event EventHandler<WintabPacketEventArgs>? PacketReceived;
        public event EventHandler? PenProximityEnter;
        public event EventHandler? PenProximityLeave;

        // ── Construction ──────────────────────────────────────────────────────

        public WintabManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
            // Check if Wintab DLL is present (driver installed).
            IsAvailable = CheckWintabAvailable();
        }

        private static bool CheckWintabAvailable()
        {
            try
            {
                IntPtr dummy = IntPtr.Zero;
                WTInfo(0, 0, dummy);    // Will throw if DLL not found.
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Opens a Wintab context and begins receiving WM_PACKET messages.
        /// Must be called after the window handle is created (e.g., in Loaded event).
        /// </summary>
        public bool Open()
        {
            if (!IsAvailable || IsOpen) return false;

            // Read the default context template.
            var lc = new LOGCONTEXT();
            IntPtr lcPtr = Marshal.AllocHGlobal(Marshal.SizeOf(lc));
            try
            {
                WTInfo(WTI_DEFCONTEXT, 0, lcPtr);
                lc = Marshal.PtrToStructure<LOGCONTEXT>(lcPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(lcPtr);
            }

            // Request only the fields we need (minimises packet size → lower latency).
            lc.lcPktData = PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION;
            lc.lcPktMode = 0;   // All fields are absolute (not relative).
            lc.lcOptions |= CXO_SYSTEM;   // Use system (screen) coordinates.
            lc.lcMoveMask = PK_X | PK_Y | PK_NORMAL_PRESSURE;
            lc.lcPktRate = 200; // 200 Hz polling.

            _hCtx = WTOpen(_hwnd, ref lc, true);
            if (_hCtx == IntPtr.Zero) return false;

            // Hook the window's message pump to intercept WM_PACKET.
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);

            return true;
        }

        public void Close()
        {
            if (!IsOpen) return;
            _hwndSource?.RemoveHook(WndProc);
            WTClose(_hCtx);
            _hCtx = IntPtr.Zero;
        }

        public void Enable(bool enable)
        {
            if (IsOpen) WTEnable(_hCtx, enable);
        }

        // ── WndProc hook ──────────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WT_PACKET)
            {
                var pkt = new PACKET();
                if (WTPacket(_hCtx, (uint)wParam.ToInt32(), ref pkt))
                {
                    // Normalise tilt from tenths-of-degrees to –1..+1.
                    double tx = pkt.pkOrientationAzimuth / 900.0;
                    double ty = pkt.pkOrientationAltitude / 900.0;

                    PacketReceived?.Invoke(this, new WintabPacketEventArgs(
                        pkt.pkX,
                        pkt.pkY,
                        pkt.pkNormalPressure,
                        (int)(tx * 900),
                        (int)(ty * 900)));
                }
                handled = false;  // Let WPF also process it.
            }
            else if (msg == WT_PROXIMITY)
            {
                bool enter = (wParam.ToInt32() & 0xFFFF) != 0;
                if (enter) PenProximityEnter?.Invoke(this, EventArgs.Empty);
                else        PenProximityLeave?.Invoke(this, EventArgs.Empty);
                handled = false;
            }
            return IntPtr.Zero;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _disposed = true;
        }
    }
}
