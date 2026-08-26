using System.IO;
using System.Text.Json;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class HistoryStore(AppConfig config)
{
    public IReadOnlyList<SearchResult> Favorites()
    {
        var path = HistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var favorites = new List<SearchResult>();
            foreach (var entry in results.EnumerateArray())
            {
                if (!entry.TryGetProperty("starred", out var starred) || starred.ValueKind != JsonValueKind.True)
                {
                    continue;
                }
                var item = entry.TryGetProperty("item", out var storedItem) && storedItem.ValueKind == JsonValueKind.Object ? storedItem : entry;
                var result = QsirchClient.ResultFromJson(item.Clone());
                result.IsFavorite = true;
                favorites.Add(result);
            }
            return SortFoldersFirst(favorites);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<SearchResult> SortFoldersFirst(IEnumerable<SearchResult> results)
    {
        return results.Select((item, index) => new { item, index })
            .OrderBy(x => x.item.IsFolder ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    private string HistoryPath()
    {
        var file = string.IsNullOrWhiteSpace(config.History.File) ? "history.json" : config.History.File;
        if (Path.IsPathRooted(file))
        {
            return file;
        }
        return Path.Combine(Path.GetDirectoryName(ConfigStore.ConfigPath) ?? AppContext.BaseDirectory, file);
    }
}
