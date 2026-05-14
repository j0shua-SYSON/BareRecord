using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using BareRecord.Capture;
using BareRecord.Hotkeys;
using BareRecord.Recording;
using BareRecord.UI.Controls;
using BareRecord.Win32;
using Windows.Graphics.Capture;

#pragma warning disable CS8618

namespace BareRecord.UI;

internal sealed partial class MainWindow : Form
{
    // ── colors (single source of truth) ────────────────────────────────────
    private static readonly Color C_Background  = Color.FromArgb(255, 14, 14, 16);
    private static readonly Color C_TextPrimary = Color.FromArgb(255, 235, 235, 240);
    private static readonly Color C_TextMuted   = Color.FromArgb(255, 150, 150, 158);
    private static readonly Color C_TextDim     = Color.FromArgb(255, 110, 110, 118);
    private static readonly Color C_SectionCaps = Color.FromArgb(255, 130, 130, 140);
    private static readonly Color C_FieldFill   = Color.FromArgb(255, 22, 22, 26);
    private static readonly Color C_FieldBorder = Color.FromArgb(255, 50, 50, 56);
    private static readonly Color C_Accent      = Color.FromArgb(255, 220, 38, 38);
    private static readonly Color C_AccentMute  = Color.FromArgb(255, 80, 30, 32);

    // ── chrome / typography ─────────────────────────────────────────────────
    private Label _lblTitle;
    private Label _lblSubtitle;
    private Label _lblStatus;

    // ── action row ──────────────────────────────────────────────────────────
    private RoundedButton _btnRecord;
    private RoundedButton _btnPause;

    // ── source section ──────────────────────────────────────────────────────
    private SegmentedControl _segSource;
    private Label _lblDisplay;
    private ComboBox _cboMonitor;
    private List<MonitorInfo> _monitors = new();

    // ── audio section ───────────────────────────────────────────────────────
    private PillToggle _pillSystem;
    private PillToggle _pillMic;
    private LevelMeter _meterSystem;
    private LevelMeter _meterMic;
    private Audio.WasapiLoopback? _idleMeterSystem;
    private Audio.WasapiLoopback? _idleMeterMic;

    // ── options section ─────────────────────────────────────────────────────
    private PillToggle _pillCountdown;
    private PillToggle _pillHighlight;
    private NumericUpDown _numAutoStop;
    private Label _lblAutoStopUnit;

    // ── save location section ───────────────────────────────────────────────
    private TextBox _txtOutput;
    private RoundedButton _btnChangeFolder;
    private RoundedButton _btnOpenFile;
    private RoundedButton _btnOpenFolder;
    private RoundedButton _btnTrouble;

    private ToolTip _toolTip;

    // ── state ──────────────────────────────────────────────────────────────
    private Settings _settings;
    private RecordingSession? _session;
    private GlobalHotkey? _hotkey;
    private System.Windows.Forms.Timer _timer;
    private CursorHighlight? _cursorHighlight;

    private DateTime _startedUtc;
    private TimeSpan _pausedDuration;
    private DateTime? _pausedAtUtc;

    private enum AppState { Idle, Recording, Paused, Saved }
    private AppState _state = AppState.Idle;
    private string? _lastSavedPath;
    private bool _toggleInFlight;
    private bool _loadingSettings;
    private bool _closingInFlight;

    public MainWindow()
    {
        _settings = Settings.Load();

        Text = "BareRecord";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = C_Background;
        ForeColor = C_TextPrimary;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _toolTip = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 400,
            ReshowDelay  = 100,
            ShowAlways   = true,
        };

        _monitors = MonitorEnumerator.Enumerate();
        InitializeComponent();
        ApplySettingsToFields();
        ApplyState(AppState.Idle);

