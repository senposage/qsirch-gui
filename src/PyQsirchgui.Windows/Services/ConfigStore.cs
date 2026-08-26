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
            var appConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(appConfig))
            {
                return appConfig;
            }
            var repoConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config.json"));
            return File.Exists(repoConfig) ? repoConfig : appConfig;
        }
    }

    public static AppConfig Load()
    {
        try
        {
            var text = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }
}
