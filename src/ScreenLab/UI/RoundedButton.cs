using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenLab.UI;

/// <summary>
/// Botão arredondado com gradiente e efeitos de passar o mouse / pressionar.
/// Usado nos botões grandes do painel: operadores, captura e configurações.
/// O clique é diferente do Button padrão (não rouba o ESPAÇO para "espaço=botoaí"):
/// ESPAÇO continua disponível para fotografar mesmo com foco num botão.
/// </summary>
public sealed class RoundedButton : Control
{
    private bool _hover;
    private bool _pressed;

    public Color ButtonColor { get; set; } = Color.FromArgb(52, 92, 130);
    public float CornerRadius { get; set; } = 10f;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.SupportsTransparentBackColor
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Size = new Size(180, 44);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color baseColor = Enabled ? ButtonColor : Color.FromArgb(85, 85, 85);
        if (_hover && Enabled)
            baseColor = Lighten(baseColor, 0.13f);
        if (_pressed && Enabled)
            baseColor = Darken(baseColor, 0.16f);

        var rect = new RectangleF(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        using var path = RoundedRect(rect, CornerRadius);

        using (var brush = new LinearGradientBrush(rect, Lighten(baseColor, 0.16f), Darken(baseColor, 0.10f), 90f))
            g.FillPath(brush, path);

        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(Color.White, 2f);
            g.DrawPath(focusPen, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(190, 190, 190),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Lighten(Color c, float f)
    {
        return Color.FromArgb(c.A,
            (int)Math.Min(255, c.R + 255 * f),
            (int)Math.Min(255, c.G + 255 * f),
            (int)Math.Min(255, c.B + 255 * f));
    }

    private static Color Darken(Color c, float f)
    {
        return Color.FromArgb(c.A,
            (int)Math.Max(0, c.R - 255 * f),
            (int)Math.Max(0, c.G - 255 * f),
            (int)Math.Max(0, c.B - 255 * f));
    }
}