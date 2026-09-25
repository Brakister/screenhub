using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenLab.Services;

/// <summary>
/// Gerencia os usuários pré-definidos e o usuário ativo (selecionado).
/// Dispara eventos para a interface manter-se sincronizada.
/// </summary>
public class UserManager
{
    private const int MaxFaceTemplatesPerUser = 20;
    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly object _sync = new();

    public event Action? ActiveUserChanged;
    public event Action? UsersChanged;

    public UserManager(AppConfig config, string? configPath = null)
    {
        _config = config;
        _configPath = string.IsNullOrWhiteSpace(configPath) ? ConfigService.ConfigPath : configPath;
    }

    public string ConfigPath => _configPath;

    /// <summary>Cópia isolada; a UI nunca enumera a lista persistida diretamente.</summary>
    public List<string> Users => UserNamesSnapshot();

    public string ActiveUser
    {
        get
        {
            lock (_sync)
            {
                EnsureUserListNoLock();
                if (string.IsNullOrWhiteSpace(_config.ActiveUser))
                    _config.ActiveUser = _config.Users[0];
                return _config.ActiveUser;
            }
        }
        set
        {
            bool changed = false;
            lock (_sync)
            {
                EnsureUserListNoLock();
                string? canonical = FindUserNoLock(value);
                if (canonical == null || string.Equals(_config.ActiveUser, canonical, StringComparison.Ordinal))
                    return;

                string previous = _config.ActiveUser;
                _config.ActiveUser = canonical;
                if (!ConfigService.Save(_config, _configPath))
                {
                    _config.ActiveUser = previous;
                    return;
                }
                changed = true;
            }

            if (changed)
                ActiveUserChanged?.Invoke();
        }
    }

    public string ActiveUserDisplayName =>
        string.IsNullOrWhiteSpace(ActiveUser) ? "Não selecionado" : ActiveUser;

    public int FaceTemplateCount(string name)
    {
        lock (_sync)
        {
            string? canonical = FindUserNoLock(name);
            return canonical != null && _config.FaceTemplateSets.TryGetValue(canonical, out var templates)
                ? templates.Count
                : 0;
        }
    }

