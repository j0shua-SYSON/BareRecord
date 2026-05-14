using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BareRecord.UI.Controls;

/// <summary>
/// Two-or-more-option segmented control with rounded outer frame. The selected
/// segment is filled in the accent colour; unselected segments are flat dark
/// with a subtle hover highlight. No radio-button "circle" glyphs.
/// </summary>
internal sealed partial class SegmentedControl : Control
{
    private readonly List<string> _items = new();
    private int _selectedIndex = 0;
    private int _hoverIndex = -1;
    private bool _enabledLook = true;

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FrameColor    { get; set; } = Color.FromArgb(255, 38, 38, 42);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InactiveColor { get; set; } = Color.FromArgb(255, 24, 24, 28);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor    { get; set; } = Color.FromArgb(255, 46, 46, 52);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedColor { get; set; } = Color.FromArgb(255, 220, 38, 38);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedText  { get; set; } = Color.White;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color InactiveText  { get; set; } = Color.FromArgb(255, 200, 200, 205);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledText  { get; set; } = Color.FromArgb(255, 110, 110, 115);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int   CornerRadius  { get; set; } = 10;

    public SegmentedControl()
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
    public IList<string> Items => _items;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selectedIndex) return;
            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
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

    public void SetItems(params string[] items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_selectedIndex >= _items.Count) _selectedIndex = 0;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = HitTest(e.X);
        if (idx != _hoverIndex) { _hoverIndex = idx; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_enabledLook || e.Button != MouseButtons.Left) return;
        int idx = HitTest(e.X);
        if (idx >= 0) SelectedIndex = idx;
    }

    private int HitTest(int x)
    {
        if (_items.Count == 0) return -1;
        int seg = Width / _items.Count;
        if (seg <= 0) return -1;
        int idx = x / seg;
        if (idx < 0) idx = 0;
        if (idx >= _items.Count) idx = _items.Count - 1;
        return idx;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(InactiveColor))
        using (var pp = RoundedRect(outer, CornerRadius))
            g.FillPath(bg, pp);

        if (_items.Count == 0) return;

        float segW = (float)Width / _items.Count;
        for (int i = 0; i < _items.Count; i++)
        {
            var rect = new RectangleF(i * segW, 0, segW, Height);

            if (i == _selectedIndex && _enabledLook)
            {
                using var inner = RoundedRect(Rectangle.Round(InsetF(rect, 3)), CornerRadius - 3);
                using var fill  = new SolidBrush(SelectedColor);
                g.FillPath(fill, inner);
            }
            else if (i == _hoverIndex && _enabledLook && i != _selectedIndex)
            {
                using var inner = RoundedRect(Rectangle.Round(InsetF(rect, 3)), CornerRadius - 3);
                using var fill  = new SolidBrush(HoverColor);
                g.FillPath(fill, inner);
            }

            var textColor = !_enabledLook
                ? DisabledText
                : (i == _selectedIndex ? SelectedText : InactiveText);

            TextRenderer.DrawText(
                g, _items[i], Font, Rectangle.Round(rect), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        using (var border = new Pen(FrameColor, 1f))
        using (var pp = RoundedRect(outer, CornerRadius))
            g.DrawPath(border, pp);
    }

    private static RectangleF InsetF(RectangleF r, float n) =>
        new(r.X + n, r.Y + n, r.Width - 2 * n, r.Height - 2 * n);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
