using System.IO;
using System.Text.Json;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class HistoryStore(AppConfig config)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<SearchResult> Favorites()
    {
        return Entries()
            .Where(entry => entry.Starred)
            .Select(entry =>
            {
                var result = entry.ToSearchResult();
                result.IsFavorite = true;
                return result;
            })
            .Pipe(SortFoldersFirst);
    }

    public IReadOnlyList<SearchResult> SearchResults(string text, string mode)
    {
        var needle = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(needle) || !config.History.Enabled)
        {
            return [];
        }

        return Entries()
            .Where(entry => MatchesMode(entry, mode))
            .Where(entry => $"{entry.Name} {entry.Extension} {entry.Path}".Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.ToSearchResult())
            .Pipe(SortFoldersFirst);
    }

    public void AddResults(IEnumerable<SearchResult> results)
    {
        if (!config.History.Enabled)
        {
            return;
        }

        var entries = Entries().ToList();
        var now = DateTime.Now.ToString("s");
        foreach (var result in results)
        {
            var existing = entries.FirstOrDefault(entry =>
                entry.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
                entry.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase) &&
                entry.MachineId.Equals(MachineId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                entries.Add(HistoryEntry.FromResult(result, now));
            }
            else
            {
                existing.LastUsed = now;
                existing.Uses += 1;
                existing.Item = result.Raw;
            }
        }
        Write(entries);
    }

    public void SetStarred(SearchResult result, bool starred)
    {
        if (!config.History.Enabled)
        {
            return;
        }

        var entries = Entries().ToList();
        var now = DateTime.Now.ToString("s");
        var existing = entries.FirstOrDefault(entry =>
            entry.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
            entry.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase) &&
            entry.MachineId.Equals(MachineId, StringComparison.OrdinalIgnoreCase));
        if (existing == null && starred)
        {
            existing = HistoryEntry.FromResult(result, now);
            entries.Add(existing);
        }
        if (existing != null)
        {
            existing.Starred = starred;
            existing.LastUsed = now;
            existing.Item = result.Raw;
        }
        Write(entries);
    }

    public bool IsStarred(SearchResult result)
    {
        return Entries().Any(entry =>
            entry.Starred &&
            entry.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
            entry.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
    }

    public void ClearCurrentMachine(bool clearStarred)
    {
        var entries = Entries()
            .Where(entry => !(entry.MachineId.Equals(MachineId, StringComparison.OrdinalIgnoreCase) || entry.Machine.Equals(MachineName, StringComparison.OrdinalIgnoreCase)) ||
                            (entry.Starred && !clearStarred))
            .ToList();
        Write(entries);
    }

    private IReadOnlyList<HistoryEntry> Entries()
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

            var entries = new List<HistoryEntry>();
            foreach (var entry in results.EnumerateArray())
            {
                entries.Add(HistoryEntry.FromJson(entry));
            }
            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path) || !string.IsNullOrWhiteSpace(entry.Name))
                .OrderByDescending(entry => entry.Starred)
                .ThenByDescending(entry => entry.LastUsed)
                .Take(Math.Max(1, config.History.MaxEntries))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static List<SearchResult> SortFoldersFirst(IEnumerable<SearchResult> results)
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

    private static bool MatchesMode(HistoryEntry entry, string mode)
    {
        return mode switch
        {
            "__all__" => true,
            "__favorites__" => entry.Starred,
            "" or "__this__" => entry.Machine.Equals(MachineName, StringComparison.OrdinalIgnoreCase) || entry.MachineId.Equals(MachineId, StringComparison.OrdinalIgnoreCase),
            _ => entry.Machine.Equals(mode, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void Write(IEnumerable<HistoryEntry> entries)
    {
        var path = HistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var normalized = entries
            .GroupBy(entry => $"{entry.Path}\0{entry.Name}\0{entry.MachineId}".ToLowerInvariant())
            .Select(group => group.OrderByDescending(entry => entry.LastUsed).First())
            .OrderByDescending(entry => entry.Starred)
            .ThenByDescending(entry => entry.LastUsed)
            .Take(Math.Max(1, config.History.MaxEntries))
            .ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 2, results = normalized }, JsonOptions));
    }

    private static string MachineName => Environment.MachineName;
    private static string MachineId => Environment.MachineName;
    private static string LocalIp => "";

    private sealed class HistoryEntry
    {
        public string Name { get; set; } = "";
        public string Extension { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string LastUsed { get; set; } = "";
        public string Machine { get; set; } = MachineName;
        public string MachineId { get; set; } = HistoryStore.MachineId;
        public string Ip { get; set; } = LocalIp;
        public int Uses { get; set; } = 1;
        public bool Starred { get; set; }
        public JsonElement Item { get; set; }

        public SearchResult ToSearchResult()
        {
            SearchResult result;
            try
            {
                result = Item.ValueKind == JsonValueKind.Object ? QsirchClient.ResultFromJson(Item.Clone()) : new SearchResult();
            }
            catch
            {
                result = new SearchResult();
            }
            result.Name = string.IsNullOrWhiteSpace(result.Name) ? Name : result.Name;
            result.Extension = string.IsNullOrWhiteSpace(result.Extension) ? Extension : result.Extension;
            result.Path = string.IsNullOrWhiteSpace(result.Path) ? Path : result.Path;
            result.Size = result.Size == 0 ? Size : result.Size;
            result.IsFavorite = Starred;
            return result;
        }

        public static HistoryEntry FromResult(SearchResult result, string now)
        {
            return new HistoryEntry
            {
                Name = result.Name,
                Extension = result.Extension,
                Path = result.Path,
                Size = result.Size,
                LastUsed = now,
                Machine = MachineName,
                MachineId = HistoryStore.MachineId,
                Ip = LocalIp,
                Uses = 1,
                Starred = result.IsFavorite,
                Item = result.Raw,
            };
        }

        public static HistoryEntry FromJson(JsonElement entry)
        {
            var item = entry.TryGetProperty("item", out var stored) && stored.ValueKind == JsonValueKind.Object ? stored.Clone() : entry.Clone();
            var result = QsirchClient.ResultFromJson(item);
            return new HistoryEntry
            {
                Name = GetString(entry, "name", result.Name),
                Extension = GetString(entry, "extension", result.Extension),
                Path = GetString(entry, "path", result.Path),
                Size = GetLong(entry, "size", result.Size),
                LastUsed = GetString(entry, "lastUsed", GetString(entry, "last_used")),
                Machine = GetString(entry, "machine"),
                MachineId = GetString(entry, "machineId", GetString(entry, "machine_id")),
                Ip = GetString(entry, "ip"),
                Uses = (int)GetLong(entry, "uses", 1),
                Starred = entry.TryGetProperty("starred", out var starred) && starred.ValueKind == JsonValueKind.True,
                Item = item,
            };
        }

        private static string GetString(JsonElement element, string name, string fallback = "")
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() ?? fallback : fallback;
        }

        private static long GetLong(JsonElement element, string name, long fallback = 0)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return fallback;
            }
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                ? number
                : long.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
    }
}

internal static class EnumerableExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> fn) => fn(value);
}
