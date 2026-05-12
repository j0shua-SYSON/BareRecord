using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using BareRecord.Capture;
using BareRecord.Hotkeys;
using BareRecord.Recording;
using BareRecord.Win32;
using Windows.Graphics.Capture;

#pragma warning disable CS8618

namespace BareRecord.UI;

internal sealed partial class MainWindow : Form
{
    private Label _lblTitle;
    private Label _lblSubtitle;
    private Label _lblStatus;
    private Button _btnRecord;
    private Button _btnPause;
    private RadioButton _radPrimary, _radWindow, _radRegion;
    private CheckBox _chkSystem, _chkMic;
    private CheckBox _chkCountdown;
    private NumericUpDown _numAutoStop;
    private TextBox _txtOutput;
    private Button _btnChangeFolder, _btnOpenFile, _btnOpenFolder, _btnTrouble;
    private ToolTip _toolTip;

    private Settings _settings;
    private RecordingSession? _session;
    private GlobalHotkey? _hotkey;
    private System.Windows.Forms.Timer _timer;
    
    private DateTime _startedUtc;
    private TimeSpan _pausedDuration;
    private DateTime? _pausedAtUtc;
    
    private enum AppState { Idle, Recording, Paused, Saved }
    private AppState _state = AppState.Idle;
    private string? _lastSavedPath;
    private bool _toggleInFlight;

    public MainWindow()
    {
        _settings = Settings.Load();
        
        Text = "BareRecord";
        Size = new Size(680, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);

        // Native .NET 9 dark mode handles most control backgrounds,
        // but we can set the Form's BackColor slightly customized if we want.
        
        _toolTip = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };

        InitializeComponent();
        ApplySettingsToFields();
        ApplyState(AppState.Idle);
        
        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (s, e) => UpdateElapsed();

        InitTray();
        Resize += OnResizeForTray;

        Load += OnLoad;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    private void InitializeComponent()
    {
        int margin = 24;
        int y = margin;
        
        _lblTitle = new Label { Text = "BareRecord", Font = new Font("Segoe UI", 20F, FontStyle.Bold), AutoSize = true, Location = new Point(margin, y) };
        Controls.Add(_lblTitle);
        y += 40;
        
        _lblSubtitle = new Label { Text = "Simple local screen recording", Font = new Font("Segoe UI", 10F), AutoSize = true, Location = new Point(margin, y), ForeColor = SystemColors.GrayText };
        Controls.Add(_lblSubtitle);
        y += 50;

        _btnRecord = CreateFlatButton("●  Start recording", margin, y, 200, 50, Color.FromArgb(220, 38, 38));
        _btnRecord.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _btnRecord.Click += async (s, e) => await ToggleAsync();
        Controls.Add(_btnRecord);

        _btnPause = CreateFlatButton("❚❚  Pause", margin + 210, y, 120, 50, Color.FromArgb(64, 64, 64));
        _btnPause.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _btnPause.Click += (s, e) => TogglePause();
        _btnPause.Visible = false;
        Controls.Add(_btnPause);
        
        y += 80;

        // Source
        Controls.Add(new Label { Text = "Source", Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoSize = true, Location = new Point(margin, y) });
        y += 30;
        _radPrimary = CreateRadio("Primary monitor", margin, y);
        _radWindow = CreateRadio("Window", margin + 160, y);
        _radRegion = CreateRadio("Region", margin + 280, y);
        y += 45;

        // Audio
        Controls.Add(new Label { Text = "Audio", Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoSize = true, Location = new Point(margin, y) });
        y += 30;
        _chkSystem = CreateCheck("System audio", margin, y);
        _chkMic = CreateCheck("Microphone", margin + 160, y);
        y += 45;

        // Options
        Controls.Add(new Label { Text = "Options", Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoSize = true, Location = new Point(margin, y) });
        y += 30;
        _chkCountdown = CreateCheck("3-2-1 countdown", margin, y);
        var lblAutoStop = new Label { Text = "Auto-stop after", AutoSize = true, Location = new Point(margin + 200, y + 2) };
        Controls.Add(lblAutoStop);
        _numAutoStop = new NumericUpDown {
            Location = new Point(margin + 320, y),
            Width = 70, Minimum = 0, Maximum = 36000, Increment = 5
        };
        _numAutoStop.ValueChanged += (s, e) => PersistSettings();
        Controls.Add(_numAutoStop);
        Controls.Add(new Label { Text = "sec  (0 = off)", AutoSize = true, Location = new Point(margin + 395, y + 2), ForeColor = SystemColors.GrayText });
        y += 45;

        // Save location
        Controls.Add(new Label { Text = "Save location", Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoSize = true, Location = new Point(margin, y) });
        y += 30;
        _txtOutput = new TextBox { Location = new Point(margin, y), Width = 400, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle };
        _btnChangeFolder = CreateFlatButton("Change", margin + 410, y - 2, 80, 28, Color.FromArgb(64, 64, 64));
        _btnChangeFolder.Click += (s, e) => BrowseForFolder();
        Controls.Add(_txtOutput);
        Controls.Add(_btnChangeFolder);
        y += 55;

        _btnOpenFile = CreateFlatButton("Open File", margin, y, 120, 36, Color.FromArgb(64, 64, 64));
        _btnOpenFile.Click += (s, e) => OpenSavedFile();
        _btnOpenFile.Visible = false;
        Controls.Add(_btnOpenFile);

        _btnOpenFolder = CreateFlatButton("Open Folder", margin + 130, y, 120, 36, Color.FromArgb(64, 64, 64));
        _btnOpenFolder.Click += (s, e) => OpenSavedFolder();
        _btnOpenFolder.Visible = false;
        Controls.Add(_btnOpenFolder);
        
        y += 60;
        _lblStatus = new Label { Text = "Ready", AutoSize = true, Location = new Point(margin, y), ForeColor = SystemColors.GrayText };
        Controls.Add(_lblStatus);
        
        _btnTrouble = CreateFlatButton("Troubleshooting", Width - 180, y, 140, 30, Color.Transparent);
        _btnTrouble.Click += (s, e) => OpenDiagnosticLog();
        Controls.Add(_btnTrouble);
    }

