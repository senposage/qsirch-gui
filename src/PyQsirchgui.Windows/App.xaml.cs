using System.Windows;
using System.Reflection;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\PyQsirchgui.SingleInstance";
    private const string InstanceActivationEventName = @"Global\PyQsirchgui.ActivateInstance";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceActivationEventName, out _);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ActivateExistingWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

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
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

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
            SignalExistingInstance();
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

    private void ActivateExistingWindow()
    {
        if (MainWindow is MainWindow window)
        {
            window.ActivateFromExternalLaunch();
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(InstanceActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
