using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MTRTracer.Core;
using MTRTracer.Core.Logging;

namespace MTRTracer.Linux;

/// <summary>
/// Linux counterpart of the WPF MessageBridge: OS integration commands (files, folders) are handled here,
/// everything else goes to <see cref="AppController"/>. The window has native decorations, so window.*
/// commands are no-ops; the page copies to the clipboard itself (navigator.clipboard on 127.0.0.1).
/// </summary>
public sealed class HostCommands(AppController controller, Action? close)
{
    public async Task<string?> HandleAsync(string json)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "req") return null;
            id = root.GetProperty("id").GetInt64();
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
            var result = await HandleAsync(type, payload);
            return JsonSerializer.Serialize(new { kind = "res", id, ok = true, result }, Json.Options);
        }
        catch (CommandException e)
        {
            return JsonSerializer.Serialize(new { kind = "res", id, ok = false, error = new { code = e.Code, message = e.Message } }, Json.Options);
        }
        catch (Exception e)
        {
            Log(e);
            return JsonSerializer.Serialize(new { kind = "res", id, ok = false, error = new { code = "internal", message = e.Message } }, Json.Options);
        }
    }

    private async Task<object?> HandleAsync(string type, JsonElement p)
    {
        switch (type)
        {
            case "window.minimize":
            case "window.drag":
                return null;
            case "window.toggleMaximize":
            case "window.state":
                return new { maximized = false };
            case "window.close":
                close?.Invoke();
                return null;

            case "clipboard.copy":
                // Handled in the page; reaching here means navigator.clipboard was unavailable.
                if (TryClipboard(Str(p, "text") ?? "")) return null;
                throw new CommandException("clipboard", Loc.S("Не удалось скопировать: установите wl-clipboard или xclip.", "Could not copy: install wl-clipboard or xclip."));

            case "shell.open":
            {
                var path = Str(p, "path") switch
                {
                    "sessions" => controller.SessionsDir,
                    "data" => controller.DataDir,
                    _ => throw new CommandException("forbidden", Loc.S("Можно открыть только папки приложения.", "Only the app's own folders can be opened.")),
                };
                Directory.CreateDirectory(path);
                Open(path);
                return null;
            }

            case "shell.reveal":
            {
                var path = Str(p, "path");
                if (path == null || !File.Exists(path)) throw new CommandException("forbidden", Loc.S("Файл процесса не найден.", "Process file not found."));
                Open(Path.GetDirectoryName(path)!);
                return null;
            }

            case "shell.url":
            {
                // External links from the settings window (releases, forks, database pages). https only.
                var url = Str(p, "url");
                if (!ExternalLinks.IsAllowed(url)) throw new CommandException("forbidden", Loc.S("Эту ссылку открыть нельзя.", "This link cannot be opened."));
                Open(url!);
                return null;
            }

            case "file.pick":
                // zenity (GNOME) or kdialog (KDE); without them the page asks for a path instead.
                foreach (var (cmd, args) in new (string, string[])[]
                         {
                             ("zenity", ["--file-selection", "--title=MaxMind .mmdb", "--file-filter=*.mmdb *.gz *.tgz *.tar"]),
                             ("kdialog", ["--getopenfilename", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "*.mmdb *.gz *.tgz *.tar"]),
                         })
                {
                    if (!HasCommand(cmd)) continue;
                    // Cancel exits non-zero, which Run reports as null.
                    var r = await Task.Run(() => Core.Native.LinuxSystem.Run(cmd, args, 600_000));
                    return new { path = string.IsNullOrWhiteSpace(r) ? null : r.Trim() };
                }
                throw new CommandException("unsupported", Loc.S("Нет диалога выбора файла (zenity/kdialog). Введите путь вручную.", "No file dialog available (zenity/kdialog). Enter the path manually."));

            case "process.icon":
                return new { dataUrl = (string?)null };

            case "export.save":
            {
                var format = Str(p, "format") ?? "txt";
                var built = await controller.HandleAsync("export.build", p);
                var el = JsonSerializer.SerializeToElement(built, Json.Options);
                var content = el.GetProperty("content").GetString() ?? "";
                if (format == "clipboard")
                {
                    if (TryClipboard(content)) return new { copied = true };
                    throw new CommandException("clipboard", Loc.S("Не удалось скопировать: установите wl-clipboard или xclip.", "Could not copy: install wl-clipboard or xclip."));
                }
                var dir = DownloadsDir();
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, el.GetProperty("fileName").GetString()!);
                await File.WriteAllTextAsync(file, content, new UTF8Encoding(false));
                return new { path = file };
            }

            default:
                return await controller.HandleAsync(type, p);
        }
    }

    private static bool HasCommand(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, name)));

    private static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>XDG download directory (xdg-user-dir DOWNLOAD), falling back to ~/Downloads.</summary>
    private static string DownloadsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Core.Native.LinuxSystem.Run("xdg-user-dir", ["DOWNLOAD"], 1500)?.Trim();
        return !string.IsNullOrEmpty(xdg) && xdg != home ? xdg : Path.Combine(home, "Downloads");
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false }); } catch { }
    }

    private static bool TryClipboard(string text)
    {
        foreach (var (cmd, args) in new (string, string[])[] { ("wl-copy", []), ("xclip", ["-selection", "clipboard"]), ("xsel", ["--clipboard", "--input"]) })
        {
            try
            {
                var psi = new ProcessStartInfo(cmd) { RedirectStandardInput = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                if (proc.WaitForExit(2000) && proc.ExitCode == 0) return true;
            }
            catch { }
        }
        return false;
    }

    public static void Log(Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTRTracer", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"errors-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:O}] {e}\n\n");
        }
        catch { }
    }
}
