using System.Diagnostics;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class ShellActions(PathMapper mapper)
{
    public void Open(SearchResult result)
    {
        var path = mapper.Resolve(result);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Show(SearchResult result)
    {
        var path = mapper.Resolve(result);
        var arguments = result.IsFolder ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    public void ShowProperties(SearchResult result)
    {
        var path = mapper.Resolve(result);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
    }
}
