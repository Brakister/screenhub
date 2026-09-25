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
    // Mantido para ler cadastros antigos de uma única amostra.
    public Dictionary<string, float[]> FaceTemplates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<float[]>> FaceTemplateSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Formato dos embeddings. Versões diferentes usam uma galeria incompatível
    /// e são descartadas na carga para obrigar um cadastro seguro novamente.
    /// </summary>
    public int FaceEmbeddingVersion { get; set; }

    public int CameraIndex { get; set; } = 0;
    public int FaceCameraIndex { get; set; } = 1;
    public int VideoWidth { get; set; } = 1920;
    public int VideoHeight { get; set; } = 1080;
    public int FaceVideoWidth { get; set; } = 1280;
    public int FaceVideoHeight { get; set; } = 720;

    public bool PalmEnabled { get; set; } = false;
    public int PalmMinAreaPct { get; set; } = 3;
    public bool FaceEnabled { get; set; } = false;
    public bool IntervalEnabled { get; set; } = false;

    public int IntervalSeconds { get; set; } = 300;
    public int FaceIntervalMs { get; set; } = 400;
    public int CooldownSeconds { get; set; } = 5;

    // --- Robustez do reconhecimento facial ---
    /// <summary>Segundos que o rosto precisa ficar visível antes de trocar o usuário ativo.</summary>
    public int FaceConfirmSeconds { get; set; } = 2;
    /// <summary>
    /// Segundos de espera depois que o rosto sai do quadro antes de aceitar
    /// outro usuário. Evita ficar trocando de pessoa no meio de uma conversa.
    /// </summary>
    public int FaceSwitchGraceSeconds { get; set; } = 8;
    /// <summary>Largura mínima do rosto, em % da imagem, para valer como identidade.</summary>
    public int FaceMinWidthPct { get; set; } = 8;
    /// <summary>Nitidez mínima (variância do Laplaciano) para o rosto contar.</summary>
    public int FaceMinSharpness { get; set; } = 60;

    // --- Ritmo de captura por rosto ---
    /// <summary>Captura só depois que o mesmo rosto aparece por estes segundos.</summary>
    public int FaceCaptureDwellSeconds { get; set; } = 3;
    /// <summary>Pausa mínima entre duas fotos da mesma pessoa.</summary>
    public int FaceCaptureCooldownSeconds { get; set; } = 300;
    /// <summary>Só dispara captura por rosto quando a pessoa foi reconhecida com segurança.</summary>
    public bool FaceCaptureRequireKnown { get; set; } = true;

    public string OutputFolder { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
}

public static class ConfigService
{
    public const string AppName = "ScreenLab";
    public const int CurrentFaceEmbeddingVersion = 2;

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

