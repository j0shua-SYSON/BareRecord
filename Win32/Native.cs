using System;
using System.Runtime.InteropServices;

namespace BareRecord.Win32;

/// <summary>
/// Minimal P/Invoke surface still needed by BareRecord after the UI was
/// migrated to WinForms: the global hotkey API, WM_HOTKEY, and the
/// fatal-error MessageBox shown when Application.Run throws.
/// </summary>
internal static class Native
{
    public const uint WM_HOTKEY     = 0x0312;

    public const uint MOD_ALT       = 0x0001;
    public const uint MOD_CONTROL   = 0x0002;
    public const uint MOD_SHIFT     = 0x0004;
    public const uint MOD_NOREPEAT  = 0x4000;

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
