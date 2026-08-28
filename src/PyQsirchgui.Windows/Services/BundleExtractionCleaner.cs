using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PyQsirchgui.Windows.Services;

public static class BundleExtractionCleaner
{
    public static BundleCleanupResult RemoveStaleBundles()
    {
        try
        {
            var extractionBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            if (string.IsNullOrWhiteSpace(extractionBase))
            {
                extractionBase = Path.Combine(Path.GetTempPath(), ".net");
            }

            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "PyQsirchgui";
            var appRoot = Path.GetFullPath(Path.Combine(extractionBase, appName));
            if (!Directory.Exists(appRoot))
            {
                return new BundleCleanupResult(0, 0, false);
            }

            var activeBundle = FindActiveBundleDirectory(appRoot);
            if (activeBundle == null)
            {
                return new BundleCleanupResult(0, 0, false);
            }

            var removed = 0;
            var retained = 0;
            foreach (var directory in Directory.EnumerateDirectories(appRoot))
            {
                if (directory.Equals(activeBundle, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var info = new DirectoryInfo(directory);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        retained++;
                        continue;
                    }

                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (IOException)
                {
                    retained++;
                }
                catch (UnauthorizedAccessException)
                {
                    retained++;
                }
            }

            return new BundleCleanupResult(removed, retained, true);
        }
        catch
        {
            return new BundleCleanupResult(0, 0, false);
        }
    }

    private static string? FindActiveBundleDirectory(string appRoot)
    {
        var rootWithSeparator = appRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var modulePath = module.FileName;
                if (!modulePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(appRoot, modulePath);
                var bundleDirectory = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (!string.IsNullOrWhiteSpace(bundleDirectory))
                {
                    return Path.Combine(appRoot, bundleDirectory);
                }
            }
        }
        catch
        {
        }

        return null;
    }
}

public readonly record struct BundleCleanupResult(int Removed, int Retained, bool ActiveBundleFound);
