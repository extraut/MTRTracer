using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MTRTracer.App;

public partial class App : Application
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTRTracer");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;
    }

    public static void LogError(Exception? e)
    {
        if (e == null) return;
        try
        {
            var dir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"errors-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:O}] {e}\n\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
