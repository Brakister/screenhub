using System;
using System.Globalization;
using System.Text;
using OpenCvSharp;
using Point = OpenCvSharp.Point;

namespace ScreenLab.Capture;

/// <summary>
/// Aplica o carimbo com data/hora + nome do usuário sobre a imagem
/// (faixa semitransparente no canto superior esquerdo).
/// </summary>
public static class OverlayRenderer
{
    public static Mat Annotate(Mat frame, DateTime timestamp, string user, string trigger)
    {
        var result = frame.Clone();
        DrawHeader(result, timestamp, user, trigger);
        return result;
    }

    private static void DrawHeader(Mat image, DateTime ts, string user, string trigger)
    {
        int boxW = Math.Min(Math.Max(image.Width - 16, 200), 620);
        const int boxH = 100;

        // Faixa semitransparente para legibilidade.
        using (var overlay = image.Clone())
        {
            Cv2.Rectangle(overlay, new Rect(8, 8, boxW, boxH), new Scalar(0, 0, 0), -1);
            Cv2.AddWeighted(overlay, 0.55, image, 0.45, 0, image);
        }

        const double fontSize = 0.9;
        var yellow = new Scalar(0, 255, 255);

        Cv2.PutText(image, $"{ts:dd/MM/yyyy HH:mm:ss}", new Point(18, 38),
            HersheyFonts.HersheySimplex, fontSize, yellow, 2, LineTypes.AntiAlias);

        string userText = string.IsNullOrEmpty(user) ? "Nao selecionado" : user;
        // A fonte Hershey do OpenCV é ASCII pura (sem glifos de acento) — qualquer
        // "ã/á/ç" vira "?" no carimbo. Reduzimos os acentos para a forma ASCII
        // ("Usuário: João" → "Usuario: Joao") pra foto sair legível e sem "?".
        userText = RemoveAccents(userText);

        // O disparo MANUAL (espaço/F9) não aparece no carimbo — não acrescenta
        // informação e só polui. Movimento/câmera continuam marcados: aí o operador
        // precisa saber por que a foto saiu.
        if (!string.IsNullOrEmpty(trigger) &&
            !string.Equals(trigger, "Manual", StringComparison.OrdinalIgnoreCase))
            userText += $"   [{RemoveAccents(trigger)}]";

        Cv2.PutText(image, $"Usuario: {userText}", new Point(18, 78),
            HersheyFonts.HersheySimplex, fontSize, yellow, 2, LineTypes.AntiAlias);
    }

    /// <summary>
    /// Remove diacríticos (ã, á, ç, é...) deixando só a base ASCII, já que a fonte
    /// Hershey usada no carimbo não tem glifos de acento.
    /// </summary>
    private static string RemoveAccents(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}