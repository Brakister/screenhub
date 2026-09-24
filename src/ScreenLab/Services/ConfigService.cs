using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenLab.Services;

/// <summary>Configuração persistida do sistema (salva em %APPDATA%\ScreenLab\config.json).</summary>
public class AppConfig
{
    public List<string> Users { get; set; } = new() { "Operador" };
    public string ActiveUser { get; set; } = "";

    public int CameraIndex { get; set; } = 0;
    public int VideoWidth { get; set; } = 1280;
    public int VideoHeight { get; set; } = 720;

    public bool PalmEnabled { get; set; } = false;   // manual-only: foto só por botão/espaço
    public int PalmMinAreaPct { get; set; } = 3;     // % da imagem ocupada pela pele p/ disparar
    public bool FaceEnabled { get; set; } = false;
    public bool IntervalEnabled { get; set; } = false;

    public int IntervalSeconds { get; set; } = 300;
    public int FaceIntervalMs { get; set; } = 400;     // intervalo entre varreduras de rosto
    public int CooldownSeconds { get; set; } = 5;      // pausa mínima entre fotos

    public string OutputFolder { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
}

public static class ConfigService
{
    public const string AppName = "ScreenLab";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string ConfigPath => Path.Combine(AppDataFolder, "config.json");

    public static string DefaultOutputFolder
    {
        get
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pics))
                pics = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(pics, AppName);
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string text = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts);
                if (cfg != null)
                {
                    if (cfg.Users == null || cfg.Users.Count == 0)
                        cfg.Users = new List<string> { "Operador" };
                    if (string.IsNullOrEmpty(cfg.OutputFolder))
                        cfg.OutputFolder = DefaultOutputFolder;
                    if (cfg.VideoWidth <= 0 || cfg.VideoHeight <= 0)
                    {
                        cfg.VideoWidth = 1280;
                        cfg.VideoHeight = 720;
                    }
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao ler a configuração (usando padrão)", ex);
        }

        var fallback = new AppConfig { OutputFolder = DefaultOutputFolder, ActiveUser = "Operador" };
        Save(fallback);
        return fallback;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppDataFolder);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao salvar a configuração", ex);
        }
    }
}