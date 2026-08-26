using System.Text.Json;

namespace PyQsirchgui.Windows.Models;

public sealed class SearchResult
{
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public long Size { get; set; }
    public string Modified { get; set; } = "";
    public bool IsFolder { get; set; }
    public bool IsFavorite { get; set; }
    public JsonElement Raw { get; set; }

    public string FileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Extension) || Name.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(Name) ? Path : Name;
            }
            return $"{Name}.{Extension}";
        }
    }

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
    public string IconText => IsFolder ? "Folder" : "File";
}

public sealed class FileTypeFilter
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "All";
    public string[] Extensions { get; init; } = [];

    public override string ToString() => Name;
}

public sealed class ResultViewMode
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "details";

    public override string ToString() => Name;
}
