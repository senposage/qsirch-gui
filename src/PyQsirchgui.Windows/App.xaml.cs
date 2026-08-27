using System.Windows;
using System.Reflection;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName();
        AppLogger.Info("app", $"startup assembly={assembly.Name} version={assembly.Version} streaming_search=True");
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("app", args.Exception, "dispatcher unhandled exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("app", ex, "app domain unhandled exception");
            }
            else
            {
                AppLogger.Error("app", new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown exception"), "app domain unhandled exception");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("app", args.Exception, "unobserved task exception");
            args.SetObserved();
        };
        base.OnStartup(e);
    }
}
