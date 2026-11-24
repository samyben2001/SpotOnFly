using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpotOnFly.Models;

namespace SpotOnFly.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            // Tolère une valeur inconnue dans le fichier sans perdre tous les
            // autres réglages (les conversions d'enum standard lèvent une exception).
            new TolerantEnumConverter<OverlayPosition>(OverlayPosition.BottomCenter),
            new TolerantEnumConverter<OverlaySize>(OverlaySize.Medium)
        }
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotOnFly",
        "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            Settings = Deserialize(json);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    internal static AppSettings Deserialize(string json)
        => Sanitize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);

            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Settings);
        Settings = Sanitize(Settings);
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static AppSettings Sanitize(AppSettings settings)
    {
        settings.HoldSeconds = Math.Clamp(settings.HoldSeconds, 1, 5);

        if (!Enum.IsDefined(settings.Position))
        {
            settings.Position = OverlayPosition.BottomCenter;
        }

        if (!Enum.IsDefined(settings.Size))
        {
            settings.Size = OverlaySize.Medium;
        }

        settings.EdgeMargin = Math.Clamp(settings.EdgeMargin, 0, 400);
        settings.IgnoredApps ??= [];
        return settings;
    }

    /// <summary>
    /// Convertit les enums en chaînes et retombe sur une valeur par défaut si le
    /// fichier contient une valeur inconnue, au lieu de faire échouer tout le JSON.
    /// </summary>
    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private readonly TEnum _fallback;

        public TolerantEnumConverter(TEnum fallback)
        {
            _fallback = fallback;
        }

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) &&
                    Enum.IsDefined(parsed))
                {
                    return parsed;
                }

                return _fallback;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                var parsed = (TEnum)Enum.ToObject(typeof(TEnum), number);
                return Enum.IsDefined(parsed) ? parsed : _fallback;
            }

            return _fallback;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
