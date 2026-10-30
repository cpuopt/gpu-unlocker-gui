using static GpuUnlocker.App.Localization;
using System.Security.Principal;
using System.Windows;
using GpuUnlocker.Core;

namespace GpuUnlocker.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--benchmark-worker")) { RunWorker(e.Args); return; }
        Localization.Current.Initialize();
        if (e.Args.Contains("--smoke-test")) { RunSmoke(); return; }
        instance = new Mutex(false, @"Local\GPUUnlocker-" + WindowsIdentity.GetCurrent().User?.Value);
        try { ownsMutex = instance.WaitOne(e.Args.Contains("--elevated-restart") ? 10000 : 0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) { MessageBox.Show(BuildInfo.ProductName + T("AlreadyRunning"), BuildInfo.ProductName); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    private async void RunWorker(string[] args)
    {
        int code = await Task.Run(() => BenchmarkWorker.Execute(args));
        Shutdown(code);
    }
    private async void RunSmoke()
    {
        try
        {
            foreach (string script in new[] { "backend.ps1", "esp.ps1", "generate.ps1" })
                if (!System.IO.File.Exists(System.IO.Path.Combine(WindowsBackend.ScriptDirectory, script)))
                    throw new System.IO.FileNotFoundException(T("ScriptsMissing"), script);
            await new WindowsBackend().RunAsync<GpuInfo[]>("Gpus");
            var window = new MainWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000 };
            MainWindow = window; window.Show();
            await Task.Delay(1600);
            for (int i = 0; i < 80 && window.ViewModel.IsBusy; i++) await Task.Delay(100);
            if (window.ViewModel.RiskAccepted || window.ViewModel.InstallCommand.CanExecute(null) || window.ViewModel.SetFirstCommand.CanExecute(null) || window.ViewModel.BootOrderRiskAccepted || window.ViewModel.Checks.Count != 6)
                throw new InvalidOperationException("Initial UI gate checks failed.");
            await LocalizationSmoke.VerifyAsync(window);
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            string output = Environment.GetEnvironmentVariable("GPUUNLOCKER_SMOKE_OUTPUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpu-unlocker-smoke.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = System.IO.File.Create(output)) encoder.Save(file);
            var scroll = ((System.Windows.Controls.DockPanel)window.Content).Children.OfType<System.Windows.Controls.ScrollViewer>().Single();
            window.BootPrioritySection.BringIntoView(); window.UpdateLayout();
            var priority = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            priority.Render(window);
            var priorityEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); priorityEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(priority));
            using (var file = System.IO.File.Create(System.IO.Path.ChangeExtension(output, ".priority.png"))) priorityEncoder.Save(file);
            scroll.ScrollToEnd(); window.UpdateLayout();
            var bottom = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bottom.Render(window);
            var bottomEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); bottomEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bottom));
            using (var file = System.IO.File.Create(System.IO.Path.ChangeExtension(output, ".bottom.png"))) bottomEncoder.Save(file);
            window.Close(); Shutdown(0);
        }
        catch (Exception ex)
        {
            string output = Environment.GetEnvironmentVariable("GPUUNLOCKER_SMOKE_OUTPUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpu-unlocker-smoke.png");
            System.IO.File.WriteAllText(output + ".error.txt", ex.ToString());
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
