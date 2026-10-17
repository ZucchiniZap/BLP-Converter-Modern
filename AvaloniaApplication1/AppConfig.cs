using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AvaloniaApplication1.Models;

public sealed class AppConfig
{
    private static readonly string ApplicationDataDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ConfigPath = Path.Combine(
        ApplicationDataDirectory,
        "ModernBLPConverter",
        "settings.json");
    private static readonly string[] LegacyConfigPaths =
    [
        Path.Combine(ApplicationDataDirectory, "AdvancedBlpConverter", "settings.json"),
        Path.Combine(ApplicationDataDirectory, "BlpMipmapFixer", "settings.json")
    ];

    public string LastInputPath { get; set; } = string.Empty;
    public string LastOutputPath { get; set; } = string.Empty;
    public string Compression { get; set; } = "DXT1";
    public string AlphaDepth { get; set; } = "1-bit";
    public bool GenerateMipmaps { get; set; } = true;
    public bool UseColorKeyAwareMipmapping { get; set; }
    public string ColorKeyHex { get; set; } = "#000000";

    public static AppConfig Load(out string? warning)
    {
        warning = null;
        var settingsPath = File.Exists(ConfigPath)
            ? ConfigPath
            : LegacyConfigPaths.FirstOrDefault(File.Exists);
        if (settingsPath is null)
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            warning = $"Settings could not be read and defaults were loaded: {ex.Message}";
            return new AppConfig();
        }
        catch (IOException ex)
        {
            warning = $"Settings could not be read: {ex.Message}";
            return new AppConfig();
        }
        catch (UnauthorizedAccessException ex)
        {
            warning = $"Settings could not be read: {ex.Message}";
            return new AppConfig();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, ConfigPath, overwrite: true);
    }
}