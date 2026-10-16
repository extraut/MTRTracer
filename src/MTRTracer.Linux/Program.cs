using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using MTRTracer.Core;
using MTRTracer.Linux;
using Photino.NET;

// MTRTracer for Linux.
//   MTRTracer              native window (Photino + WebKitGTK)
//   MTRTracer --browser    open the interface in the default browser instead
//   MTRTracer --port 8642  fixed loopback port (default: random free port)
//   MTRTracer --devtools   enable WebKit inspector

var argv = args.Select(a => a.ToLowerInvariant()).ToList();
var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTRTracer");
Loc.Language = AppController.PeekLanguage(dataDir);
if (argv.Contains("--help") || argv.Contains("-h"))
{
    Console.WriteLine(Loc.S("""
        MTRTracer — трассировка маршрута с картой и историей.
          --browser      открыть интерфейс в браузере вместо окна
          --port N       порт локального сервера (по умолчанию случайный, только 127.0.0.1)
          --devtools     включить инспектор WebKit
        """, """
        MTRTracer — route tracer with a map and history.
          --browser      open the interface in the browser instead of a window
          --port N       local server port (random by default, 127.0.0.1 only)
          --devtools     enable the WebKit inspector
        """));
    return 0;
}
var browserMode = argv.Contains("--browser");
var port = 0;
var pi = argv.IndexOf("--port");
if (pi >= 0 && pi + 1 < argv.Count) int.TryParse(argv[pi + 1], out port);

Directory.CreateDirectory(dataDir);
AppDomain.CurrentDomain.UnhandledException += (_, e) => HostCommands.Log(e.ExceptionObject as Exception ?? new Exception("unknown"));
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    HostCommands.Log(e.Exception);
    e.SetObserved();
};

if (Assembly.GetExecutingAssembly().GetManifestResourceNames().All(n => !n.StartsWith("ui/")))
{
    Console.Error.WriteLine(Loc.S("Интерфейс не встроен в программу: сначала соберите ui (npm run build), затем опубликуйте. Используйте build.sh.",
        "The interface is not embedded: build ui first (npm run build), then publish. Use build.sh."));
    return 2;
}

var server = new LocalServer(port);
var controller = new AppController(server, dataDir);
PhotinoWindow? window = null;
var quit = new ManualResetEventSlim();
server.Start(new HostCommands(controller, () =>
{
    if (window != null) window.Invoke(() => window.Close());
    else quit.Set();
}));

Console.WriteLine($"MTRTracer: {server.Origin} ({Loc.S("только локально", "local only")})");

if (!browserMode)
{
    try
    {
        window = new PhotinoWindow()
            .SetTitle("MTRTracer")
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(1480, 920))
            .SetMinSize(980, 620)
            .Center()
            .SetResizable(true)
            .SetContextMenuEnabled(argv.Contains("--devtools"))
            .SetDevToolsEnabled(argv.Contains("--devtools"))
            .SetIconFile(ExtractIcon(dataDir) ?? "")
            .Load(new Uri(server.AppUrl));
        window.WaitForClose();
    }
    catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
    {
        // No WebKitGTK/GTK on this system: keep working through the browser.
        Console.Error.WriteLine(Loc.S("Не удалось открыть окно: нет libwebkit2gtk-4.1 / libgtk-3 (", "Could not open the window: libwebkit2gtk-4.1 / libgtk-3 missing (") + e.GetType().Name + ").");
        Console.Error.WriteLine(Loc.S("Открываю интерфейс в браузере. Установите пакет libwebkit2gtk-4.1-0 (Debian/Ubuntu) или webkit2gtk4.1 (Fedora), чтобы работало окно.",
            "Opening the interface in the browser. Install libwebkit2gtk-4.1-0 (Debian/Ubuntu) or webkit2gtk4.1 (Fedora) for the window."));
        window = null;
        browserMode = true;
    }
}

if (browserMode)
{
    OpenBrowser(server.AppUrl);
    Console.WriteLine(Loc.S("Интерфейс открыт в браузере. Ctrl+C — выход.", "The interface is open in the browser. Ctrl+C to quit."));
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        quit.Set();
    };
    quit.Wait();
}

await controller.DisposeAsync();
await server.DisposeAsync();
return 0;

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false }); }
    catch { Console.WriteLine(Loc.S("Откройте в браузере: ", "Open in a browser: ") + url); }
}

static string? ExtractIcon(string dataDir)
{
    try
    {
        var path = Path.Combine(dataDir, "mtrtracer.png");
        if (File.Exists(path)) return path;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mtrtracer.png");
        if (s == null) return null;
        using var f = File.Create(path);
        s.CopyTo(f);
        return path;
    }
    catch
    {
        return null;
    }
}
