using System;
using System.Collections.Generic;

namespace ScreenLab.Services;

/// <summary>
/// Gerencia os usuários pré-definidos e o usuário ativo (selecionado).
/// Dispara eventos para a interface manter-se sincronizada.
/// </summary>
public class UserManager
{
    private readonly AppConfig _config;

    public event Action? ActiveUserChanged;
    public event Action? UsersChanged;

    public UserManager(AppConfig config) => _config = config;

    public List<string> Users
    {
        get
        {
            if (_config.Users.Count == 0)
                _config.Users.Add("Operador");
            return _config.Users;
        }
    }

    public string ActiveUser
    {
        get
        {
            if (string.IsNullOrEmpty(_config.ActiveUser) && Users.Count > 0)
                _config.ActiveUser = Users[0];
            return _config.ActiveUser;
        }
        set
        {
            if (value != null && Users.Contains(value) && _config.ActiveUser != value)
            {
                _config.ActiveUser = value;
                ConfigService.Save(_config);
                ActiveUserChanged?.Invoke();
            }
        }
    }

    public string ActiveUserDisplayName =>
        string.IsNullOrWhiteSpace(ActiveUser) ? "Não selecionado" : ActiveUser;

    public void AddUser(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || Users.Contains(name))
            return;

        Users.Add(name);
        ConfigService.Save(_config);
        UsersChanged?.Invoke();

        if (Users.Count == 1 || string.IsNullOrEmpty(_config.ActiveUser))
            ActiveUser = name;
    }

    /// <summary>Nunca remove o último usuário (evita estado inválido).</summary>
    public bool RemoveUser(string name)
    {
        if (Users.Count <= 1 || !Users.Contains(name))
            return false;

        Users.Remove(name);
        if (_config.ActiveUser == name)
            _config.ActiveUser = Users[0];

        ConfigService.Save(_config);
        UsersChanged?.Invoke();
        ActiveUserChanged?.Invoke();
        return true;
    }
}