using System.Drawing;
using Forms = System.Windows.Forms;

namespace PyQsirchgui.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show PyQsirchgui", null, (_, _) => showWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());

        _icon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "PyQsirchgui",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        if (resource?.Stream == null)
        {
            throw new InvalidOperationException("The application tray icon could not be loaded.");
        }

        using (resource.Stream)
        {
            return new Icon(resource.Stream);
        }
    }
}
