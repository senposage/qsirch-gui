using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class PathMapper(AppConfig config)
{
    private static readonly object MappedDrivesGate = new();
    private static IReadOnlyList<MappedDrive> _mappedDrives = [];
    private static DateTime _mappedDrivesExpiresUtc = DateTime.MinValue;
    public string Resolve(SearchResult result)
    {
        var qpath = Normalize(result.Path);
        var fileName = result.FileName.Trim();
        if (!result.IsFolder && !result.HasUsableFileName)
        {
            throw new InvalidOperationException("Qsirch did not provide a file name for this result. Run the search again to refresh it from the NAS.");
        }
        if (!string.IsNullOrWhiteSpace(fileName) && !qpath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            qpath = qpath.TrimEnd('\\') + "\\" + fileName;
        }
        if (qpath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return qpath;
        }

        foreach (var mapping in config.PathMappings)
        {
            var target = Normalize(mapping.MappedRoot).TrimEnd('\\');
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            if (TryResolveMappedRoot(qpath, source, target, out var resolved))
            {
                return resolved;
            }
        }

        var netUsePath = ResolveFromMappedDrives(qpath);
        if (!string.IsNullOrWhiteSpace(netUsePath))
        {
            return netUsePath;
        }

        throw new InvalidOperationException("No path mapping matched this result. Add a mapping in Settings, or map the matching NAS share in Windows.");
    }

    private static string? ResolveFromMappedDrives(string qpath)
    {
        var relative = qpath.TrimStart('\\');
        foreach (var drive in NetUseDrives())
        {
            var remote = Normalize(drive.Remote).TrimEnd('\\');
            if (qpath.StartsWith(remote, StringComparison.OrdinalIgnoreCase))
            {
                return CombineRoot(drive.LocalRoot, qpath[remote.Length..].TrimStart('\\'));
            }

            var remoteParts = remote.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var shareName = remoteParts.Length > 1 ? remoteParts[^1] : "";
            if (string.IsNullOrWhiteSpace(shareName))
            {
                continue;
            }

            foreach (var prefix in CandidateSharePrefixes(shareName))
            {
                if (relative.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.LocalRoot;
                }
                if (relative.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return CombineRoot(drive.LocalRoot, relative[(prefix.Length + 1)..]);
                }
            }
        }
        return null;
    }

    private static bool TryResolveMappedRoot(string qpath, string source, string target, out string resolved)
    {
        foreach (var prefix in CandidateMappingPrefixes(source))
        {
            if (!TryMatchPathRoot(qpath, prefix, out var rest))
            {
                continue;
            }

            resolved = CombineRoot(target, rest);
            return true;
        }

        resolved = "";
        return false;
    }

    private static IEnumerable<string> CandidateMappingPrefixes(string source)
    {
        var normalized = Normalize(source).Trim('\\');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        var sourceParts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var shareName = sourceParts.Length > 0 ? sourceParts[^1] : "";
        if (!string.IsNullOrWhiteSpace(shareName))
        {
            yield return shareName;
            yield return "Shared\\" + shareName;
        }
    }

    private static bool TryMatchPathRoot(string path, string root, out string rest)
    {
        path = Normalize(path).TrimStart('\\');
        root = Normalize(root).Trim('\\');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            rest = "";
            return true;
        }
        if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            rest = path[(root.Length + 1)..];
            return true;
        }

        rest = "";
        return false;
    }

    private static IEnumerable<string> CandidateSharePrefixes(string shareName)
    {
        yield return shareName;
        yield return "Shared\\" + shareName;
    }

    private static string CombineRoot(string root, string rest)
    {
        root = root.TrimEnd('\\') + "\\";
        return string.IsNullOrWhiteSpace(rest) ? root : Path.Combine(root, rest);
    }

    private static IReadOnlyList<MappedDrive> NetUseDrives()
    {
        lock (MappedDrivesGate)
        {
            if (DateTime.UtcNow < _mappedDrivesExpiresUtc)
            {
                return _mappedDrives;
            }

            _mappedDrives = ReadMappedDrives();
            _mappedDrivesExpiresUtc = DateTime.UtcNow.AddMinutes(2);
            return _mappedDrives;
        }
    }

    private static IReadOnlyList<MappedDrive> ReadMappedDrives()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("net.exe", "use")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null)
            {
                return [];
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            var drives = new List<MappedDrive>();
            foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var local = parts.FirstOrDefault(part => Regex.IsMatch(part, "^[A-Z]:$", RegexOptions.IgnoreCase));
                var remote = parts.FirstOrDefault(part => part.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(local) && !string.IsNullOrWhiteSpace(remote))
                {
                    drives.Add(new MappedDrive(local + "\\", remote));
                }
            }
            return drives;
        }
        catch
        {
            return [];
        }
    }

    private static string Normalize(string value) => (value ?? "").Replace('/', '\\').Trim();

    private sealed record MappedDrive(string LocalRoot, string Remote);
}
