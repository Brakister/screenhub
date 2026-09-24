using System;
using System.Threading;
using System.Windows.Forms;
using ScreenLab.Services;
using ScreenLab.UI;

namespace ScreenLab;

internal static class Program
{
    private const string InstanceMutexName = "ScreenLab_Instance_8F3A2C9D";
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        // Garante que apenas uma instância do ScreenLab rode ao mesmo tempo (importante para 24/7).
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "O ScreenLab já está em execução. Verifique a bandeja do sistema.",
                "ScreenLab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Captura erros inesperados para o log, sem derrubar o sistema silenciosamente.
        Application.ThreadException += (s, e) =>
            LoggerService.Error("Erro não tratado na interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LoggerService.Error("Erro fatal", e.ExceptionObject as Exception);

        bool startMinimized = Array.IndexOf(args, "--minimized") >= 0;

        try
        {
            Application.Run(new MainForm(startMinimized));
        }
        catch (Exception ex)
        {
            LoggerService.Error("Erro ao iniciar o ScreenLab", ex);
            MessageBox.Show($"Erro ao iniciar o ScreenLab:\n{ex.Message}",
                "ScreenLab", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* ignorado */ }
            _instanceMutex.Dispose();
        }
    }
}