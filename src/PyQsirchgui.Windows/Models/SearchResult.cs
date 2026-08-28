using System.Text.Json;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PyQsirchgui.Windows.Models;

public sealed class SearchResult : INotifyPropertyChanged
{
    private ImageSource? _iconSource;
    private bool _isFavorite;

    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public long Size { get; set; }
    public string Modified { get; set; } = "";
    public bool IsFolder { get; set; }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }
            _isFavorite = value;
            OnPropertyChanged();
        }
    }
    public JsonElement Raw { get; set; }

    public string FileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                var fromPath = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
                return !string.IsNullOrWhiteSpace(Extension) && fromPath.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase)
                    ? fromPath
                    : "";
            }
            if (string.IsNullOrWhiteSpace(Extension) || Name.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase))
            {
                return Name;
            }
            return $"{Name}.{Extension}";
        }
    }

    public bool HasUsableFileName => !string.IsNullOrWhiteSpace(FileName) &&
                                     !FileName.Trim().Equals("." + Extension.Trim(), StringComparison.OrdinalIgnoreCase);

    public string DisplayPath
    {
        get
        {
            var text = Path.Replace('/', '\\').Trim('\\');
            if (text.StartsWith("Shared\\", StringComparison.OrdinalIgnoreCase))
            {
                text = text[7..];
            }
            return text;
        }
    }

    public string Kind => IsFolder ? "Folder" : string.IsNullOrWhiteSpace(Extension) ? "File" : Extension.ToUpperInvariant() + " File";
    public string SizeText => IsFolder || Size <= 0 ? "" : Size >= 1048576 ? $"{Size / 1048576.0:F1} MB" : $"{Math.Max(1, Size / 1024)} KB";
    public DateTime? ModifiedDate => TryParseModified(Modified);
    public string ModifiedGroup => DateGroup(ModifiedDate);
    public string IconText => IsFolder ? "Folder" : "File";
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public bool HasThumbnailAction
    {
        get
        {
            return Raw.ValueKind == JsonValueKind.Object &&
                   Raw.TryGetProperty("actions", out var actions) &&
                   actions.TryGetProperty("thumbnail", out var thumbnail) &&
                   thumbnail.ValueKind != JsonValueKind.Null &&
                   !string.IsNullOrWhiteSpace(thumbnail.ToString());
        }
    }

    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            if (Equals(_iconSource, value))
            {
                return;
            }
            _iconSource = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static DateTime? TryParseModified(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return local;
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var invariant))
        {
            return invariant;
        }
        return null;
    }

    private static string DateGroup(DateTime? value)
    {
        if (value == null)
        {
            return "";
        }

        var date = value.Value.Date;
        var today = DateTime.Today;
        var thisWeek = StartOfWeek(today);
        var lastWeek = thisWeek.AddDays(-7);
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);

        if (date == today)
        {
            return "Today";
        }
        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }
        if (date >= thisWeek)
        {
            return "Earlier this week";
        }
        if (date >= lastWeek && date < thisWeek)
        {
            return "Last week";
        }
        if (date >= thisMonth)
        {
            return "Earlier this month";
        }
        if (date >= lastMonth && date < thisMonth)
        {
            return "Last month";
        }
        return "Older";
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = ((int)date.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        return date.Date.AddDays(-offset);
    }
}

public sealed class FileTypeFilter
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "All";
    public string[] Extensions { get; init; } = [];

    public override string ToString() => Name;
}

public sealed class FileTypeFilterOption
{
    public required FileTypeFilter Filter { get; init; }
    public bool IsSelected { get; set; }

    public string Name => Filter.Name;
}

public sealed class ResultViewMode
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "details";

    public override string ToString() => Name;
}

public sealed class ResultSortMode
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "recent";

    public override string ToString() => Name;
}