    /// <summary>
    /// Carrega a configuração. O caminho explícito existe para probes e testes
    /// isolados; o aplicativo usa sempre <see cref="ConfigPath"/>.
    /// </summary>
    public static AppConfig Load(string? configPath = null)
    {
        string path = string.IsNullOrWhiteSpace(configPath) ? ConfigPath : configPath;

        lock (Sync)
        {
            bool fileExists = File.Exists(path);
            AppConfig cfg;

            if (!fileExists)
            {
                cfg = CreateDefault();
            }
            else
            {
                try
                {
                    string text = File.ReadAllText(path);
                    cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts)
                        ?? throw new JsonException("config.json contém JSON vazio.");
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    LoggerService.Error("Configuração facial inválida; usando valores padrão", ex);
                    cfg = CreateDefault();

                    // Nunca substitui silenciosamente um arquivo que ainda pode
                    // ser recuperado. Só grava o novo padrão depois de preservado.
                    string? backupPath = PreserveInvalidConfig(path);
                    if (backupPath != null)
                    {
                        LoggerService.Warn($"Configuração anterior preservada em: {backupPath}");
                        if (!SaveLocked(cfg, path))
                            LoggerService.Warn("Não foi possível gravar a configuração padrão restaurada.");
                    }
                    else
                    {
                        LoggerService.Error(
                            "A configuração inválida não pôde ser preservada; o arquivo original foi mantido.");
                    }
                    return cfg;
                }
                catch (Exception ex)
                {
                    LoggerService.Error("Falha ao ler a configuração (usando padrão sem salvar)", ex);
                    return CreateDefault();
                }
            }

            bool saveRequired = !fileExists;
            if (NormalizeConfig(cfg))
                saveRequired = true;
            if (saveRequired && !SaveLocked(cfg, path))
                LoggerService.Warn("A configuração foi carregada, mas não pôde ser persistida.");

            return cfg;
        }
    }

    /// <summary>Salva de forma atômica e informa se a persistência foi concluída.</summary>
    public static bool Save(AppConfig cfg, string? configPath = null)
    {
        string path = string.IsNullOrWhiteSpace(configPath) ? ConfigPath : configPath;
        lock (Sync)
            return SaveLocked(cfg, path);
    }

    private static AppConfig CreateDefault() => new()
    {
        OutputFolder = DefaultOutputFolder,
        ActiveUser = "Operador",
        FaceEmbeddingVersion = CurrentFaceEmbeddingVersion,
    };

    private static bool NormalizeConfig(AppConfig cfg)
    {
        bool changed = false;

        var users = new List<string>();
        var knownUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? rawName in cfg.Users ?? new List<string>())
        {
            string name = rawName?.Trim() ?? "";
            if (name.Length == 0 || !knownUsers.Add(name))
                continue;
            users.Add(name);
        }
        if (users.Count == 0)
        {
            users.Add("Operador");
            knownUsers.Add("Operador");
            changed = true;
        }
        if (cfg.Users == null || !cfg.Users.SequenceEqual(users, StringComparer.Ordinal))
        {
            cfg.Users = users;
            changed = true;
        }
        else
        {
            cfg.Users = users;
        }

        string active = FindCanonicalUser(users, cfg.ActiveUser) ?? users[0];
        if (!string.Equals(cfg.ActiveUser, active, StringComparison.Ordinal))
        {
            cfg.ActiveUser = active;
            changed = true;
        }

        if (cfg.FaceEmbeddingVersion != CurrentFaceEmbeddingVersion)
        {
            bool hadTemplates = (cfg.FaceTemplates?.Count ?? 0) > 0 ||
                (cfg.FaceTemplateSets?.Count ?? 0) > 0;
            cfg.FaceTemplates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            cfg.FaceTemplateSets = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
            cfg.FaceEmbeddingVersion = CurrentFaceEmbeddingVersion;
            changed = true;
            if (hadTemplates)
            {
                LoggerService.Warn(
                    "Galeria facial incompatível descartada. Faça o cadastro das 5 poses novamente.");
            }
        }
        else if (SanitizeFaceTemplates(cfg, users))
        {
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.OutputFolder))
        {
            cfg.OutputFolder = DefaultOutputFolder;
            changed = true;
        }

        changed |= ClampProperty(() => cfg.CameraIndex, value => cfg.CameraIndex = value, 0, 9);
        changed |= ClampProperty(() => cfg.FaceCameraIndex, value => cfg.FaceCameraIndex = value, 0, 9);
        changed |= ClampProperty(() => cfg.VideoWidth, value => cfg.VideoWidth = value, 320, 7680);
        changed |= ClampProperty(() => cfg.VideoHeight, value => cfg.VideoHeight = value, 240, 4320);
        changed |= ClampProperty(() => cfg.FaceVideoWidth, value => cfg.FaceVideoWidth = value, 320, 1920);
        changed |= ClampProperty(() => cfg.FaceVideoHeight, value => cfg.FaceVideoHeight = value, 240, 1080);
        changed |= ClampProperty(() => cfg.PalmMinAreaPct, value => cfg.PalmMinAreaPct = value, 1, 100);
        changed |= ClampProperty(() => cfg.IntervalSeconds, value => cfg.IntervalSeconds = value, 1, 86400);
        changed |= ClampProperty(() => cfg.FaceIntervalMs, value => cfg.FaceIntervalMs = value, 100, 5000);
        changed |= ClampProperty(() => cfg.CooldownSeconds, value => cfg.CooldownSeconds = value, 0, 3600);
        changed |= ClampProperty(() => cfg.FaceConfirmSeconds, value => cfg.FaceConfirmSeconds = value, 0, 60);
        changed |= ClampProperty(() => cfg.FaceSwitchGraceSeconds, value => cfg.FaceSwitchGraceSeconds = value, 0, 600);
        changed |= ClampProperty(() => cfg.FaceMinWidthPct, value => cfg.FaceMinWidthPct = value, 1, 100);
        changed |= ClampProperty(() => cfg.FaceMinSharpness, value => cfg.FaceMinSharpness = value, 0, 5000);
        changed |= ClampProperty(() => cfg.FaceCaptureDwellSeconds, value => cfg.FaceCaptureDwellSeconds = value, 0, 600);
        changed |= ClampProperty(() => cfg.FaceCaptureCooldownSeconds, value => cfg.FaceCaptureCooldownSeconds = value, 0, 86400);

        return changed;
    }

    private static bool SanitizeFaceTemplates(AppConfig cfg, List<string> users)
    {
        bool changed = false;
        var legacy = NormalizeLegacyTemplates(cfg.FaceTemplates, users, ref changed);
        var sets = NormalizeTemplateSets(cfg.FaceTemplateSets, users, ref changed);
        cfg.FaceTemplates = legacy;
        cfg.FaceTemplateSets = sets;

        // O último conjunto válido é a amostra legada. Assim, arquivos v2 antigos
        // que ainda só tinham FaceTemplates continuam compatíveis.
        foreach ((string user, List<float[]> templates) in sets)
        {
            if (!legacy.TryGetValue(user, out float[]? current) ||
                !current.AsSpan().SequenceEqual(templates[^1]))
            {
                legacy[user] = templates[^1].ToArray();
                changed = true;
            }
        }

        foreach ((string user, float[] template) in legacy.ToList())
        {
            if (sets.ContainsKey(user))
                continue;
            sets[user] = new List<float[]> { template.ToArray() };
            changed = true;
        }

        return changed;
    }

    private static Dictionary<string, float[]> NormalizeLegacyTemplates(
        Dictionary<string, float[]>? source, List<string> users, ref bool changed)
    {
        var normalized = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawName, float[]? embedding) in source ??
            Enumerable.Empty<KeyValuePair<string, float[]>>())
        {
            string? user = FindCanonicalUser(users, rawName);
            if (user == null || !IsUsableEmbedding(embedding) || normalized.ContainsKey(user))
            {
                changed = true;
                continue;
            }
            normalized[user] = embedding!.ToArray();
            if (!string.Equals(rawName, user, StringComparison.Ordinal))
                changed = true;
        }
        return normalized;
    }

    private static Dictionary<string, List<float[]>> NormalizeTemplateSets(
        Dictionary<string, List<float[]>>? source, List<string> users, ref bool changed)
    {
        var normalized = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawName, List<float[]>? templates) in source ??
            Enumerable.Empty<KeyValuePair<string, List<float[]>>>())
        {
            string? user = FindCanonicalUser(users, rawName);
            if (user == null)
            {
                changed = true;
                continue;
            }
            if (!string.Equals(rawName, user, StringComparison.Ordinal))
                changed = true;

            if (!normalized.TryGetValue(user, out List<float[]>? valid))
            {
                valid = new List<float[]>();
                normalized[user] = valid;
            }

            int sourceCount = templates?.Count ?? 0;
            foreach (float[] embedding in templates ?? Enumerable.Empty<float[]>())
            {
                if (!IsUsableEmbedding(embedding))
                {
                    changed = true;
                    continue;
                }
                if (valid.Count < 12)
                    valid.Add(embedding.ToArray());
                else
                    changed = true;
            }

            if (sourceCount > 12 || templates == null)
                changed = true;
            if (valid.Count == 0)
            {
                normalized.Remove(user);
                changed = true;
            }
        }
        return normalized;
    }

    private static string? FindCanonicalUser(List<string> users, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;
        string name = requested.Trim();
        return users.FirstOrDefault(user => string.Equals(user, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsableEmbedding(float[]? embedding)
    {
        if (embedding is not { Length: 128 })
            return false;

        double sumSquares = 0;
        foreach (float value in embedding)
        {
            if (!float.IsFinite(value))
                return false;
            sumSquares += value * (double)value;
        }
        return sumSquares > 1e-6;
    }

    private static bool Clamp(ref int value, int minimum, int maximum)
    {
        int clamped = Math.Clamp(value, minimum, maximum);
        if (clamped == value)
            return false;
        value = clamped;
        return true;
    }

    private static bool ClampProperty(Func<int> getter, Action<int> setter, int minimum, int maximum)
    {
        int value = getter();
        bool changed = Clamp(ref value, minimum, maximum);
        setter(value);
        return changed;
    }

    private static string? PreserveInvalidConfig(string path)
    {
        try
        {
            string folder = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string backup = Path.Combine(
                folder,
                $"{name}.invalid-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
            File.Copy(path, backup, overwrite: false);
            return backup;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao preservar a configuração inválida", ex);
            return null;
        }
    }

    private static bool SaveLocked(AppConfig cfg, string configPath)
    {
        string? temporaryPath = null;
        try
        {
            string? folder = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            string json = JsonSerializer.Serialize(cfg, JsonOpts);
            temporaryPath = configPath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, configPath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao salvar a configuração", ex);
            return false;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // O arquivo temporário já não participa da configuração ativa.
                }
            }
        }
    }
}
