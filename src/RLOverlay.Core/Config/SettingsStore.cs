using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RLOverlay.Core.Config;

/// <summary>Loads and saves <see cref="OverlaySettings"/> as JSON. Corrupt files are backed up and replaced by defaults.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger _logger;

    public SettingsStore(string path, ILogger<SettingsStore>? logger = null)
    {
        FilePath = path;
        _logger = logger ?? NullLogger<SettingsStore>.Instance;
    }

    public string FilePath { get; }

    public OverlaySettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new OverlaySettings().Normalize();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(FilePath), JsonOptions);
            return (settings ?? new OverlaySettings()).Upgrade().Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = FilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            _logger.LogWarning("Settings file unreadable ({Error}); using defaults and keeping a copy at {Backup}", ex.Message, backup);
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }

            return new OverlaySettings().Normalize();
        }
    }

    public void Save(OverlaySettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
