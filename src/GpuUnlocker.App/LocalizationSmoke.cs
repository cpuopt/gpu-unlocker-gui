using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GpuUnlocker.Core;

namespace GpuUnlocker.App;

// Runs only under --smoke-test; no ESP writes, driver loads, or benchmark downloads.
internal static class LocalizationSmoke
{
    public static async Task VerifyAsync(MainWindow window)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Localization smoke: " + message);
        }
        var resources = new ResourceManager("GpuUnlocker.App.Resources.Strings", typeof(Localization).Assembly);
        var english = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var chinese = resources.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Require(english.Keys.Order().SequenceEqual(chinese.Keys.Order()), "Resource key parity");
        foreach (string key in english.Keys)
        {
            Require(!string.IsNullOrWhiteSpace(english[key]) && !string.IsNullOrWhiteSpace(chinese[key]), "Empty resource: " + key);
            Require(CompositeFormat.Parse(english[key]).MinimumArgumentCount == CompositeFormat.Parse(chinese[key]).MinimumArgumentCount, "Format argument parity: " + key);
        }
        Require(resources.GetString("CancelTask", CultureInfo.GetCultureInfo("fr-FR")) == english["CancelTask"], "English fallback");
        string preferencePath = Path.Combine(Path.GetTempPath(), "gpu-unlocker-language-" + Guid.NewGuid().ToString("N"), "language.txt");
        try
        {
            var preference = new LanguagePreference(preferencePath);
            Require(preference.Load(CultureInfo.GetCultureInfo("zh-TW")) == "zh-CN", "Chinese system default");
            Require(preference.Load(CultureInfo.GetCultureInfo("de-DE")) == "en-US", "Non-Chinese system default");
            preference.Save("en-US");
            Require(new LanguagePreference(preferencePath).Load(CultureInfo.GetCultureInfo("zh-CN")) == "en-US", "English preference persists across instances");
            preference.Save("zh-CN");
            Require(new LanguagePreference(preferencePath).Load(CultureInfo.GetCultureInfo("en-US")) == "zh-CN", "Chinese preference persists across instances");
            File.WriteAllText(preferencePath, "invalid-culture");
            Require(preference.Load(CultureInfo.GetCultureInfo("en-US")) == "en-US", "Invalid preference fallback");
        }
        finally { if (File.Exists(preferencePath)) File.Delete(preferencePath); }
        string originalCulture = Localization.Current.Culture.Name;
        var vm = window.ViewModel;
        var family = vm.SelectedFamily; var target = vm.SelectedTarget;
        string proxy = vm.ProxyAddress, originalLog = vm.Log;
        int rounds = vm.BenchmarkRounds, updates = 0;
        var message = Localization.T("PackageSummary", "ad102", 9, 12.5);
        message.PropertyChanged += (_, _) => updates++;
        string output = Environment.GetEnvironmentVariable("GPUUNLOCKER_SMOKE_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "gpu-unlocker-smoke.png");
        var scroll = ((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single();
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "en-US" })
            {
                Localization.Current.SetCulture(culture);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                Require(vm.SelectedLanguage.Culture == culture && ((LanguageOption)window.LanguageSelector.SelectedItem).Culture == culture, "Language selector synchronization");
                Require(ReferenceEquals(family, vm.SelectedFamily) && ReferenceEquals(target, vm.SelectedTarget), "GPU selection preservation");
                Require(vm.ProxyAddress == proxy && vm.BenchmarkRounds == rounds && vm.Log == originalLog, "Settings and log preservation");
                Require(!vm.RiskAccepted && !vm.BootOrderRiskAccepted && !vm.InstallCommand.CanExecute(null) && !vm.SetFirstCommand.CanExecute(null), "Consent and command gates");
                var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToArray();
                Require(texts.Contains(Localization.Current["RiskTitle"]), "Live static text binding");
                Require(texts.Contains(Localization.Current["AdminCheck"]), "Live check-row binding");
                Require(texts.Contains(vm.PackageDescription.Value), "Live status binding");
                Require((string)Descendants<DataGrid>(window).Single().Columns[0].Header == Localization.Current["ModeColumn"], "DataGrid header binding");
                Require(new ThroughputRow(new("FP32", "通过", 1, "", [], true)).Status.Value == Localization.Current["ResultPassed"], "Benchmark status localization");
                Require(message.Value.Contains(culture == "zh-CN" ? "个部署文件" : "deployment files"), "Existing formatted message refresh");
                scroll.ScrollToTop(); window.UpdateLayout(); Capture(window, Path.ChangeExtension(output, $".{culture}.png"));
                window.BootPrioritySection.BringIntoView(); window.UpdateLayout(); Capture(window, Path.ChangeExtension(output, $".{culture}.priority.png"));
                scroll.ScrollToEnd(); window.UpdateLayout(); Capture(window, Path.ChangeExtension(output, $".{culture}.bottom.png"));
            }
            Require(updates == 3, "Localized message change notifications");
            window.Width = window.MinWidth; scroll.ScrollToTop(); window.UpdateLayout();
            Capture(window, Path.ChangeExtension(output, ".narrow.png")); window.Width = 1020;
        }
        finally { Localization.Current.SetCulture(originalCulture); scroll.ScrollToTop(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
