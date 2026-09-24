using System;

namespace ScreenLab.Capture;

/// <summary>Representa uma foto capturada e salva pelo sistema.</summary>
public sealed record CapturedPhoto(string FilePath, string User, DateTime Timestamp, string Trigger)
{
    public string FileName => Path.GetFileName(FilePath);
}