    private Button CreateFlatButton(string text, int x, int y, int w, int h, Color bg)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            FlatAppearance = { BorderSize = 0 }
        };
    }

    private RadioButton CreateRadio(string text, int x, int y)
    {
        var rb = new RadioButton { Text = text, Location = new Point(x, y), AutoSize = true };
        rb.CheckedChanged += (s, e) => PersistSettings();
        Controls.Add(rb);
        return rb;
    }

    private CheckBox CreateCheck(string text, int x, int y)
    {
        var cb = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true };
        cb.CheckedChanged += (s, e) => PersistSettings();
        Controls.Add(cb);
        return cb;
    }

    private bool _loadingSettings;

    private void ApplySettingsToFields()
    {
        // Avoid persisting back the partial state we observe while assigning
        // checkbox/radio/numeric values one at a time.
        _loadingSettings = true;
        try
        {
            if (_settings.Source == "Window") _radWindow.Checked = true;
            else if (_settings.Source == "Region") _radRegion.Checked = true;
            else _radPrimary.Checked = true;

            _chkSystem.Checked = _settings.AudioSystem;
            _chkMic.Checked = _settings.AudioMic;
            _chkCountdown.Checked = _settings.Countdown;
            _numAutoStop.Value = Math.Min(_numAutoStop.Maximum, Math.Max(_numAutoStop.Minimum, _settings.AutoStopSeconds));
            _txtOutput.Text = ShortenPath(_settings.OutputFolder);
            _toolTip.SetToolTip(_txtOutput, _settings.OutputFolder);
            _toolTip.SetToolTip(_btnChangeFolder, "Change save folder");
        }
        finally { _loadingSettings = false; }
    }

    private void PersistSettings()
    {
        if (_loadingSettings) return;

        if (_radWindow.Checked) _settings.Source = "Window";
        else if (_radRegion.Checked) _settings.Source = "Region";
        else _settings.Source = "Primary";

        _settings.AudioSystem = _chkSystem.Checked;
        _settings.AudioMic = _chkMic.Checked;
        _settings.Countdown = _chkCountdown.Checked;
        _settings.AutoStopSeconds = (int)_numAutoStop.Value;
        _settings.Save();
    }

    private static string ShortenPath(string full)
    {
        if (string.IsNullOrEmpty(full)) return full;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (full.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                var rel = full[home.Length..].TrimStart('\\', '/');
                return rel.Length == 0 ? full : rel;
            }
        }
        catch { }
        return full;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Windows.Graphics.Capture not supported", true);
            _btnRecord.Enabled = false;
            return;   // Skip the hotkey too — there's nothing it could trigger.
        }

        try { _hotkey = new GlobalHotkey(Handle, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x52); }
        catch { SetStatus("Hotkey unavailable", true); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _hotkey != null && m.WParam.ToInt32() == GlobalHotkey.Id)
        {
            _ = ToggleAsync();
        }
        base.WndProc(ref m);
    }

    private async Task ToggleAsync()
    {
        if (_toggleInFlight) return;
        _toggleInFlight = true;
        try
        {
            if (_state == AppState.Idle || _state == AppState.Saved)
            {
                if (_state == AppState.Saved) TransitionToIdle();
                await StartAsync();
            }
            else
            {
                await StopAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            try { _session?.Dispose(); } catch { }
            _session = null;
            ApplyState(AppState.Idle);
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    private void TogglePause()
    {
        if (_state == AppState.Recording)
        {
            _session?.Pause();
            _pausedAtUtc = DateTime.UtcNow;
            ApplyState(AppState.Paused);
        }
        else if (_state == AppState.Paused)
        {
            if (_pausedAtUtc != null)
            {
                _pausedDuration += DateTime.UtcNow - _pausedAtUtc.Value;
                _pausedAtUtc = null;
            }
            _session?.Resume();
            ApplyState(AppState.Recording);
        }
    }

    private async Task StartAsync()
    {
        var folder = _settings.OutputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            SetStatus("Pick a folder", true);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { SetStatus("Can't write to folder: " + ex.Message, true); return; }

        var sourceLabel = _radWindow.Checked ? "Window" : _radRegion.Checked ? "Region" : "Primary";
        var baseName    = _settings.BuildFileName(DateTime.Now, sourceLabel);
        var outputPath  = Path.Combine(folder, baseName + ".mp4");
        _settings.Save();   // persist incremented Counter

        GraphicsCaptureItem? item = null;
        if (_radPrimary.Checked)
        {
            item = CaptureItemFactory.ForPrimaryMonitor();
        }
        else
        {
            item = await CaptureItemFactory.PickWindowAsync(Handle);
            if (item == null) return;
        }

        if (_settings.MinimizeToTrayWhileRecording) HideToTray();

        if (_settings.Countdown)
        {
            await CountdownOverlay.RunAsync();
        }

        var session = new RecordingSession(item, outputPath,
            captureSystemAudio: _chkSystem.Checked,
            captureMic:         _chkMic.Checked,
            captureCursor:      _settings.ShowCursor);
        session.AbortRequested += OnAbortRequested;
        try { session.Start(); } catch { session.Dispose(); throw; }

        _session = session;
        _startedUtc = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;
        _pausedAtUtc = null;
        _timer.Start();

        ApplyState(AppState.Recording);
        NotifyStarted();
    }

    private async Task StopAsync()
    {
        if (_session == null) return;
        var session = _session;
        _session = null;

        _timer.Stop();
        SetStatus("Finalizing...", false);

        try
        {
            await session.StopAsync();
            _lastSavedPath = session.OutputPath;
            ApplyState(AppState.Saved);
            RestoreFromTray();
            NotifySaved(session.OutputPath);
        }
        catch (Exception ex)
        {
            SetStatus("Recording failed: " + ex.Message, true);
            ApplyState(AppState.Idle);
            RestoreFromTray();
        }
        finally
        {
            session.Dispose();
        }
    }

    private void TransitionToIdle()
    {
        _lastSavedPath = null;
        ApplyState(AppState.Idle);
    }

    private void ApplyState(AppState next)
    {
        _state = next;
        bool idle = next == AppState.Idle;
        bool recording = next == AppState.Recording;
        bool paused = next == AppState.Paused;
        bool saved = next == AppState.Saved;

        bool settingsEnabled = idle || saved;
        _radPrimary.Enabled = _radWindow.Enabled = _radRegion.Enabled = settingsEnabled;
        _chkSystem.Enabled = _chkMic.Enabled = settingsEnabled;
        _btnChangeFolder.Enabled = settingsEnabled;

        _btnRecord.Text = (recording || paused) ? "■  Stop recording" : "●  Start recording";
        _btnRecord.BackColor = (recording || paused) ? Color.FromArgb(64, 64, 64) : Color.FromArgb(220, 38, 38);
        
        _btnPause.Visible = recording || paused;
        _btnPause.Text = paused ? "▶  Resume" : "❚❚  Pause";

        _btnOpenFile.Visible = _btnOpenFolder.Visible = saved;

        if (saved) SetStatus("Saved recording", false);
        else if (idle) SetStatus("Ready", false);
        else if (paused) SetStatus("Paused", false);
        else UpdateElapsed();

        UpdateTrayState();
    }

    private void OnAbortRequested(string reason)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnAbortRequested(reason)));
            return;
        }
        if (_session == null) return;
        SetStatus(reason, true);
        _ = ToggleAsync();
    }

    private void UpdateElapsed()
    {
        if (_session == null || _state != AppState.Recording) return;
        var elapsed = DateTime.UtcNow - _startedUtc - _pausedDuration;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        SetStatus($"Recording  {elapsed:hh\\:mm\\:ss}", false);

        int autoStop = _settings.AutoStopSeconds;
        if (autoStop > 0 && elapsed.TotalSeconds >= autoStop && !_toggleInFlight)
        {
            _ = ToggleAsync();
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = isError ? Color.FromArgb(252, 165, 165) : SystemColors.GrayText;
    }

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Where should recordings be saved?" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.OutputFolder = dlg.SelectedPath;
            _txtOutput.Text = ShortenPath(_settings.OutputFolder);
            _toolTip.SetToolTip(_txtOutput, _settings.OutputFolder);
            PersistSettings();
        }
    }

    private void OpenSavedFile()
    {
        if (!string.IsNullOrEmpty(_lastSavedPath))
            Process.Start(new ProcessStartInfo(_lastSavedPath) { UseShellExecute = true });
    }

    private void OpenSavedFolder()
    {
        if (!string.IsNullOrEmpty(_lastSavedPath))
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
    }

    private void OpenDiagnosticLog()
    {
        var log = DisplayDiagnostics.WriteToTempLog();
        if (log != null) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    private bool _closingInFlight;
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_state == AppState.Recording || _state == AppState.Paused)
        {
            e.Cancel = true;
            if (!_closingInFlight)
            {
                _closingInFlight = true;
                _ = StopThenCloseAsync();
            }
        }
    }

    private async Task StopThenCloseAsync()
    {
        try { await StopAsync(); } catch { }
        Application.Exit();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _hotkey?.Dispose();
        _session?.Dispose();
        DisposeTray();
    }
}