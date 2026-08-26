using System.IO;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class PathMapper(AppConfig config)
{
    public string Resolve(SearchResult result)
    {
        var qpath = Normalize(result.Path);
        var fileName = result.FileName;
        if (!string.IsNullOrWhiteSpace(fileName) && !qpath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            qpath = qpath.TrimEnd('\\') + "\\" + fileName;
        }

        foreach (var mapping in config.PathMappings)
        {
            var target = Normalize(mapping.MappedRoot).TrimEnd('\\');
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            if (qpath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            {
                var rest = qpath[source.Length..].TrimStart('\\');
                return string.IsNullOrWhiteSpace(rest) ? target : Path.Combine(target, rest);
            }

            var sourceParts = source.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var shareName = sourceParts.Length > 0 ? sourceParts[^1] : "";
            if (!string.IsNullOrWhiteSpace(shareName))
            {
                var relative = qpath.TrimStart('\\');
                if (relative.Equals(shareName, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }
                if (relative.StartsWith(shareName + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(target, relative[(shareName.Length + 1)..]);
                }
            }
        }

        throw new InvalidOperationException("No path mapping matched this result. Add a mapping in Settings.");
    }

    private static string Normalize(string value) => (value ?? "").Replace('/', '\\').Trim();
}
