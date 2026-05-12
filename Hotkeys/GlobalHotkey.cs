using System;
using BareRecord.Win32;

namespace BareRecord.Hotkeys;

internal sealed partial class GlobalHotkey : IDisposable
{
    public const int Id = 0xB1B1;

    public const uint MOD_ALT      = Native.MOD_ALT;
    public const uint MOD_CONTROL  = Native.MOD_CONTROL;
    public const uint MOD_SHIFT    = Native.MOD_SHIFT;
    public const uint MOD_NOREPEAT = Native.MOD_NOREPEAT;

    private readonly IntPtr _hwnd;
    private bool _disposed;

    /// <summary>
    /// Registers a system-wide hotkey on <paramref name="hwnd"/>. The owning
    /// window's WndProc receives <c>WM_HOTKEY</c> with <c>wParam == Id</c>.
    /// </summary>
    public GlobalHotkey(IntPtr hwnd, uint modifiers, uint virtualKey)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("Window handle is null.", nameof(hwnd));
        if (!Native.RegisterHotKey(hwnd, Id, modifiers | MOD_NOREPEAT, virtualKey))
            throw new InvalidOperationException("Could not register the global hotkey (it may already be in use).");
        _hwnd = hwnd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.UnregisterHotKey(_hwnd, Id);
    }
}
