using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MTRTracer.Core;
using MTRTracer.Core.Logging;

namespace MTRTracer.App.Bridge;

/// <summary>
/// Request/response channel between the React page and the app: {kind:"req", id, type, payload} in,
/// {kind:"res", id, ok, result | error} out. Window and OS integration commands are handled here,
/// everything else goes to <see cref="AppController"/>.
/// </summary>
public sealed class MessageBridge
{
    private readonly Window _window;
    private readonly AppController _controller;
    private readonly Action<string> _post;
    private readonly ConcurrentDictionary<string, string?> _icons = new();

    public MessageBridge(Window window, AppController controller, Action<string> post)
    {
        _window = window;
        _controller = controller;
        _post = post;
    }

    public async void OnMessage(string json)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "req") return;
            id = root.GetProperty("id").GetInt64();
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
            var result = await HandleAsync(type, payload);
            Reply(new { kind = "res", id, ok = true, result });
        }
        catch (CommandException e)
        {
            Reply(new { kind = "res", id, ok = false, error = new { code = e.Code, message = e.Message } });
        }
        catch (Exception e)
        {
            App.LogError(e);
            Reply(new { kind = "res", id, ok = false, error = new { code = "internal", message = e.Message } });
        }
    }

    private void Reply(object msg) => _post(JsonSerializer.Serialize(msg, Json.Options));

    private async Task<object?> HandleAsync(string type, JsonElement p)
    {
        switch (type)
        {
            case "window.minimize":
                _window.WindowState = WindowState.Minimized;
                return null;
            case "window.toggleMaximize":
                _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return new { maximized = _window.WindowState == WindowState.Maximized };
            case "window.close":
                _window.Close();
                return null;
            case "window.drag":
                ReleaseCapture();
                SendMessage(new WindowInteropHelper(_window).Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                return null;
            case "window.state":
                return new { maximized = _window.WindowState == WindowState.Maximized };

            case "clipboard.copy":
                Clipboard.SetText(Str(p, "text") ?? "");
                return null;

            case "shell.open":
            {
                var path = Str(p, "path");
                if (path == "sessions") path = _controller.SessionsDir;
                else if (path == "data") path = _controller.DataDir;
                else throw new CommandException("forbidden", "Можно открыть только папки приложения.");
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return null;
            }

            case "shell.reveal":
            {
                // Only reveals an existing executable in Explorer (used for "open process folder").
                var path = Str(p, "path");
                if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new CommandException("forbidden", "Файл процесса не найден.");
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return null;
            }

            case "export.save":
            {
                var format = Str(p, "format") ?? "txt";
                var built = await _controller.HandleAsync("export.build", p);
                var el = JsonSerializer.SerializeToElement(built, Json.Options);
                var content = el.GetProperty("content").GetString() ?? "";
                if (format == "clipboard")
                {
                    Clipboard.SetText(content);
                    return new { copied = true };
                }
                var dlg = new SaveFileDialog
                {
                    FileName = el.GetProperty("fileName").GetString(),
                    Filter = el.GetProperty("filter").GetString() + "|Все файлы|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                };
                if (dlg.ShowDialog(_window) != true) return new { cancelled = true };
                await File.WriteAllTextAsync(dlg.FileName!, content, new System.Text.UTF8Encoding(true));
                return new { path = dlg.FileName };
            }

            case "process.icon":
                return new { dataUrl = Icon(Str(p, "path")) };

            default:
                return await _controller.HandleAsync(type, p);
        }
    }

    private static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private string? Icon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return _icons.GetOrAdd(path, pth =>
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(pth);
                if (icon == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return null;
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
