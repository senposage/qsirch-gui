using System.IO;
using System.Text.Json;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath
    {
        get
        {
            var packageConfig = Path.Combine(AppContext.BaseDirectory, "config", "config.json");
            if (File.Exists(packageConfig))
            {
                return packageConfig;
            }

            var legacyPackageConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config", "config.json"));
            if (File.Exists(legacyPackageConfig))
            {
                return legacyPackageConfig;
            }

            var appConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(appConfig))
            {
                return appConfig;
            }

            var repoConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));
            return File.Exists(repoConfig) ? repoConfig : appConfig;
        }
    }

    public static string PortableRoot
    {
        get
        {
            var configDirectory = Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory;
            return Path.GetFileName(configDirectory).Equals("config", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(configDirectory) ?? configDirectory
                : configDirectory;
        }
    }

    public static AppConfig Load()
    {
        try
        {
            var text = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
        catch
        {
            var config = new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
    }

    public static void Save(AppConfig config)
    {
        config.CaptureCurrentHost();
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        config.ApplyCurrentHost();
    }
}
