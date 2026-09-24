using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScreenLab.UI;

/// <summary>Cria o ícone do aplicativo/bandeja em tempo de execução (sem assets binários).</summary>
public static class AppIcons
{
    private static Icon? _cached;

    public static Icon Icon => _cached ??= Create();

    private static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(16, 84, 150)))
                g.FillRectangle(bg, 0, 0, 32, 32);

            using (var pen = new Pen(Color.White, 3f))
                g.DrawEllipse(pen, 6, 4, 15, 15);

            using (var dot = new SolidBrush(Color.FromArgb(255, 209, 0)))
                g.FillEllipse(dot, 10, 8, 7, 7);

            using (var handle = new Pen(Color.White, 4f))
                g.DrawLine(handle, 19, 18, 26, 25);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}