using static GpuUnlocker.App.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using GpuUnlocker.Core;
using Microsoft.Win32;

namespace GpuUnlocker.App;

public sealed record LanguageOption(string Culture, string Name);
public sealed record FamilyOption(string Id, LocalizedText Name);
public sealed record CheckRow(string Key, LocalizedText Label, CheckStatus Status, LocalizedText Detail, bool ShowElevate = false, bool ShowRemove = false);
public sealed record ThroughputRow(ThroughputResult Result)
{
    public string Mode => Result.Mode;
    public double? Tflops => Result.Tflops;
    public double? LockedTflops => Result.LockedTflops;
    public double? ImprovementPercent => Result.ImprovementPercent;
    public LocalizedText Status => Diagnostic(Result.Status);
    public LocalizedText Detail => Result.CorrectnessPassed && Result.Detail.StartsWith("8192³ dense GEMM", StringComparison.Ordinal)
        ? T("GemmResultDetail", Result.Mode is "FP16" or "BF16" ? Result.Mode : "FP32", Result.Samples.Length)
        : Diagnostic(Result.Detail);
}

public sealed class UiCommand(Func<Task> execute, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly CacheLayout cache = new();
    private readonly WindowsBackend backend = new();
    private readonly DependencyDownloader downloader;
    private readonly PatchBuilder builder;
    private readonly UnlockProbe unlockProbe;
    private GpuTarget? selectedTarget;
    private UnlockState runtimeState = UnlockState.Unknown;
    private LocalizedText runtimeMessage = T("RuntimeNotChecked");
    private LocalizedText benchmarkMessage = T("BenchmarkNotRun");
    private string? reportPath;
    private BenchmarkStateEvidence? benchmarkEvidence;
    private readonly BenchmarkHistory benchmarkHistory = new();
    private CancellationTokenSource? cancellation;
    private EnvironmentReport environment;
    private PatchPackage? package;
    private bool dependenciesReady;
    private bool riskAccepted;
    private bool busy;
    private bool transaction;
    private bool canCancel;
    private bool initialized;
    private ProxyMode proxyMode = ProxyMode.System;
    private string proxyAddress = "http://127.0.0.1:7890";
    private FamilyOption? selectedFamily;
    private string log = T("ReadyLog", BuildInfo.ProductName);
    private LocalizedText statusText = T("ReadRisk");
    private LocalizedText gpuDescription = T("DetectingGpu");
    private LocalizedText downloadDescription = T("DependenciesNotDownloaded");
    private double downloadPercent;
    private LocalizedText packageDescription = T("PatchNotBuilt");
    private LocalizedText verificationMessage = T("InstallationNotChecked");
    private bool installedSuccessfully;
    private bool bootOrderRiskAccepted;
    private VerificationReport verification = new();

    public MainViewModel()
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        environment = new() { IsAdmin = admin };
        downloader = new(cache); builder = new(cache, downloader, backend);
        unlockProbe = new(cache, downloader);
        Checks.Add(new("admin", T("AdminCheck"), admin ? CheckStatus.Pass : CheckStatus.Fail, admin ? T("AdminRunning") : T("AdminRequired"), !admin));
        foreach (var item in new[] { ("gpt", T("GptCheck")), ("esp", T("EspCheck")), ("access", T("EspAccessCheck")), ("clean", T("EspCleanCheck")), ("secure", T("SecureBootCheck")) })
            Checks.Add(new(item.Item1, item.Item2, CheckStatus.Unknown, T("CheckAfterConsent")));
        SetVerification(new());
        RefreshCommand = new UiCommand(RefreshAsync, () => RiskAccepted && !IsBusy);
        ReopenCommand = new UiCommand(ReopenAsync, () => !IsBusy && !environment.IsAdmin);
        DownloadCommand = new UiCommand(DownloadAsync, () => RiskAccepted && environment.IsAdmin && !IsBusy);
        BuildCommand = new UiCommand(BuildAsync, () => RiskAccepted && environment.IsAdmin && dependenciesReady && SelectedFamily is not null && !IsBusy);
        InstallCommand = new UiCommand(InstallAsync, () => RiskAccepted && !IsBusy && package is not null && package.Manifest.Family == SelectedFamily?.Id && environment.CanInstall);
        RemoveCommand = new UiCommand(RemoveAsync, () => RiskAccepted && !IsBusy && environment.CanRemove);
        VerifyCommand = new UiCommand(VerifyAsync, () => RiskAccepted && !IsBusy && environment.IsAdmin && environment.EspFound);
        SetFirstCommand = new UiCommand(SetFirstAsync, () => BootPriorityGate.CanExecute(verification, RiskAccepted, BootOrderRiskAccepted, IsBusy, environment.IsAdmin, environment.EspWritable));
        CancelCommand = new UiCommand(() => { cancellation?.Cancel(); return Task.CompletedTask; }, () => IsBusy && canCancel && !IsTransaction);
        OpenCacheCommand = new UiCommand(() => { if (Directory.Exists(cache.Root)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { cache.Root } }); return Task.CompletedTask; }, () => Directory.Exists(cache.Root));
        SaveLogCommand = new UiCommand(SaveLogAsync);
        DetectRuntimeCommand = new UiCommand(DetectRuntimeAsync, () => RiskAccepted && environment.IsAdmin && dependenciesReady && SelectedTarget is not null && !IsBusy);
        BenchmarkCommand = new UiCommand(BenchmarkAsync, () => RiskAccepted && SelectedTarget?.Cuda is not null && !IsBusy);
        OpenReportCommand = new UiCommand(() => { if (reportPath is not null) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", reportPath } }); return Task.CompletedTask; }, () => reportPath is not null && File.Exists(reportPath));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void UpdateDerived()
    {
        foreach (string name in new[] { nameof(IsBusy), nameof(NotBusy), nameof(IsTransaction), nameof(SettingsEnabled), nameof(CustomProxyEnabled), nameof(InstallTooltip), nameof(TargetDescription), nameof(BootOrderMessage), nameof(StartupMessage) }) Changed(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<CheckRow> VerificationChecks { get; } = [];
    public ObservableCollection<GpuTarget> Targets { get; } = [];
    public int[] BenchmarkRoundOptions { get; } = [7, 11, 15];
    public int BenchmarkRounds { get; set; } = GemmProtocol.DefaultRounds;
    public ObservableCollection<ThroughputRow> ThroughputResults { get; } = [];
    public LanguageOption[] Languages { get; } = [new("zh-CN", "中文"), new("en-US", "English")];
    public LanguageOption SelectedLanguage
    {
        get => Languages.First(x => x.Culture == Localization.Current.Culture.Name);
        set
        {
            if (value is null || value.Culture == Localization.Current.Culture.Name) return;
            Localization.Current.SetCulture(value.Culture);
            try { Localization.Current.SavePreference(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog(T("LanguageSaveFailed", ex.Message)); }
        }
    }
    public void RefreshLanguage()
    {
        // Refresh presentation only: never rerun a probe or reset consent, selection,
        // downloaded packages, cancellation, or a protected operation on language changes.
        Changed(null);
    }
    public GpuTarget? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (selectedTarget == value) return;
            selectedTarget = value; benchmarkEvidence = null; RuntimeState = UnlockState.Unknown; RuntimeMessage = T("SelectedRuntimeNotChecked");
            ThroughputResults.Clear(); reportPath = null; BenchmarkMessage = T("SelectedBenchmarkNotRun");
            Changed(); UpdateDerived();
        }
    }
    public string TargetDescription => SelectedTarget?.Cuda is { } gpu ? T("CudaIdentity", gpu.Uuid, gpu.Major, gpu.Minor) : T("CudaNotMapped");
    public UnlockState RuntimeState { get => runtimeState; private set { runtimeState = value; Changed(); Changed(nameof(RuntimeCheck)); } }
    public CheckStatus RuntimeCheck => RuntimeState == UnlockState.Unlocked ? CheckStatus.Pass : RuntimeState == UnlockState.Locked ? CheckStatus.Fail : CheckStatus.Unknown;
    public LocalizedText RuntimeMessage { get => runtimeMessage; private set { runtimeMessage = value; Changed(); } }
    public LocalizedText BenchmarkMessage { get => benchmarkMessage; private set { benchmarkMessage = value; Changed(); } }
    public FamilyOption[] FamilyOptions { get; } = [new("ad102", T("Family40")), new("ga102", T("Family30")), new("tu10x", T("Family20"))];
    public bool RiskAccepted
    {
        get => riskAccepted;
        set { riskAccepted = value; if (!value) BootOrderRiskAccepted = false; Changed(); UpdateDerived(); if (value && !IsBusy) _ = RefreshAsync(); }
    }
    public bool IsBusy => busy;
    public bool IsTransaction => transaction;
    public bool NotBusy => !busy;
    public bool SettingsEnabled => RiskAccepted && !busy;
    public bool CustomProxyEnabled => SettingsEnabled && UseCustomProxy;
    public bool UseSystemProxy { get => proxyMode == ProxyMode.System; set { if (value) SetProxy(ProxyMode.System); } }
    public bool UseNoProxy { get => proxyMode == ProxyMode.None; set { if (value) SetProxy(ProxyMode.None); } }
    public bool UseCustomProxy { get => proxyMode == ProxyMode.Custom; set { if (value) SetProxy(ProxyMode.Custom); } }
    private void SetProxy(ProxyMode mode) { proxyMode = mode; Changed(nameof(UseSystemProxy)); Changed(nameof(UseNoProxy)); Changed(nameof(UseCustomProxy)); UpdateDerived(); }
    public string ProxyAddress { get => proxyAddress; set { proxyAddress = value; Changed(); } }
    public FamilyOption? SelectedFamily
    {
        get => selectedFamily;
        set
        {
            if (selectedFamily == value) return;
            selectedFamily = value; package = null; PackageDescription = T("FamilySelected");
            Changed(); UpdateDerived();
        }
    }
    public string Log { get => log; private set { log = value; Changed(); } }
    public LocalizedText StatusText { get => statusText; private set { statusText = value; Changed(); } }
    public LocalizedText GpuDescription { get => gpuDescription; private set { gpuDescription = value; Changed(); } }
    public LocalizedText DownloadDescription { get => downloadDescription; private set { downloadDescription = value; Changed(); } }
    public double DownloadPercent { get => downloadPercent; private set { downloadPercent = value; Changed(); } }
    public LocalizedText PackageDescription { get => packageDescription; private set { packageDescription = value; Changed(); } }
    public LocalizedText VerificationMessage { get => verificationMessage; private set { verificationMessage = value; Changed(); } }
    public bool InstalledSuccessfully { get => installedSuccessfully; private set { installedSuccessfully = value; Changed(); } }
    public bool BootOrderRiskAccepted { get => bootOrderRiskAccepted; set { bootOrderRiskAccepted = value; Changed(); UpdateDerived(); } }
    public LocalizedText BootOrderMessage => !verification.BootOrderKnown
        ? T(verification.BootOrderMessage == "尚未检查启动顺序。" ? "BootUnchecked" : "BootUnknown")
        : T(verification.UnlockFirst ? "BootFirst" : verification.UnlockInBootOrder ? "BootNotFirst" : "BootNotListed")
            + (verification.WindowsInBootOrder ? (LocalizedText)"" : T("BootMissingFallback"))
            + (verification.BootSequence.Length == 0 ? (LocalizedText)"" : T("BootSequence"));
    public string StartupMessage => T("StartupMessage", BootOrderMessage);
    public string CacheDescription => T("CachePath", cache.Root);
    public string InstallTooltip => environment.HasUnlockFiles ? T("ExistingPatchTooltip") : T("InstallTooltip");
    public ICommand RefreshCommand { get; }
    public ICommand ReopenCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand SetFirstCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenCacheCommand { get; }
    public ICommand SaveLogCommand { get; }
    public ICommand DetectRuntimeCommand { get; }
    public ICommand BenchmarkCommand { get; }
    public ICommand OpenReportCommand { get; }

    public void AppendLog(string line)
    {
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(() => AppendLog(line)); return; }
        string combined = Log + $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
        Log = combined.Length > 180000 ? combined[^150000..] : combined;
    }
    private IProgress<DownloadProgress> Progress() => new Progress<DownloadProgress>(p => {
        DownloadPercent = p.Percent;
        DownloadDescription = p.Total is > 0 ? T("DownloadProgress", p.FileName, p.Received, p.Total, p.Percent) : T("DownloadProgressUnknown", p.FileName, p.Received);
    });
    private ProxySettings Proxy() => new(proxyMode, proxyAddress);
    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        await RunAsync(T("DetectGpuTask"), false, async token =>
        {
            var raw = await backend.RunAsync<GpuInfo[]>("Gpus", AppendLog, token: token);
            var gpus = raw.Select(g => g with { Family = Families.Classify(g.Name, g.DeviceId) }).ToArray();
            GpuDescription = new(() => string.Join("\n", gpus.Select(g => $"{g.Name} · {g.Family ?? T("UnknownFamily")}")));
            if (gpus.Length == 0) GpuDescription = T("NoGpu");
            string? preferred = Families.Preferred(gpus);
            if (SelectedFamily is null && preferred is not null) SelectedFamily = FamilyOptions.First(x => x.Id == preferred);
            CudaGpu[] cuda = [];
            try { cuda = await Task.Run(CudaBenchmark.Enumerate, token); }
            catch (Exception ex) { AppendLog(T("CudaEnumerationFailed") + ex.Message); }
            foreach (var target in GpuIdentity.Map(gpus, cuda)) Targets.Add(target);
            SelectedTarget = Targets.FirstOrDefault(t => t.WindowsGpu.Family == preferred) ?? Targets.FirstOrDefault();
            AppendLog(T("GpuEnumerationComplete"));
        });
        if (RiskAccepted) await RefreshAsync();
    }
    private async Task RunAsync(LocalizedText title, bool isTransaction, Func<CancellationToken, Task> action, bool cancellable = false)
    {
        if (busy) return;
        busy = true; transaction = isTransaction; canCancel = cancellable; cancellation = new(); UpdateDerived(); StatusText = T("TaskRunning", title); AppendLog(title);
        try { await action(cancellation.Token); StatusText = T("TaskComplete", title); }
        catch (OperationCanceledException) { StatusText = T("TaskCancelled"); AppendLog(StatusText); }
        catch (Exception ex) { StatusText = T("TaskFailed", title); AppendLog(ex.Message); }
        finally { cancellation.Dispose(); cancellation = null; busy = false; transaction = false; UpdateDerived(); }
    }
    private async Task RefreshCoreAsync()
    {
        BootOrderRiskAccepted = false; SetVerification(new());
        environment = await backend.RunAsync<EnvironmentReport>("Probe", AppendLog);
        Checks.Clear();
        foreach (var c in environment.Checks) Checks.Add(new(c.Key, Diagnostic(c.Label), c.Status, Diagnostic(c.Detail), c.Key == "admin" && !environment.IsAdmin, c.Key == "clean" && environment.HasUnlockFiles));
        if (environment.UnlockFiles.Length > 0) AppendLog(T("ExistingEspFiles") + string.Join("、", environment.UnlockFiles));
        if (environment.CanRemove) SetVerification(await backend.RunAsync<VerificationReport>("Verify", AppendLog));
        UpdateDerived();
    }
    private Task RefreshAsync() => RunAsync(T("EnvironmentTask"), false, _ => RefreshCoreAsync());
    private Task DownloadAsync() => RunAsync(T("DownloadTask"), false, async token =>
    {
        dependenciesReady = false; cache.EnsureSecure();
        await downloader.PrepareReleaseAsync(Proxy(), Progress(), token);
        await downloader.VerifyExecutablesAsync(token); dependenciesReady = true;
        AppendLog(T("DependenciesVerified"));
    }, cancellable: true);
    private Task BuildAsync() => RunAsync(T("Build"), false, async token =>
    {
        package = null; PackageDescription = T("Generating");
        package = await builder.BuildAsync(SelectedFamily!.Id, Proxy(), Progress(), AppendLog, token);
        PackageDescription = T("PackageSummary", package.Manifest.Family, package.Manifest.Files.Length, package.Manifest.Files.Sum(f => f.Bytes) / 1048576.0);
    }, cancellable: true);
    private Task InstallAsync() => RunAsync(T("InstallTask"), true, async _ =>
    {
        InstalledSuccessfully = false;
        await RefreshCoreAsync();
        if (!environment.CanInstall) throw new InvalidOperationException(InstallTooltip);
        var result = await backend.RunAsync<VerificationReport>("Install", AppendLog, package!.Directory);
        SetVerification(result); await RefreshCoreAsync();
    });
    private Task RemoveAsync() => RunAsync(T("RemoveTask"), true, async _ =>
    {
        await backend.RunAsync<System.Text.Json.JsonElement>("Remove", AppendLog);
        InstalledSuccessfully = false; SetVerification(new() { Message = T("PatchRemoved") }); await RefreshCoreAsync();
    });
    private Task SetFirstAsync() => RunAsync(T("SetPriorityTask"), true, async _ =>
    {
        try { SetVerification(await backend.RunAsync<VerificationReport>("SetFirst", AppendLog)); }
        catch { SetVerification(new() { Message = T("SetPriorityFailed") }); throw; }
        finally { BootOrderRiskAccepted = false; }
    });
    private Task VerifyAsync() => RunAsync(T("Verify"), false, async _ =>
    {
        BootOrderRiskAccepted = false; SetVerification(new());
        SetVerification(await backend.RunAsync<VerificationReport>("Verify", AppendLog));
    });
    private Task DetectRuntimeAsync() => RunAsync(T("DetectRuntime"), true, async _ =>
    {
        benchmarkEvidence = null;
        BootOrderRiskAccepted = false;
        if (environment.IsAdmin && environment.EspFound)
        {
            SetVerification(new());
            try { SetVerification(await backend.RunAsync<VerificationReport>("Verify", AppendLog)); }
            catch (Exception ex) { AppendLog(T("BootConfigurationUnknown") + ex.Message); }
        }
        RuntimeState = UnlockState.Unknown; RuntimeMessage = T("DiagnosticRunning");
        try
        {
            var result = await unlockProbe.RunAsync(SelectedTarget!.WindowsGpu.DeviceId, AppendLog);
            RuntimeState = result.State;
            RuntimeMessage = result.Timestamp.ToString("HH:mm:ss") + " · " + T(result.State switch { UnlockState.Unlocked => "RuntimeUnlocked", UnlockState.Locked => "RuntimeLocked", _ => "RuntimeUnknown" })
                + (result.CleanupPassed ? (LocalizedText)"" : T("CleanupIncomplete"));
            AppendLog(result.Detail);
            if (result.CleanupPassed && result.State != UnlockState.Unknown && SelectedTarget.Cuda is { } cuda)
                benchmarkEvidence = new(result.State, cuda.Uuid, cuda.PciBusId, SelectedTarget.WindowsGpu.DeviceId, result.Timestamp);
        }
        catch (Exception ex) { RuntimeMessage = T("UnknownResult") + ex.Message; throw; }
    });
    private Task BenchmarkAsync() => RunAsync(T("GemmTask"), false, async token =>
    {
        var target = SelectedTarget!.Cuda!; ThroughputResults.Clear(); reportPath = null;
        var evidence = benchmarkEvidence;
        int rounds = BenchmarkRounds;
        GemmProtocol.ValidateRounds(rounds);
        BenchmarkMessage = T("PreparingBlas");
        var runtimeProgress = new Progress<DownloadProgress>(p => {
            BenchmarkMessage = p.Total is long total ? T("BlasProgress", p.Received / 1048576.0, total / 1048576.0) : T("BlasProgressUnknown", p.Received / 1048576.0);
        });
        try { await GemmRuntime.PrepareAsync(new(proxyMode, proxyAddress), runtimeProgress, token); }
        catch (OperationCanceledException) { BenchmarkMessage = T("BlasCancelled"); throw; }
        catch (Exception ex) { BenchmarkMessage = T("BlasFailed", ex.Message); throw; }
        BenchmarkMessage = T("BenchmarkRunning", rounds);
        string executable = Environment.ProcessPath ?? throw new IOException(T("WorkerPathUnknown"));
        string? assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(App).Assembly.Location : null;
        BenchmarkReport report;
        try { report = await new BenchmarkClient(executable, assembly).RunAsync(target.Uuid, AppendLog, token, rounds); }
        catch (OperationCanceledException) { BenchmarkMessage = T("BenchmarkCancelled"); throw; }
        catch (Exception ex) { BenchmarkMessage = T("BenchmarkFailed", ex.Message); throw; }
        if (report.PciBusId != target.PciBusId) throw new IOException(T("PciMismatch"));
        LocalizedText comparisonNote;
        try
        {
            var history = await benchmarkHistory.RecordAsync(report, evidence, token);
            report = history.Report;
            comparisonNote = T("History" + history.Status, report.BaselineTimestamp);
            AppendLog(history.Message);
        }
        catch (IOException ex) { comparisonNote = T("BaselineIoFailed", ex.Message); }
        catch (UnauthorizedAccessException ex) { comparisonNote = T("BaselineAccessFailed", ex.Message); }
        foreach (var result in report.Results) ThroughputResults.Add(new(result));
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPUUnlocker", "Reports");
        Directory.CreateDirectory(directory); reportPath = Path.Combine(directory, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, WireJson.Options), token);
        LocalizedText busyNote = report.InitialUtilizationPercent >= 25 ? T("BusyGpu", report.InitialUtilizationPercent) : "";
        BenchmarkMessage = T("BenchmarkSummary", report.Name, report.Rounds, busyNote, comparisonNote, reportPath);
    }, cancellable: true);
    private void SetVerification(VerificationReport report)
    {
        verification = report;
        VerificationChecks.Clear();
        VerificationChecks.Add(new("files", T("FilesCheck"), report.FilesValid ? CheckStatus.Pass : CheckStatus.Unknown, ""));
        VerificationChecks.Add(new("controls", T("ControlsCheck"), report.ControlsValid ? CheckStatus.Pass : CheckStatus.Unknown, ""));
        VerificationChecks.Add(new("boot", T("BootEntryCheck"), report.BootEntryValid ? CheckStatus.Pass : CheckStatus.Unknown, ""));
        VerificationChecks.Add(new("order", T("BootFirstCheck"), !report.BootOrderKnown ? CheckStatus.Unknown : report.UnlockFirst ? CheckStatus.Pass : CheckStatus.Fail, ""));
        VerificationChecks.Add(new("fallback", T("FallbackCheck"), !report.BootOrderKnown ? CheckStatus.Unknown : report.WindowsInBootOrder ? CheckStatus.Pass : CheckStatus.Fail, ""));
        VerificationMessage = report.Complete ? T("InstallationValid", BootOrderMessage) : string.IsNullOrEmpty(report.Message) ? T("InstallationNotChecked") : Diagnostic(report.Message);
        InstalledSuccessfully = report.Complete;
        UpdateDerived();
    }
    private Task ReopenAsync()
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new IOException(T("ExecutablePathUnknown"));
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(App).Assembly.Location);
            start.ArgumentList.Add("--elevated-restart");
            using var process = Process.Start(start);
            if (process is not null) Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { AppendLog(T("ElevationCancelled")); }
        catch (Exception ex) { AppendLog(T("ReopenFailed") + ex.Message); }
        return Task.CompletedTask;
    }
    private Task SaveLogAsync()
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = T("LogFilter"), FileName = $"gpu-unlocker-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, Log);
        }
        catch (Exception ex) { AppendLog(T("SaveLogFailed") + ex.Message); }
        return Task.CompletedTask;
    }
}
