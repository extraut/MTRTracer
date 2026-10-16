using System.Globalization;

namespace MTRTracer.Core;

/// <summary>
/// Interface language for texts produced by the core (analyzer notes, events, errors, reports).
/// Both variants sit next to each other at the call site: <c>Loc.S("Нет ответа", "No reply")</c>.
/// </summary>
public static class Loc
{
    private static volatile bool _en = SystemDefault() == "en";

    /// <summary>"ru" or "en".</summary>
    public static string Language
    {
        get => _en ? "en" : "ru";
        set => _en = Normalize(value) == "en";
    }

    public static bool En => _en;

    public static string S(string ru, string en) => _en ? en : ru;

    /// <summary>Language to start with when the user has not chosen one: Russian for a Russian UI culture, English otherwise.</summary>
    public static string SystemDefault()
    {
        var c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return c is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
    }

    public static string Normalize(string? lang) => lang?.Trim().ToLowerInvariant() is "en" ? "en" : lang?.Trim().ToLowerInvariant() is "ru" ? "ru" : SystemDefault();
}
