using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenLab.UI;

/// <summary>
/// Luz de status (estilo LED) ao lado da pré-visualização:
/// - Vermelho: pronto, não está tirando foto
/// - Verde: captura em andamento (tirando a foto)
/// </summary>
public sealed class LedControl : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private Color _idleColor = Color.FromArgb(230, 45, 45);
    private Color _activeColor = Color.FromArgb(0, 220, 80);
    private int _flashTicks;
    private bool _blinkError;
    private bool _blinkPhase;

    public LedControl()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor
                 | ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Size = new Size(22, 22);

        _timer.Tick += (s, e) =>
        {
            if (_flashTicks > 0)
            {
                _flashTicks--;
                Invalidate();
            }
            if (_blinkError)
            {
                _blinkPhase = !_blinkPhase;
                Invalidate();
            }
        };
        _timer.Start();
    }

    /// <summary>Estado estável do LED (ex.: verde em execução, âmbar pausado).</summary>
    public void SetIdle(Color color)
    {
        _idleColor = color;
        _blinkError = false;
        _flashTicks = 0;
        Invalidate();
    }

    /// <summary>Flash brilhante — usado no acionamento (verde = foto capturada).</summary>
    public void Flash(Color color, int durationMs = 1500)
    {
        _activeColor = color;
        _flashTicks = Math.Max(1, durationMs / 100);
        Invalidate();
    }

    /// <summary>Estado de erro: vermelho piscando até SetIdle() ser chamado.</summary>
    public void SetError()
    {
        _blinkError = true;
        _blinkPhase = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool flashing = _flashTicks > 0 && (_flashTicks / 2) % 2 == 0;
        Color color;
        if (_blinkError)
            color = _blinkPhase ? Color.FromArgb(255, 60, 60) : Color.FromArgb(110, 20, 20);
        else if (flashing)
            color = _activeColor;
        else
            color = _idleColor;

        var rect = new Rectangle(1, 1, Width - 3, Height - 3);

        // Brilho ao redor do LED
        using (var glow = new SolidBrush(Color.FromArgb(flashing || _blinkError ? 95 : 30, color)))
            g.FillEllipse(glow, rect);

        rect.Inflate(-3, -3);

        // Anel escuro ao redor
        using (var ring = new Pen(Color.FromArgb(70, Color.Black), 2f))
            g.DrawEllipse(ring, rect);

        // Corpo do LED
        using (var fill = new SolidBrush(color))
            g.FillEllipse(fill, rect);

        // Reflexo para dar efeito de vidro
        using (var shine = new SolidBrush(Color.FromArgb(110, Color.White)))
        {
            var hi = new Rectangle(
                rect.X + rect.Width / 3,
                rect.Y + rect.Height / 5,
                rect.Width / 3,
                rect.Height / 4);
            g.FillEllipse(shine, hi);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}