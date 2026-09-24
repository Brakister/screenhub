using System;
using Microsoft.Win32;

namespace ScreenLab.Services;

/// <summary>
/// Controla a inicialização automática com o Windows (chave Run do Registro,
/// sem precisar de admin). Usado para o sistema funcionar 24/7 após login.
/// </summary>
public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenLab";
    private const string RunArgs = ""; // janela visível para os operadores acompanharem

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? value = key?.GetValue(ValueName) as string;
            return value != null && value.Contains("ScreenLab", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao consultar inicialização com o Windows", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? AppContext.BaseDirectory + "ScreenLab.exe";
                key.SetValue(ValueName, $"\"{exe}\"{RunArgs}");
                LoggerService.Info($"Inicialização com o Windows habilitada: {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                LoggerService.Info("Inicialização com o Windows desabilitada");
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao configurar inicialização com o Windows", ex);
        }
    }
}