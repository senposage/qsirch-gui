using System.IO;
using System.Text.Json;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public static class ConfigStore
{
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
            return JsonSerializer.Deserialize<AppConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
