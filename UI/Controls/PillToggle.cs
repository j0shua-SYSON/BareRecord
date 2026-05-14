using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BareRecord.UI.Controls;

/// <summary>
/// Single-state on/off pill button. When checked, draws a filled accent pill
/// with a small white dot indicator. When unchecked, draws an outlined pill in
/// the muted-text colour. Replaces the WinForms checkbox glyph entirely.
/// </summary>
internal sealed partial class PillToggle : Control
{
    private bool _checked;
    private bool _hover;
    private bool _enabledLook = true;

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor   { get; set; } = Color.FromArgb(255, 60, 60, 66);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InactiveFill  { get; set; } = Color.FromArgb(255, 22, 22, 26);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverFill     { get; set; } = Color.FromArgb(255, 30, 30, 36);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor   { get; set; } = Color.FromArgb(255, 220, 38, 38);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InactiveText  { get; set; } = Color.FromArgb(255, 210, 210, 215);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ActiveText    { get; set; } = Color.White;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledText  { get; set; } = Color.FromArgb(255, 110, 110, 115);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int   CornerRadius  { get; set; } = 22;

    public PillToggle()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 44;
        Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new bool Enabled
    {
        get => base.Enabled;
        set
        {
            base.Enabled = value;
            _enabledLook = value;
            Cursor = value ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (_enabledLook && !_hover) { _hover = true; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_enabledLook && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = !_enabledLook
            ? InactiveFill
            : _checked ? AccentColor : (_hover ? HoverFill : InactiveFill);

        using (var bg = new SolidBrush(fill))
        using (var pp = RoundedRect(outer, CornerRadius))
            g.FillPath(bg, pp);

        if (!_checked)
        {
            using var border = new Pen(BorderColor, 1.2f);
            using var pp = RoundedRect(outer, CornerRadius);
            g.DrawPath(border, pp);
        }

        const int dotPad = 14;
        const int dotSize = 14;
        int dy = (Height - dotSize) / 2;

        if (_enabledLook)
        {
            using var dot = new SolidBrush(_checked ? Color.White : Color.FromArgb(255, 90, 90, 96));
            g.FillEllipse(dot, new Rectangle(dotPad, dy, dotSize, dotSize));
        }
        else
        {
            using var dot = new SolidBrush(Color.FromArgb(255, 70, 70, 76));
            g.FillEllipse(dot, new Rectangle(dotPad, dy, dotSize, dotSize));
        }

        Color text = !_enabledLook
            ? DisabledText
            : (_checked ? ActiveText : InactiveText);
        var textRect = new Rectangle(dotPad + dotSize + 10, 0, Width - (dotPad + dotSize + 10) - 14, Height);
        TextRenderer.DrawText(
            g, Text, Font, textRect, text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = radius * 2;
        if (d > r.Width)  d = r.Width;
        if (d > r.Height) d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
