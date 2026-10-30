using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;

namespace GpuUnlocker.App;

/// <summary>RESX/ResourceManager localization with English fallback and live WPF bindings.</summary>
public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Current { get; } = new();
    private static readonly ResourceManager Resources = new("GpuUnlocker.App.Resources.Strings", typeof(Localization).Assembly);
    private static readonly LanguagePreference Preference = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPUUnlocker", "language.txt"));
    private static readonly Lazy<Dictionary<string, string>> DiagnosticKeys = new(() =>
        Resources.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, true)!
            .Cast<DictionaryEntry>().Where(e => e.Value is string)
            .GroupBy(e => (string)e.Value!).ToDictionary(g => g.Key, g => (string)g.First().Key));
    private CultureInfo culture = CultureInfo.GetCultureInfo("en-US");
    public CultureInfo Culture => culture;
    public string this[string key] => Resources.GetString(key, culture) ?? throw new MissingManifestResourceException($"Missing localization key: {key}");
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    public void Initialize()
    {
        SetCulture(Preference.Load(CultureInfo.CurrentUICulture));
    }

    public void SetCulture(string name)
    {
        if (name is not ("zh-CN" or "en-US")) throw new ArgumentOutOfRangeException(nameof(name));
        culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new("Item[]"));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SavePreference() => Preference.Save(culture.Name);

    public static LocalizedText T(string key, params object?[] args) => new(() => string.Format(Current.Culture, Current[key], args));

    // Only exact, known backend display labels are localized. Protocol values, paths,
    // exception details, and diagnostic logs remain intact.
    public static LocalizedText Diagnostic(string text) => DiagnosticKeys.Value.TryGetValue(text, out string? key) ? T(key) : text;
}

internal sealed class LanguagePreference(string path)
{
    public string Load(CultureInfo systemCulture)
    {
        try
        {
            if (File.Exists(path))
            {
                string saved = File.ReadAllText(path).Trim();
                if (saved is "zh-CN" or "en-US") return saved;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return systemCulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US";
    }
    public void Save(string culture)
    {
        if (culture is not ("zh-CN" or "en-US")) throw new ArgumentOutOfRangeException(nameof(culture));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, culture);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Keep the resource key and arguments so existing status messages also change language.</summary>
public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly Func<string> render;
    public LocalizedText(Func<string> render)
    {
        this.render = render;
        // WPF's weak event manager avoids retaining completed progress/status messages.
        PropertyChangedEventManager.AddHandler(Localization.Current, OnCultureChanged, "Item[]");
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, new(nameof(Value)));
    public string Value => render();
    public override string ToString() => Value;
    public static implicit operator string(LocalizedText value) => value.Value;
    public static implicit operator LocalizedText(string value) => new(() => value);
    public static LocalizedText operator +(LocalizedText left, string right) => new(() => left.Value + right);
    public static LocalizedText operator +(string left, LocalizedText right) => new(() => left + right.Value);
    public static LocalizedText operator +(LocalizedText left, LocalizedText right) => new(() => left.Value + right.Value);
}