        // Single UI timer drives both elapsed-time display and meter animation.
        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        InitTray();
        Resize    += OnResizeForTray;
        Load      += OnLoad;
        FormClosing += OnFormClosing;
        FormClosed  += OnFormClosed;
    }

    // ── layout ─────────────────────────────────────────────────────────────
    private void InitializeComponent()
    {
        const int margin = 28;
        const int contentW = 684;          // window width 740 = margin + content + margin
        int y = 22;

        _lblTitle = new Label
        {
            Text = "BareRecord",
            Font = new Font("Segoe UI", 26F, FontStyle.Bold),
            ForeColor = C_TextPrimary,
            AutoSize = true,
            Location = new Point(margin, y),
        };
        Controls.Add(_lblTitle);
        y += 50;

        _lblSubtitle = new Label
        {
            Text = "Simple, local screen recording — nothing else.",
            Font = new Font("Segoe UI", 10.5F),
            ForeColor = C_TextMuted,
            AutoSize = true,
            Location = new Point(margin, y),
        };
        Controls.Add(_lblSubtitle);
        y += 38;

        // Action row ────────────────────────────────────────────────────────
        _btnRecord = new RoundedButton
        {
            Text = "●  Start recording",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            Variant = RoundedButton.ButtonVariant.Filled,
            Accent = C_Accent,
            Location = new Point(margin, y),
            Size = new Size(420, 64),
            CornerRadius = 12,
        };
        _btnRecord.Click += async (s, e) => await ToggleAsync();
        Controls.Add(_btnRecord);

        _btnPause = new RoundedButton
        {
            Text = "Pause",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Variant = RoundedButton.ButtonVariant.Subtle,
            Location = new Point(margin + 432, y),
            Size = new Size(contentW - 432, 64),
            CornerRadius = 12,
            Visible = false,
        };
        _btnPause.Click += (s, e) => TogglePause();
        Controls.Add(_btnPause);
        y += 64 + 30;

        // Source section ────────────────────────────────────────────────────
        AddSectionHeader("SOURCE", margin, y);
        y += 26;
        _segSource = new SegmentedControl
        {
            Location = new Point(margin, y),
            Size = new Size(contentW, 44),
            CornerRadius = 12,
        };
        _segSource.SetItems("Monitor", "Window");
        _segSource.SelectedIndexChanged += (s, e) =>
        {
            UpdateMonitorComboVisibility();
            PersistSettings();
        };
        Controls.Add(_segSource);
        y += 44 + 16;

        _lblDisplay = new Label
        {
            Text = "Display",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = C_TextMuted,
            AutoSize = true,
            Location = new Point(margin, y + 6),
            Visible = false,
        };
        Controls.Add(_lblDisplay);

        _cboMonitor = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(margin + 80, y),
            Size = new Size(contentW - 80, 32),
            Font = new Font("Segoe UI", 10F),
            BackColor = C_FieldFill,
            ForeColor = C_TextPrimary,
            Visible = false,
        };
        for (int i = 0; i < _monitors.Count; i++)
            _cboMonitor.Items.Add(_monitors[i].FriendlyName(i + 1));
        _cboMonitor.SelectedIndexChanged += (s, e) => PersistSettings();
        Controls.Add(_cboMonitor);
        if (_monitors.Count > 1) y += 42;

        // Audio section ─────────────────────────────────────────────────────
        AddSectionHeader("AUDIO", margin, y);
        y += 26;

        int pillW = (contentW - 16) / 2;
        _pillSystem = new PillToggle
        {
            Text = "System audio",
            Location = new Point(margin, y),
            Size = new Size(pillW, 48),
            CornerRadius = 24,
            AccentColor = C_Accent,
        };
        _pillSystem.CheckedChanged += (s, e) => { PersistSettings(); UpdateMeterActivity(); };
        Controls.Add(_pillSystem);

        _pillMic = new PillToggle
        {
            Text = "Microphone",
            Location = new Point(margin + pillW + 16, y),
            Size = new Size(pillW, 48),
            CornerRadius = 24,
            AccentColor = C_Accent,
        };
        _pillMic.CheckedChanged += (s, e) => { PersistSettings(); UpdateMeterActivity(); };
        Controls.Add(_pillMic);
        y += 48 + 8;

        _meterSystem = new LevelMeter
        {
            Location = new Point(margin + 6, y),
            Size = new Size(pillW - 12, 8),
        };
        Controls.Add(_meterSystem);

        _meterMic = new LevelMeter
        {
            Location = new Point(margin + pillW + 16 + 6, y),
            Size = new Size(pillW - 12, 8),
        };
        Controls.Add(_meterMic);
        y += 8 + 26;

        // Options section ───────────────────────────────────────────────────
        AddSectionHeader("OPTIONS", margin, y);
        y += 26;

        _pillCountdown = new PillToggle
        {
            Text = "3-2-1 countdown",
            Location = new Point(margin, y),
            Size = new Size(pillW, 36),
            CornerRadius = 18,
            AccentColor = C_Accent,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        };
        _pillCountdown.CheckedChanged += (s, e) => PersistSettings();
        Controls.Add(_pillCountdown);

        // Auto-stop input lives to the right of countdown
        var lblAutoStop = new Label
        {
            Text = "Auto-stop",
            Font = new Font("Segoe UI", 10F),
            ForeColor = C_TextMuted,
            AutoSize = true,
            Location = new Point(margin + pillW + 16, y + 8),
        };
        Controls.Add(lblAutoStop);

        _numAutoStop = new NumericUpDown
        {
            Location = new Point(margin + pillW + 16 + 84, y + 4),
            Size = new Size(76, 28),
            Minimum = 0, Maximum = 36000, Increment = 5,
            Font = new Font("Segoe UI", 10F),
            BackColor = C_FieldFill,
            ForeColor = C_TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _numAutoStop.ValueChanged += (s, e) => PersistSettings();
        Controls.Add(_numAutoStop);

        _lblAutoStopUnit = new Label
        {
            Text = "sec  (0 = off)",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = C_TextDim,
            AutoSize = true,
            Location = new Point(margin + pillW + 16 + 84 + 84, y + 8),
        };
        Controls.Add(_lblAutoStopUnit);
        y += 36 + 8;

        _pillHighlight = new PillToggle
        {
            Text = "Highlight mouse clicks",
            Location = new Point(margin, y),
            Size = new Size(pillW, 36),
            CornerRadius = 18,
            AccentColor = C_Accent,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        };
        _pillHighlight.CheckedChanged += (s, e) => PersistSettings();
        Controls.Add(_pillHighlight);
        _toolTip.SetToolTip(_pillHighlight, "Draws an expanding ring at each click. Best for monitor recordings; window recordings won't capture the overlay.");
        y += 36 + 24;

        // Save location ─────────────────────────────────────────────────────
        AddSectionHeader("SAVE LOCATION", margin, y);
        y += 26;

        _txtOutput = new TextBox
        {
            Location = new Point(margin, y),
            Size = new Size(contentW - 144, 32),
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5F),
            BackColor = C_FieldFill,
            ForeColor = C_TextPrimary,
        };
        Controls.Add(_txtOutput);

        _btnChangeFolder = new RoundedButton
        {
            Text = "Change",
            Variant = RoundedButton.ButtonVariant.Subtle,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(margin + contentW - 130, y - 2),
            Size = new Size(130, 36),
            CornerRadius = 10,
        };
        _btnChangeFolder.Click += (s, e) => BrowseForFolder();
        Controls.Add(_btnChangeFolder);
        y += 32 + 16;

        _btnOpenFile = new RoundedButton
        {
            Text = "Open File",
            Variant = RoundedButton.ButtonVariant.Subtle,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(margin, y),
            Size = new Size(150, 40),
            CornerRadius = 10,
            Visible = false,
        };
        _btnOpenFile.Click += (s, e) => OpenSavedFile();
        Controls.Add(_btnOpenFile);

        _btnOpenFolder = new RoundedButton
        {
            Text = "Open Folder",
            Variant = RoundedButton.ButtonVariant.Subtle,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(margin + 160, y),
            Size = new Size(150, 40),
            CornerRadius = 10,
            Visible = false,
        };
        _btnOpenFolder.Click += (s, e) => OpenSavedFolder();
        Controls.Add(_btnOpenFolder);
        y += 40 + 22;

        // Status / Troubleshoot footer ──────────────────────────────────────
        _lblStatus = new Label
        {
            Text = "Ready",
            Font = new Font("Segoe UI", 10F),
            ForeColor = C_TextMuted,
            AutoSize = true,
            Location = new Point(margin, y + 8),
        };
        Controls.Add(_lblStatus);

        _btnTrouble = new RoundedButton
        {
            Text = "Troubleshooting",
            Variant = RoundedButton.ButtonVariant.Ghost,
            Font = new Font("Segoe UI", 9.5F),
            Location = new Point(margin + contentW - 150, y),
            Size = new Size(150, 32),
            CornerRadius = 8,
        };
        _btnTrouble.Click += (s, e) => OpenDiagnosticLog();
        Controls.Add(_btnTrouble);
        y += 32 + 22;

        ClientSize = new Size(740, y);
    }

    private void AddSectionHeader(string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = C_SectionCaps,
            AutoSize = true,
            Location = new Point(x, y),
        };
        Controls.Add(lbl);
    }

    private void UpdateMonitorComboVisibility()
    {
        bool windowMode = _segSource.SelectedIndex == 1;
        bool show = !windowMode && _monitors.Count > 1;
        _lblDisplay.Visible = show;
        _cboMonitor.Visible = show;
    }

    private void UpdateMeterActivity()
    {
        _meterSystem.Active = _pillSystem.Checked;
        _meterMic.Active    = _pillMic.Checked;
    }

    // ── settings binding ───────────────────────────────────────────────────
    private void ApplySettingsToFields()
    {
        _loadingSettings = true;
        try
        {
            _segSource.SelectedIndex = _settings.Source == "Window" ? 1 : 0;

            int mi = _settings.MonitorIndex;
            if (mi < 0 || mi >= _monitors.Count)
                mi = IndexOfPrimaryMonitor();
            if (_cboMonitor.Items.Count > 0)
                _cboMonitor.SelectedIndex = mi;

            _pillSystem.Checked    = _settings.AudioSystem;
            _pillMic.Checked       = _settings.AudioMic;
            _pillCountdown.Checked = _settings.Countdown;
            _pillHighlight.Checked = _settings.HighlightClicks;
            _numAutoStop.Value     = Math.Min(_numAutoStop.Maximum,
                                       Math.Max(_numAutoStop.Minimum, _settings.AutoStopSeconds));
            _txtOutput.Text        = ShortenPath(_settings.OutputFolder);
            _toolTip.SetToolTip(_txtOutput, _settings.OutputFolder);
            _toolTip.SetToolTip(_btnChangeFolder, "Change save folder");
        }
        finally { _loadingSettings = false; }

        UpdateMonitorComboVisibility();
        UpdateMeterActivity();
    }

    private int IndexOfPrimaryMonitor()
    {
        for (int i = 0; i < _monitors.Count; i++)
            if (_monitors[i].IsPrimary) return i;
        return 0;
    }

    private void PersistSettings()
    {
        if (_loadingSettings) return;
        _settings.Source           = _segSource.SelectedIndex == 1 ? "Window" : "Monitor";
        _settings.MonitorIndex     = _cboMonitor.SelectedIndex >= 0 ? _cboMonitor.SelectedIndex : 0;
        _settings.AudioSystem      = _pillSystem.Checked;
        _settings.AudioMic         = _pillMic.Checked;
        _settings.Countdown        = _pillCountdown.Checked;
        _settings.HighlightClicks  = _pillHighlight.Checked;
        _settings.AutoStopSeconds  = (int)_numAutoStop.Value;
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

    // ── lifecycle ──────────────────────────────────────────────────────────
    private void OnLoad(object? sender, EventArgs e)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            SetStatus("Windows.Graphics.Capture not supported", true);
            _btnRecord.Enabled = false;
            return;
        }

        try { _hotkey = new GlobalHotkey(Handle, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x52); }
        catch { SetStatus("Hotkey unavailable", true); }

        StartIdleMeters();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _hotkey != null && m.WParam.ToInt32() == GlobalHotkey.Id)
            _ = ToggleAsync();
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
        if (string.IsNullOrWhiteSpace(folder)) { SetStatus("Pick a folder", true); return; }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { SetStatus("Can't write to folder: " + ex.Message, true); return; }

        bool isWindow   = _segSource.SelectedIndex == 1;
        var sourceLabel = isWindow ? "Window" : "Monitor";
        var baseName    = _settings.BuildFileName(DateTime.Now, sourceLabel);
        var outputPath  = Path.Combine(folder, baseName + ".mp4");
        _settings.Save();   // persist incremented Counter

        GraphicsCaptureItem? item = null;
        if (isWindow)
        {
            item = await CaptureItemFactory.PickWindowAsync(Handle);
            if (item == null) return;
        }
        else
        {
            // Re-enumerate at start so a hotplugged monitor is picked up
            // without forcing the user to restart the app.
            _monitors = MonitorEnumerator.Enumerate();
            if (_monitors.Count == 0)
            {
                // Enumeration failed entirely — fall back to the primary
                // monitor via MonitorFromPoint. If even that returns null
                // (no display at all), ForPrimaryMonitor throws and the
                // outer catch reports the failure to the status line.
                item = CaptureItemFactory.ForPrimaryMonitor();
            }
            else
            {
                int idx = _settings.MonitorIndex;
                if (idx < 0 || idx >= _monitors.Count) idx = IndexOfPrimaryMonitor();
                var mon = _monitors[idx];
                try { item = CaptureItemFactory.ForMonitor(mon.Handle); }
                catch
                {
                    // Selected monitor disappeared between persist and start;
                    // fall back to primary rather than fail outright.
                    item = CaptureItemFactory.ForPrimaryMonitor();
                }
            }
        }

        // Idle meter pumps must release the audio endpoints before the
        // recording session grabs them — running two clients on the same
        // endpoint is legal but wasteful and we'd be double-paying CPU.
        StopIdleMeters();

        // Pick a screen for the countdown overlay: the selected monitor for
        // monitor mode, otherwise whichever screen currently owns this window.
        Rectangle countdownBounds;
        if (isWindow)
        {
            countdownBounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            int idx = _settings.MonitorIndex;
            if (idx < 0 || idx >= _monitors.Count) idx = IndexOfPrimaryMonitor();
            var m = _monitors[idx];
            countdownBounds = new Rectangle(m.Left, m.Top, m.Width, m.Height);
        }

        if (_settings.MinimizeToTrayWhileRecording) HideToTray();

        if (_settings.Countdown) await CountdownOverlay.RunAsync(countdownBounds);

        // Cursor highlight is monitor-only in practice — for window mode the
        // overlay still appears on screen but isn't in the captured pixels.
        if (_settings.HighlightClicks)
        {
            try { _cursorHighlight = CursorHighlight.Start(); }
            catch { _cursorHighlight = null; }
        }

        var session = new RecordingSession(item, outputPath,
            captureSystemAudio: _pillSystem.Checked,
            captureMic:         _pillMic.Checked,
            captureCursor:      _settings.ShowCursor);
        session.AbortRequested += OnAbortRequested;
        try { session.Start(); }
        catch
        {
            session.Dispose();
            DisposeCursorHighlight();
            StartIdleMeters();   // restore idle meters since we never started
            throw;
        }

        _session = session;
        _startedUtc = DateTime.UtcNow;
        _pausedDuration = TimeSpan.Zero;
        _pausedAtUtc = null;

        ApplyState(AppState.Recording);
        NotifyStarted();
    }

    private async Task StopAsync()
    {
        if (_session == null) return;
        var session = _session;
        _session = null;

        SetStatus("Finalizing…", false);
        DisposeCursorHighlight();

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
            StartIdleMeters();
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
        bool idle      = next == AppState.Idle;
        bool recording = next == AppState.Recording;
        bool paused    = next == AppState.Paused;
        bool saved     = next == AppState.Saved;

        bool settingsEnabled = idle || saved;
        _segSource.Enabled    = settingsEnabled;
        _cboMonitor.Enabled   = settingsEnabled;
        _pillSystem.Enabled   = settingsEnabled;
        _pillMic.Enabled      = settingsEnabled;
        _pillCountdown.Enabled = settingsEnabled;
        _pillHighlight.Enabled = settingsEnabled;
        _numAutoStop.Enabled   = settingsEnabled;
        _btnChangeFolder.Enabled = settingsEnabled;

        if (recording || paused)
        {
            _btnRecord.Text   = "■  Stop recording";
            _btnRecord.Accent = Color.FromArgb(255, 60, 60, 66);
            _btnRecord.Variant = RoundedButton.ButtonVariant.Subtle;
        }
        else
        {
            _btnRecord.Text    = "●  Start recording";
            _btnRecord.Accent  = C_Accent;
            _btnRecord.Variant = RoundedButton.ButtonVariant.Filled;
        }
        _btnRecord.Invalidate();

        _btnPause.Visible = recording || paused;
        _btnPause.Text    = paused ? "Resume" : "Pause";

        _btnOpenFile.Visible = _btnOpenFolder.Visible = saved;

        if (saved)        SetStatus("Saved recording", false);
        else if (idle)    SetStatus("Ready", false);
        else if (paused)  SetStatus("Paused", false);
        else              UpdateElapsed();

        UpdateTrayState();
    }

    private void OnAbortRequested(string reason)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => OnAbortRequested(reason))); return; }
        if (_session == null) return;
        SetStatus(reason, true);
        _ = ToggleAsync();
    }

    // ── timer-driven UI updates ────────────────────────────────────────────
    private void OnTimerTick(object? sender, EventArgs e)
    {
        UpdateElapsed();
        UpdateLevelMeters();
    }

    private void UpdateElapsed()
    {
        if (_session == null || _state != AppState.Recording) return;
        var elapsed = DateTime.UtcNow - _startedUtc - _pausedDuration;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        SetStatus($"Recording  {elapsed:hh\\:mm\\:ss}", false);

        int autoStop = _settings.AutoStopSeconds;
        if (autoStop > 0 && elapsed.TotalSeconds >= autoStop && !_toggleInFlight)
            _ = ToggleAsync();
    }

    private void UpdateLevelMeters()
    {
        float sys, mic;
        if (_state == AppState.Recording || _state == AppState.Paused)
        {
            sys = _session?.SystemAudioPeak ?? 0f;
            mic = _session?.MicPeak ?? 0f;
        }
        else
        {
            sys = _idleMeterSystem?.Peak ?? 0f;
            mic = _idleMeterMic?.Peak    ?? 0f;
        }
        _meterSystem.SetLevel(_pillSystem.Checked ? sys : 0f);
        _meterMic.SetLevel(   _pillMic.Checked    ? mic : 0f);
    }

    // ── idle meter pumps ───────────────────────────────────────────────────
    private void StartIdleMeters()
    {
        if (_state == AppState.Recording || _state == AppState.Paused) return;
        if (!Visible || WindowState == FormWindowState.Minimized) return;

        if (_idleMeterSystem == null)
        {
            // Dispose the local on any failure path — `s.Start()` can throw
            // after the WASAPI pump thread is already spinning, and leaking
            // the instance leaks an MTA thread + COM handles forever.
            var s = new Audio.WasapiLoopback(Audio.WasapiCaptureMode.SystemAudio);
            try { s.Start(); _idleMeterSystem = s; }
            catch { try { s.Dispose(); } catch { } }
        }
        if (_idleMeterMic == null)
        {
            var m = new Audio.WasapiLoopback(Audio.WasapiCaptureMode.Microphone);
            try { m.Start(); _idleMeterMic = m; }
            catch { try { m.Dispose(); } catch { } }
        }
    }

    private void StopIdleMeters()
    {
        try { _idleMeterSystem?.Dispose(); } catch { }
        try { _idleMeterMic?.Dispose();    } catch { }
        _idleMeterSystem = null;
        _idleMeterMic    = null;
    }

    private void DisposeCursorHighlight()
    {
        try { _cursorHighlight?.Dispose(); } catch { }
        _cursorHighlight = null;
    }

    private void SetStatus(string text, bool isError)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = isError ? Color.FromArgb(255, 252, 165, 165) : C_TextMuted;
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
        _timer.Stop();
        _timer.Dispose();
        _hotkey?.Dispose();
        _session?.Dispose();
        DisposeCursorHighlight();
        StopIdleMeters();
        DisposeTray();
    }
}
