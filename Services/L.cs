using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PowerHub.Services;

public sealed class L : INotifyPropertyChanged
{
    public static L Current { get; } = new();
    public static readonly LanguageChoice[] Languages = [new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"), new("en", "English"), new("ja", "日本語")];
    private Dictionary<string, string> translations = new(StringComparer.Ordinal);
    public string Language { get; private set; } = "zh-Hans";
    public static string AppName => T("AppName");
    public event PropertyChangedEventHandler? PropertyChanged;

    public static string Normalize(string? language) => Languages.Any(item => item.Id == language) ? language! : "zh-Hans";
    public static bool SetLanguage(string? language)
    {
        var selected = Normalize(language);
        if (Current.Language == selected && Current.translations.Count > 0) return false;
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"PowerHub.Locales.{selected}.json");
        Current.translations = stream is null ? new(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
        Current.Language = selected;
        Current.PropertyChanged?.Invoke(Current, new(nameof(Language)));
        return true;
    }

    public static string T(string key)
    {
        if (Current.translations.TryGetValue(key, out var translated)) return translated;
        return key == "AppName" ? Current.Language.StartsWith("zh", StringComparison.Ordinal) ? "电栖" : "Perivolt" : key;
    }

    public static string F(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, T(key), arguments);
    public sealed record LanguageChoice(string Id, string Label);
}
