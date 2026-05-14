using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BareRecord.UI;

/// <summary>
/// Click-through overlay system that paints an expanding red ring wherever
/// the user clicks the primary mouse button. One overlay <see cref="Form"/>
/// per <see cref="Screen"/>; a single shared low-level mouse hook routes
/// click points to whichever overlay covers them.
///
/// Lifecycle is tied to a recording session — call <see cref="Start"/> when
/// recording begins and <see cref="Dispose"/> when it ends. The overlay is
/// composited by Windows so it shows up in WGC recordings of any monitor it
/// covers. For window-mode recordings (single-window WGC) the overlay is
/// visible to the user but not in the captured pixels — that's a known
/// limitation of capturing a window rather than a display.
/// </summary>
internal sealed partial class CursorHighlight : IDisposable
{
    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private readonly List<RingOverlay> _overlays = new();

    // The delegate must be kept alive while the hook is installed; if it gets
    // GC'd, Windows will call into a freed address and crash the process.
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookHandle;
    private bool _disposed;

    public static CursorHighlight Start()
    {
        var h = new CursorHighlight();
        h.StartInternal();
        return h;
    }

    private void StartInternal()
    {
        foreach (var screen in Screen.AllScreens)
        {
            var ov = new RingOverlay(screen.Bounds);
            ov.Show();
            _overlays.Add(ov);
        }

        _hookProc = HookProc;
        _hookHandle = SetWindowsHookExW(WH_MOUSE_LL, _hookProc, IntPtr.Zero, 0);
        // SetWindowsHookEx can fail on locked-down environments (UAC, secure
        // desktop, Group Policy). We don't surface the error — the overlay
        // simply won't react to clicks, which is preferable to crashing.
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0 && !_disposed)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                try
                {
                    var st = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    Dispatch(st.pt.X, st.pt.Y);
                }
                catch { /* never block the input pipeline */ }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Dispatch(int screenX, int screenY)
    {
        foreach (var ov in _overlays)
        {
            var b = ov.ScreenBounds;
            if (screenX >= b.X && screenX < b.Right && screenY >= b.Y && screenY < b.Bottom)
            {
                ov.QueueRing(screenX - b.X, screenY - b.Y);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hookHandle); } catch { }
            _hookHandle = IntPtr.Zero;
        }
        _hookProc = null;

        foreach (var ov in _overlays)
        {
            try { ov.Close(); ov.Dispose(); } catch { }
        }
        _overlays.Clear();
    }
}

/// <summary>
/// One screen's worth of click rings. Form is sized to the screen's bounds,
/// click-through, topmost, no taskbar, no activation. Background is black and
/// keyed transparent — the AA falloff of a red ring blends to black at the
/// edges, which fades naturally into the keyed-out background.
/// </summary>
internal sealed partial class RingOverlay : Form
{
    private readonly struct Ring
    {
        public readonly int X, Y;
        public readonly long StartMs;
        public Ring(int x, int y, long startMs) { X = x; Y = y; StartMs = startMs; }
    }

    private const int LifetimeMs   = 360;
    private const float StartRadius = 14f;
    private const float EndRadius   = 46f;

    private static readonly Color KeyColor    = Color.Black;
    private static readonly Color RingColor   = Color.FromArgb(255, 240, 60, 60);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Ring> _rings = new(8);
    private readonly object _gate = new();
    private readonly System.Windows.Forms.Timer _anim;
    public Rectangle ScreenBounds { get; }

    public RingOverlay(Rectangle screenBounds)
    {
        ScreenBounds    = screenBounds;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        Bounds          = screenBounds;
        BackColor       = KeyColor;
        TransparencyKey = KeyColor;
        DoubleBuffered  = true;
        SetStyle(ControlStyles.Selectable, false);

        _anim = new System.Windows.Forms.Timer { Interval = 16 };
        _anim.Tick += (s, e) => Tick();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW  = 0x00000080;
            const int WS_EX_TOPMOST     = 0x00000008;
            const int WS_EX_NOACTIVATE  = 0x08000000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void QueueRing(int localX, int localY)
    {
        // Mouse hook fires on the UI thread, so this is single-threaded today,
        // but the gate is cheap and keeps the structure safe if that changes.
        lock (_gate)
        {
            _rings.Add(new Ring(localX, localY, _clock.ElapsedMilliseconds));
            if (!_anim.Enabled) _anim.Start();
        }
        Invalidate();
    }

    private void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        bool empty;
        lock (_gate)
        {
            _rings.RemoveAll(r => now - r.StartMs > LifetimeMs);
            empty = _rings.Count == 0;
        }
        if (empty) _anim.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        long now = _clock.ElapsedMilliseconds;

        Ring[] snapshot;
        lock (_gate) snapshot = _rings.ToArray();

        foreach (var r in snapshot)
        {
            long age = now - r.StartMs;
            if (age < 0 || age > LifetimeMs) continue;
            float t = age / (float)LifetimeMs;
            float radius = StartRadius + (EndRadius - StartRadius) * t;
            float thickness = 4f * (1f - t) + 0.5f;
            int alpha = (int)(255f * (1f - t));
            if (alpha < 0) alpha = 0;
            using var pen = new Pen(Color.FromArgb(alpha, RingColor.R, RingColor.G, RingColor.B), thickness);
            g.DrawEllipse(pen, r.X - radius, r.Y - radius, radius * 2, radius * 2);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _anim.Stop();
        _anim.Dispose();
        base.OnFormClosed(e);
    }
}
