using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BareRecord.UI;

/// <summary>
/// Full-screen click-through overlay that counts down 3-2-1 before recording.
/// Magenta is set as both BackColor and TransparencyKey so everything outside
/// the centered pill is keyed out. The pill itself is drawn with fully opaque
/// colors — semi-transparent fills would blend with the magenta background
/// and render as dark purple instead of solid black.
/// </summary>
internal sealed partial class CountdownOverlay : Form
{
    private static readonly Color KeyColor   = Color.Magenta;
    private static readonly Color PillFill   = Color.FromArgb(255, 18, 18, 18);
    private static readonly Color RingColor  = Color.FromArgb(255, 220, 38, 38);
    private const int PillRadius = 140;

    private int _value = 3;
    private readonly TaskCompletionSource _done = new();
    private System.Windows.Forms.Timer? _timer;

    private CountdownOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        Bounds          = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 800, 600);
        BackColor       = KeyColor;
        TransparencyKey = KeyColor;
        DoubleBuffered  = true;
        SetStyle(ControlStyles.Selectable, false);
    }

    /// <summary>Shows the overlay for 3 seconds (3, 2, 1) and then closes.</summary>
    public static Task RunAsync()
    {
        var ov = new CountdownOverlay();
        ov.Show();
        ov.StartTimer();
        return ov._done.Task;
    }

    private void StartTimer()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (s, e) =>
        {
            if (--_value <= 0) Close();
            else Invalidate();
        };
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Make sure the awaiter completes whether we closed naturally (timer
        // hit 0) or the form was destroyed externally — leaking a TCS would
        // hang StartAsync indefinitely.
        if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
        _done.TrySetResult();
        base.OnFormClosed(e);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        int cx = Width / 2, cy = Height / 2;
        var pill = new Rectangle(cx - PillRadius, cy - PillRadius, PillRadius * 2, PillRadius * 2);

        using (var bg = new SolidBrush(PillFill))
            g.FillEllipse(bg, pill);
        using (var ringPen = new Pen(RingColor, 6f))
            g.DrawEllipse(ringPen, pill);

        using var font = new Font("Segoe UI", 96f, FontStyle.Bold);
        using var fg   = new SolidBrush(Color.White);
        var s  = _value.ToString();
        var sz = g.MeasureString(s, font);
        g.DrawString(s, font, fg, cx - sz.Width / 2, cy - sz.Height / 2);
    }
}
