using System.Net;

namespace MTRTracer.Core.Mtr;

public sealed record ParsedTarget(string Host, int? Port);

/// <summary>Accepts what users paste: IPs, domains, URLs, host:port, [v6]:port, user@host.</summary>
public static class TargetParser
{
    public static ParsedTarget? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"', '\'', '<', '>');
        int? port = null;

        if (s.Contains("://"))
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                var h = uri.IdnHost.Trim('[', ']');
                return new ParsedTarget(h, uri.IsDefaultPort ? DefaultPort(uri.Scheme) : uri.Port);
            }
            s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        // Drop path, query, fragment.
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return null;
            var inner = s[1..close];
            if (close + 2 < s.Length && s[close + 1] == ':' && int.TryParse(s[(close + 2)..], out var p6)) port = p6;
            return IPAddress.TryParse(inner, out _) ? new ParsedTarget(inner, port) : null;
        }

        if (IPAddress.TryParse(s, out var ip) && (s.Count(c => c == ':') >= 2 || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            return new ParsedTarget(s, null);

        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var p))
        {
            port = p;
            s = s[..colon];
        }

        s = s.TrimEnd('.');
        if (s.Length == 0 || s.Length > 253) return null;
        if (s.Any(c => char.IsWhiteSpace(c) || c is ',' or ';' or '\\')) return null;
        if (s.Any(c => c > 127))
        {
            try { s = new System.Globalization.IdnMapping().GetAscii(s); } catch { return null; }
        }
        return new ParsedTarget(s.ToLowerInvariant(), port);
    }

    private static int? DefaultPort(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        "ftp" => 21,
        "ssh" => 22,
        _ => null,
    };
}
