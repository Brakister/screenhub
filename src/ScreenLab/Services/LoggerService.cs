using System;
using System.IO;

namespace ScreenLab.Services;

/// <summary>
/// Registro de eventos em arquivo (um arquivo por dia), protegido contra falhas.
/// </summary>
public static class LoggerService
{
    private static readonly object Sync = new();
    private static readonly string LogFolder =
        Path.Combine(ConfigService.AppDataFolder, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("AVISO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERRO", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogFolder);
                string file = Path.Combine(LogFolder, $"ScreenLab_{DateTime.Now:yyyyMMdd}.log");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Nunca deixe uma falha de log derrubar o sistema.
        }
    }
}