using System.IO;
using System.Text;

namespace SpotOnFly.Services;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotOnFly");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception exception) => Write("ERROR", exception.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);

                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    {
                        // Conserve un historique au lieu de supprimer le journal.
                        File.Move(LogPath, LogPath + ".1", overwrite: true);
                    }
                }
                catch
                {
                    // La rotation est best-effort : le message courant doit
                    // tout de même être écrit.
                }

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Utf8WithBom);
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application.
        }

        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
    }
}
