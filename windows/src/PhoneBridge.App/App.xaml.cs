using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhoneBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background loop throwing must not silently kill the session with no trace.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "unobserved task");
            args.SetObserved();
        };
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "dispatcher");
        MessageBox.Show(
            $"{e.Exception.Message}\n\nSee %LOCALAPPDATA%\\PhoneBridge\\crash.log",
            "PhoneBridge error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBridge");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] ({source}) {ex}\n\n");
        }
        catch { /* logging must never be the thing that crashes */ }
    }
}
