using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using MTRTracer.App.Bridge;
using MTRTracer.Core;

namespace MTRTracer.App;

public partial class MainWindow : Window
{
    private const string Host = "app.mtrtracer";
    private AppController? _controller;
    private WebEventSink? _sink;
    private MessageBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowStyle();
        StateChanged += (_, _) => OnStateChanged();
        Loaded += async (_, _) => await InitAsync();
        Closing += async (_, _) =>
        {
            if (_controller != null) await _controller.DisposeAsync();
        };
    }

    private async Task InitAsync()
    {
        Loc.Language = AppController.PeekLanguage(App.DataDir);
        string? version = null;
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
        if (version == null)
        {
            ShowFallback(Loc.S("Для работы интерфейса нужен Microsoft Edge WebView2 Runtime. Он встроен в Windows 11 и большинство Windows 10; установите его и перезапустите программу.",
                "The interface needs the Microsoft Edge WebView2 Runtime. It ships with Windows 11 and most Windows 10 installs; install it and restart the program."));
            return;
        }

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDir, "WebView2"),
                new CoreWebView2EnvironmentOptions());
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            App.LogError(e);
            ShowFallback(Loc.S("Не удалось запустить WebView2: ", "Could not start WebView2: ") + e.Message);
            return;
        }

        var core = Web.CoreWebView2;
        var dev = Environment.GetEnvironmentVariable("MTRTRACER_DEV_URL");
        var s = core.Settings;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = dev != null;
        s.AreDefaultContextMenusEnabled = dev != null;
        s.AreDevToolsEnabled = dev != null || Debugger.IsAttached || Environment.GetEnvironmentVariable("MTRTRACER_DEVTOOLS") == "1";
        try { s.IsNonClientRegionSupportEnabled = true; } catch { }

        // External links open in the default browser, never inside the app.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{Host}/") && (dev == null || !e.Uri.StartsWith(dev)))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        _sink = new WebEventSink(Dispatcher);
        _controller = new AppController(_sink, App.DataDir);
        _bridge = new MessageBridge(this, _controller, json => core.PostWebMessageAsJson(json));
        core.WebMessageReceived += (_, e) => _bridge.OnMessage(e.WebMessageAsJson);
        _sink.Attach(json => core.PostWebMessageAsJson(json));

        if (dev != null)
        {
            core.Navigate(dev);
        }
        else
        {
            var root = UiAssets.Extract(App.DataDir);
            if (!File.Exists(Path.Combine(root, "index.html")))
            {
                ShowFallback(Loc.S("Интерфейс не встроен в программу: перед сборкой нужно собрать папку ui (npm run build). Запустите build.ps1, он сделает всё по порядку.",
                    "The interface is not embedded: build the ui folder first (npm run build). Run build.ps1, it does everything in order."));
                return;
            }
            core.SetVirtualHostNameToFolderMapping(Host, root, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate($"https://{Host}/index.html");
        }
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    private void ShowFallback(string text)
    {
        Web.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
        FallbackText.Text = text;
        FallbackLink.Content = Loc.S("Скачать WebView2 Runtime", "Download WebView2 Runtime");
        FallbackClose.Content = Loc.S("Закрыть", "Close");
    }

    private void OnInstallRuntime(object sender, RoutedEventArgs e) =>
        OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        // A maximized chrome-less window overhangs the monitor by the resize border; compensate.
        if (WindowState == WindowState.Maximized)
        {
            var b = SystemParameters.WindowResizeBorderThickness;
            var pad = SystemParameters.WindowNonClientFrameThickness;
            Root.Margin = new Thickness(b.Left + pad.Left - 1, b.Top + 2, b.Right + pad.Right - 1, b.Bottom + pad.Bottom - 1);
        }
        else
        {
            Root.Margin = new Thickness(0);
        }
        _sink?.Emit("window.state", new { maximized = WindowState == WindowState.Maximized });
    }

    private void ApplyWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        int border = 0x00332D2A; // COLORREF (BGR): subtle border matching the UI
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>The built React UI ships as embedded resources and is unpacked once per build.</summary>
internal static class UiAssets
{
    public static string Extract(string dataDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stamp = asm.ManifestModule.ModuleVersionId.ToString("N")[..12];
        var baseDir = Path.Combine(dataDir, "ui");
        var root = Path.Combine(baseDir, stamp);
        var marker = Path.Combine(root, ".complete");
        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(root);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/")))
            {
                var rel = name["ui/".Length..].Replace('\\', '/');
                var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            foreach (var old in Directory.GetDirectories(baseDir).Where(d => !d.EndsWith(stamp)))
            {
                try { Directory.Delete(old, true); } catch { }
            }
        }
        return root;
    }
}
