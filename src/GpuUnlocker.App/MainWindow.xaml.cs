using static GpuUnlocker.App.Localization;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GpuUnlocker.Core;

namespace GpuUnlocker.App;
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = ViewModel;
        Localization.Current.CultureChanged += OnCultureChanged;
        OnCultureChanged(this, EventArgs.Empty);
        Closed += (_, _) => Localization.Current.CultureChanged -= OnCultureChanged;
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Closing += OnClosing;
    }
    private void OnCultureChanged(object? sender, EventArgs e)
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.Current.Culture.IetfLanguageTag);
        ViewModel.RefreshLanguage();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            e.Cancel = true;
            ViewModel.AppendLog(ViewModel.IsTransaction ? T("TransactionClosing") : T("TaskClosing"));
        }
    }
    private void Terminal_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(value is CheckStatus.Pass ? Color.FromRgb(39, 164, 108) : Color.FromRgb(212, 75, 87));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class GpuDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is GpuTarget target ? $"{target.WindowsGpu.Name} · {target.Cuda?.PciBusId ?? target.WindowsGpu.PciBusId ?? values[1].ToString()}" : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
