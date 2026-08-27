using System.Windows;
using System.Reflection;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\PyQsirchgui.SingleInstance";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstance())
        {
            Shutdown();
            return;
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            var mutex = new Mutex(true, InstanceMutexName, out var createdNew);
            if (createdNew)
            {
                _instanceMutex = mutex;
                return true;
            }

            mutex.Dispose();
            MessageBox.Show(
                "PyQsirchgui is already running on this computer. Close the existing instance before starting another.",
                "PyQsirchgui already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "PyQsirchgui could not establish its single-instance check. Contact your administrator before running another copy.",
                "PyQsirchgui startup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }
}
