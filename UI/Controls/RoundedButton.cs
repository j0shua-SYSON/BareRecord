using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BareRecord.UI.Controls;

/// <summary>
/// Flat, rounded-corner action button. Used in place of the default
/// <see cref="Button"/> so the chrome stays consistent with the segmented
/// and pill controls.
///
/// Properties of note: <see cref="Accent"/> is the primary fill colour;
/// <see cref="Variant"/> picks between filled and outline styles.
/// </summary>
internal sealed partial class RoundedButton : Button
{
    public enum ButtonVariant { Filled, Outline, Subtle, Ghost }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent       { get; set; } = Color.FromArgb(255, 220, 38, 38);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color OutlineColor { get; set; } = Color.FromArgb(255, 60, 60, 66);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SubtleFill   { get; set; } = Color.FromArgb(255, 32, 32, 38);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SubtleHover  { get; set; } = Color.FromArgb(255, 42, 42, 50);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int   CornerRadius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonVariant Variant { get; set; } = ButtonVariant.Filled;

    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleCenter;
        UseCompatibleTextRendering = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill, border, text;
        switch (Variant)
        {
            case ButtonVariant.Outline:
                fill   = _hover && Enabled ? Color.FromArgb(255, 30, 30, 36) : Color.FromArgb(0, 0, 0, 0);
                border = OutlineColor;
                text   = Enabled ? Color.White : Color.FromArgb(255, 110, 110, 115);
                break;
            case ButtonVariant.Subtle:
                fill   = _hover && Enabled ? SubtleHover : SubtleFill;
                border = OutlineColor;
                text   = Enabled ? Color.White : Color.FromArgb(255, 110, 110, 115);
                break;
            case ButtonVariant.Ghost:
                fill   = _hover && Enabled ? Color.FromArgb(255, 28, 28, 34) : Color.FromArgb(0, 0, 0, 0);
                border = Color.FromArgb(0, 0, 0, 0);
                text   = Enabled ? Color.FromArgb(255, 200, 200, 205) : Color.FromArgb(255, 110, 110, 115);
                break;
            case ButtonVariant.Filled:
            default:
                fill   = Enabled
                    ? (_pressed ? Darken(Accent, 0.15f) : (_hover ? Lighten(Accent, 0.08f) : Accent))
                    : Color.FromArgb(255, 55, 55, 60);
                border = fill;
                text   = Enabled ? Color.White : Color.FromArgb(255, 150, 150, 155);
                break;
        }

        if (fill.A > 0)
        {
            using var bg = new SolidBrush(fill);
            using var pp = RoundedRect(r, CornerRadius);
            g.FillPath(bg, pp);
        }
        if (border.A > 0 && Variant != ButtonVariant.Filled)
        {
            using var pen = new Pen(border, 1.2f);
            using var pp = RoundedRect(r, CornerRadius);
            g.DrawPath(pen, pp);
        }

        TextRenderer.DrawText(
            g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static Color Lighten(Color c, float amt)
    {
        int r = (int)(c.R + (255 - c.R) * amt);
        int g = (int)(c.G + (255 - c.G) * amt);
        int b = (int)(c.B + (255 - c.B) * amt);
        return Color.FromArgb(c.A, Clamp(r), Clamp(g), Clamp(b));
    }
    private static Color Darken(Color c, float amt)
    {
        int r = (int)(c.R * (1 - amt));
        int g = (int)(c.G * (1 - amt));
        int b = (int)(c.B * (1 - amt));
        return Color.FromArgb(c.A, Clamp(r), Clamp(g), Clamp(b));
    }
    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

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
}
