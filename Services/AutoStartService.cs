using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SpotOnFly.Services;

internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SpotOnFly";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex);
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                key.SetValue(ValueName, BuildLaunchCommand());
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    private static string BuildLaunchCommand()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Chemin du processus introuvable.");

        // En développement (dotnet run), le processus est dotnet.exe : on enregistre
        // la commande complète pour lancer l'assembly managée.
        if (string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (!string.IsNullOrEmpty(assemblyName))
            {
                var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
                return $"\"{exePath}\" \"{assemblyPath}\"";
            }
        }

        return $"\"{exePath}\"";
    }
}
