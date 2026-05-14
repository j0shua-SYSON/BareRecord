using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace BareRecord.UI;

/// <summary>
/// Tray icon + balloon notifications. Kept separate from
/// <see cref="MainWindow"/> so the layout / state machine code stays compact.
/// </summary>
internal sealed partial class MainWindow
{
    private NotifyIcon? _tray;
    private ToolStripMenuItem? _miShowHide;
    private ToolStripMenuItem? _miStop;

    private void InitTray()
    {
        var menu = new ContextMenuStrip();

        _miShowHide = new ToolStripMenuItem("Hide", null, (s, e) => ToggleVisibleFromTray());
        menu.Items.Add(_miShowHide);

        _miStop = new ToolStripMenuItem("Stop recording", null, async (s, e) =>
        {
            if (_state == AppState.Recording || _state == AppState.Paused)
                await ToggleAsync();
        });
        _miStop.Visible = false;
        menu.Items.Add(_miStop);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (s, e) => Close()));

        _tray = new NotifyIcon
        {
            Icon            = LoadTrayIcon() ?? SystemIcons.Application,
            Text            = "BareRecord",
            Visible         = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (s, e) => RestoreFromTray();
    }

    private static Icon? LoadTrayIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    private void OnResizeForTray(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            ShowInTaskbar = false;
            Hide();
            if (_miShowHide != null) _miShowHide.Text = "Show";
            // No UI to feed; release the audio endpoints rather than spend
            // CPU on meters nobody can see.
            StopIdleMeters();
        }
    }

    private void ToggleVisibleFromTray()
    {
        if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
        else RestoreFromTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_miShowHide != null) _miShowHide.Text = "Show";
        StopIdleMeters();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        if (_miShowHide != null) _miShowHide.Text = "Hide";
        StartIdleMeters();
    }

    private void UpdateTrayState()
    {
        if (_tray == null) return;
        bool active = _state == AppState.Recording || _state == AppState.Paused;
        if (_miStop != null) _miStop.Visible = active;
        _tray.Text = active ? "BareRecord — recording" : "BareRecord";
    }

    private void NotifyStarted()
    {
        if (!_settings.Notifications || _tray == null) return;
        try { _tray.ShowBalloonTip(2500, "BareRecord", "Recording started", ToolTipIcon.Info); }
        catch { }
    }

    private void NotifySaved(string path)
    {
        if (!_settings.Notifications || _tray == null) return;
        try
        {
            _tray.BalloonTipClicked -= OnSavedBalloonClicked;
            _pendingNotifyPath = path;
            _tray.BalloonTipClicked += OnSavedBalloonClicked;
            _tray.ShowBalloonTip(3500, "BareRecord", "Saved " + System.IO.Path.GetFileName(path), ToolTipIcon.Info);
        }
        catch { }
    }

    private string? _pendingNotifyPath;
    private void OnSavedBalloonClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingNotifyPath)) return;
        try { Process.Start(new ProcessStartInfo(_pendingNotifyPath) { UseShellExecute = true }); }
        catch { }
    }

    private void DisposeTray()
    {
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } }
        catch { }
    }
}
