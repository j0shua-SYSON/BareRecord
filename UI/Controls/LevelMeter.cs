using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BareRecord.UI.Controls;

/// <summary>
/// Horizontal audio level meter with attack/decay smoothing. The owner pushes
/// raw peak values (0..1) from any thread via <see cref="SetLevel"/>; the
/// control runs its own UI-timer animation so the bar smooths between samples.
/// Holds a brief "peak hold" tick at the highest level seen recently.
/// </summary>
internal sealed partial class LevelMeter : Control
{
    private float _targetLevel;        // most recent raw peak pushed in
    private float _displayLevel;       // smoothed value actually drawn
    private float _holdLevel;          // peak-hold indicator
    private DateTime _holdStamp = DateTime.MinValue;
    private bool _active = true;

    private readonly System.Windows.Forms.Timer _anim;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor   { get; set; } = Color.FromArgb(255, 22, 22, 26);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor  { get; set; } = Color.FromArgb(255, 38, 38, 42);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color LowColor     { get; set; } = Color.FromArgb(255, 96, 220, 120);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color MidColor     { get; set; } = Color.FromArgb(255, 240, 200, 80);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HighColor    { get; set; } = Color.FromArgb(255, 240, 90, 90);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoldColor    { get; set; } = Color.FromArgb(255, 230, 230, 235);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InactiveBar  { get; set; } = Color.FromArgb(255, 60, 60, 66);

    /// <summary>
    /// When false the bar fades to nothing and the meter stops animating —
    /// used when the corresponding audio source is toggled off in the UI.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!_active) { _targetLevel = 0; _holdLevel = 0; }
            Invalidate();
        }
    }

    public LevelMeter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 6;

        _anim = new System.Windows.Forms.Timer { Interval = 33 };
        _anim.Tick += (s, e) => Tick();
    }

    /// <summary>
    /// Push the latest raw peak (0..1). Safe to call from any thread — this
    /// just stores a float; the UI thread reads it in <see cref="Tick"/>.
    /// </summary>
    public void SetLevel(float peak)
    {
        if (peak < 0) peak = 0;
        if (peak > 1) peak = 1;
        // Plain assignment; on x64 a 32-bit float store is atomic and we don't
        // need ordering guarantees beyond "eventually visible to the UI tick".
        _targetLevel = peak;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _anim.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _anim.Stop();
        base.OnHandleDestroyed(e);
    }

    private void Tick()
    {
        float t = _active ? _targetLevel : 0f;

        // Asymmetric smoothing: snap upward (attack), drift downward (decay).
        if (t > _displayLevel) _displayLevel += (t - _displayLevel) * 0.6f;
        else                   _displayLevel += (t - _displayLevel) * 0.18f;
        if (_displayLevel < 0.001f) _displayLevel = 0;

        // Peak hold: latch the highest sample for 700 ms then decay.
        if (_displayLevel >= _holdLevel)
        {
            _holdLevel = _displayLevel;
            _holdStamp = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _holdStamp).TotalMilliseconds > 700)
        {
            _holdLevel -= 0.02f;
            if (_holdLevel < _displayLevel) _holdLevel = _displayLevel;
            if (_holdLevel < 0.001f) _holdLevel = 0;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int radius = Math.Min(Height / 2, 4);
        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(TrackColor))
        using (var pp = RoundedRect(outer, radius))
            g.FillPath(bg, pp);

        if (!_active)
        {
            using var pen = new Pen(BorderColor, 1f);
            using var pp = RoundedRect(outer, radius);
            g.DrawPath(pen, pp);
            return;
        }

        int filledW = (int)((Width - 2) * _displayLevel);
        if (filledW > 0)
        {
            var barRect = new Rectangle(1, 1, filledW, Height - 2);
            Color c = _displayLevel < 0.6f ? LowColor
                   : _displayLevel < 0.9f ? MidColor
                                          : HighColor;
            using var brush = new SolidBrush(c);
            using var pp = RoundedRect(barRect, Math.Min(barRect.Height / 2, 3));
            g.FillPath(brush, pp);
        }

        if (_holdLevel > 0.02f && _holdLevel <= 1f)
        {
            int x = (int)((Width - 2) * _holdLevel);
            if (x >= 1 && x < Width - 2)
            {
                using var pen = new Pen(HoldColor, 1.5f);
                g.DrawLine(pen, x, 1, x, Height - 2);
            }
        }

        using (var pen = new Pen(BorderColor, 1f))
        using (var pp = RoundedRect(outer, radius))
            g.DrawPath(pen, pp);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || r.Width <= 2 || r.Height <= 2)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}