    /// <summary>Cópia isolada para a thread da câmera nunca enumerar o dicionário mutável.</summary>
    public Dictionary<string, List<float[]>> FaceTemplateSetsSnapshot()
    {
        lock (_sync)
        {
            var snapshot = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string user, List<float[]> templates) in _config.FaceTemplateSets)
            {
                snapshot[user] = templates?.Select(embedding => embedding.ToArray()).ToList()
                    ?? new List<float[]>();
            }
            return snapshot;
        }
    }

    public List<string> UserNamesSnapshot()
    {
        lock (_sync)
        {
            EnsureUserListNoLock();
            return _config.Users.ToList();
        }
    }

    public bool SaveConfig()
    {
        lock (_sync)
            return ConfigService.Save(_config, _configPath);
    }

    /// <summary>Substitui o cadastro de uma persona por um conjunto de poses.</summary>
    /// <returns>False quando o usuário não existe, o embedding é inválido ou a gravação falha.</returns>
    public bool SaveFaceTemplates(string name, List<float[]> embeddings)
    {
        if (embeddings == null || embeddings.Count == 0 ||
            embeddings.Any(embedding => !IsUsableEmbedding(embedding)))
            return false;

        var normalized = embeddings
            .Take(MaxFaceTemplatesPerUser)
            .Select(embedding => embedding.ToArray())
            .ToList();

        lock (_sync)
        {
            string? canonical = FindUserNoLock(name);
            if (canonical == null)
                return false;

            bool hadSets = _config.FaceTemplateSets.TryGetValue(canonical, out var previousSets);
            var oldSets = hadSets
                ? previousSets?.Select(embedding => embedding.ToArray()).ToList()
                : null;
            bool hadLegacy = _config.FaceTemplates.TryGetValue(canonical, out var previousLegacy);
            float[]? oldLegacy = hadLegacy ? previousLegacy?.ToArray() : null;

            _config.FaceEmbeddingVersion = ConfigService.CurrentFaceEmbeddingVersion;
            _config.FaceTemplateSets[canonical] = normalized;
            _config.FaceTemplates[canonical] = normalized[^1].ToArray();
            if (!ConfigService.Save(_config, _configPath))
            {
                if (hadSets && oldSets != null)
                    _config.FaceTemplateSets[canonical] = oldSets;
                else
                    _config.FaceTemplateSets.Remove(canonical);
                if (hadLegacy && oldLegacy != null)
                    _config.FaceTemplates[canonical] = oldLegacy;
                else
                    _config.FaceTemplates.Remove(canonical);
                return false;
            }
            return true;
        }
    }

    /// <summary>Acrescenta uma pose real, mantendo o limite por usuário.</summary>
    public bool AddFaceTemplate(string name, float[] embedding)
    {
        if (!IsUsableEmbedding(embedding))
            return false;

        lock (_sync)
        {
            string? canonical = FindUserNoLock(name);
            if (canonical == null)
                return false;

            bool hadTemplates = _config.FaceTemplateSets.TryGetValue(canonical, out var templates);
            var previousTemplates = hadTemplates
                ? templates?.Select(item => item.ToArray()).ToList()
                : null;
            bool hadLegacy = _config.FaceTemplates.TryGetValue(canonical, out var previousLegacy);
            float[]? oldLegacy = hadLegacy ? previousLegacy?.ToArray() : null;

            if (!hadTemplates || templates == null)
            {
                templates = new List<float[]>();
                _config.FaceTemplateSets[canonical] = templates;
            }
            templates.Add(embedding.ToArray());
            if (templates.Count > MaxFaceTemplatesPerUser)
                templates.RemoveRange(0, templates.Count - MaxFaceTemplatesPerUser);

            _config.FaceEmbeddingVersion = ConfigService.CurrentFaceEmbeddingVersion;
            _config.FaceTemplates[canonical] = templates[^1].ToArray();
            if (!ConfigService.Save(_config, _configPath))
            {
                if (hadTemplates && previousTemplates != null)
                    _config.FaceTemplateSets[canonical] = previousTemplates;
                else
                    _config.FaceTemplateSets.Remove(canonical);
                if (hadLegacy && oldLegacy != null)
                    _config.FaceTemplates[canonical] = oldLegacy;
                else
                    _config.FaceTemplates.Remove(canonical);
                return false;
            }
            return true;
        }
    }

    public bool AddUser(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return false;

        bool activeChanged = false;
        lock (_sync)
        {
            EnsureUserListNoLock();
            if (FindUserNoLock(name) != null)
                return false;

            var previousUsers = _config.Users.ToList();
            string previousActive = _config.ActiveUser;
            _config.Users.Add(name);
            if (string.IsNullOrWhiteSpace(_config.ActiveUser))
            {
                _config.ActiveUser = name;
                activeChanged = true;
            }

            if (!ConfigService.Save(_config, _configPath))
            {
                _config.Users = previousUsers;
                _config.ActiveUser = previousActive;
                return false;
            }
        }

        UsersChanged?.Invoke();
        if (activeChanged)
            ActiveUserChanged?.Invoke();
        return true;
    }

    /// <summary>Nunca remove o último usuário (evita estado inválido).</summary>
    public bool RemoveUser(string name)
    {
        bool activeChanged = false;
        lock (_sync)
        {
            EnsureUserListNoLock();
            string? canonical = FindUserNoLock(name);
            if (_config.Users.Count <= 1 || canonical == null)
                return false;

            var previousUsers = _config.Users.ToList();
            string previousActive = _config.ActiveUser;
            bool hadLegacy = _config.FaceTemplates.TryGetValue(canonical, out var oldLegacy);
            float[]? legacy = hadLegacy ? oldLegacy?.ToArray() : null;
            bool hadSets = _config.FaceTemplateSets.TryGetValue(canonical, out var oldSets);
            var sets = hadSets
                ? oldSets?.Select(embedding => embedding.ToArray()).ToList()
                : null;

            _config.Users.Remove(canonical);
            _config.FaceTemplates.Remove(canonical);
            _config.FaceTemplateSets.Remove(canonical);
            if (string.Equals(_config.ActiveUser, canonical, StringComparison.OrdinalIgnoreCase))
            {
                _config.ActiveUser = _config.Users[0];
                activeChanged = true;
            }

            if (!ConfigService.Save(_config, _configPath))
            {
                _config.Users = previousUsers;
                _config.ActiveUser = previousActive;
                if (hadLegacy && legacy != null)
                    _config.FaceTemplates[canonical] = legacy;
                if (hadSets && sets != null)
                    _config.FaceTemplateSets[canonical] = sets;
                return false;
            }
        }

        UsersChanged?.Invoke();
        if (activeChanged)
            ActiveUserChanged?.Invoke();
        return true;
    }

    private void EnsureUserListNoLock()
    {
        if (_config.Users == null || _config.Users.Count == 0)
        {
            _config.Users = new List<string> { "Operador" };
            if (string.IsNullOrWhiteSpace(_config.ActiveUser))
                _config.ActiveUser = "Operador";
        }
    }

    private string? FindUserNoLock(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        return _config.Users.FirstOrDefault(user =>
            string.Equals(user, name, StringComparison.OrdinalIgnoreCase));
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
}
