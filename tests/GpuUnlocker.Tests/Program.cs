using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GpuUnlocker.Core;

if (args.FirstOrDefault() == "--benchmark-worker") return BenchmarkWorker.Execute(args);
if (args.FirstOrDefault() == "--prepare-gemm")
{
    await GemmRuntime.PrepareAsync(new(ProxyMode.None), null, CancellationToken.None);
    Console.WriteLine("PASS pinned archive extraction and DLL verification: " + GemmRuntime.Verify());
    return 0;
}
if (args.FirstOrDefault() == "--gemm-cancellation")
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string executable = Environment.ProcessPath!;
    string? assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? System.Reflection.Assembly.GetExecutingAssembly().Location : null;
    bool measured = false;
    try
    {
        await new BenchmarkClient(executable, assembly).RunAsync(CudaBenchmark.Enumerate().First().Uuid, line =>
        {
            Console.WriteLine(line);
            if (line.Contains("第 1/7 轮")) { measured = true; cancel.Cancel(); }
        }, cancel.Token);
        throw new Exception("Canceled benchmark unexpectedly returned an accepted report.");
    }
    catch (OperationCanceledException) when (measured)
    { Console.WriteLine("PASS cancel running GEMM worker without accepting a partial report."); return 0; }
}
if (args.FirstOrDefault() == "--gemm-integration")
{
    await GemmRuntime.PrepareAsync(new(ProxyMode.None), null, CancellationToken.None);
    var gpu = CudaBenchmark.Enumerate().First();
    string executable = Environment.ProcessPath!;
    string? assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? System.Reflection.Assembly.GetExecutingAssembly().Location : null;
    if (args.Length > 2) { executable = Path.GetFullPath(args[2]); assembly = null; }
    var report = await new BenchmarkClient(executable, assembly).RunAsync(gpu.Uuid, Console.WriteLine, CancellationToken.None);
    string output = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/gemm-validation.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(report, WireJson.Options));
    Console.WriteLine($"PASS actual GEMM worker + JSON validation: {output}");
    return 0;
}

int count = 0;
string root = Path.Combine(Path.GetTempPath(), "gpu-unlocker-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
async Task Reject(Func<Task> action, string message)
{
    bool rejected = false;
    try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OperationCanceledException) { rejected = true; }
    Assert(rejected, message);
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
try
{
    string pnp = @"PCI\VEN_10DE&DEV_2805\UNIQUE-A";
    string Header(string device) => $"===== GPU 1/1: {device} =====";
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Ada]: UNLOCKED (override values live)"], pnp, 0) == UnlockState.Unlocked, "ssdiag unlocked");
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Ada]: NOT_UNLOCKED"], pnp, 0) == UnlockState.Locked, "NOT_UNLOCKED is not UNLOCKED");
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Ada]: \u001b[92mUNLOCKED\u001b[0m"], pnp, 0) == UnlockState.Unlocked, "ANSI output");
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Ada]: UNLOCKED"], pnp, 2) == UnlockState.Unknown, "Exit failure not a verdict");
    Assert(SsdiagOutput.Parse([Header(pnp + "-OTHER"), "VERDICT [Ada]: UNLOCKED"], pnp, 0) == UnlockState.Unknown, "Wrong same-model device");
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Unknown]: family recognized - FEAT addresses not mapped for this family"], pnp, 0) == UnlockState.Unknown, "Unsupported family");
    Assert(SsdiagOutput.Parse([Header(pnp), "BAR0 reads all zeros"], pnp, 0) == UnlockState.Unknown, "Degenerate register read");
    Assert(SsdiagOutput.Parse([Header(pnp), "VERDICT [Ada]: UNLOCKED", Header(pnp), "VERDICT [Ada]: NOT_UNLOCKED"], pnp, 0) == UnlockState.Unknown, "Ambiguous cards");
    Assert(SsdiagOutput.Parse(["log mentions UNLOCKED"], pnp, 0) == UnlockState.Unknown, "Loose text cannot fake verdict");
    foreach (int arch in new[] { 75, 80, 86, 89 })
    {
        var kernel = CudaKernels.Load(arch);
        Assert(kernel.Bytes.Length > 1000 && kernel.Bytes[0] == 0x7f && kernel.Bytes[1] == (byte)'E', "Embedded ELF cubin and digest");
    }
    Assert(CudaKernels.Supported(75, "FP16") && !CudaKernels.Supported(75, "BF16") && !CudaKernels.Supported(75, "TF32"), "Turing modes");
    Assert(!CudaKernels.Supported(90, "FP16") && !CudaKernels.Supported(89, "INVALID"), "Unsupported mode/architecture");
    Assert(CudaKernels.Flops(1, 32, 1, 89, "FP16") == 32768 && CudaKernels.Flops(1, 32, 1, 89, "BF16") == 32768, "MMA operation count");
    Assert(CudaKernels.Flops(1, 32, 1, 89, "TF32") == 16384 && CudaKernels.Flops(1, 32, 1, 75, "FP16") == 16384, "TF32/SM75 operation count");
    Assert(CudaKernels.Median([20, 10, 30]) == 20, "Median statistics");
    Assert(CudaKernels.NormalizePci("00000000:01:00.0") == "0000:01:00.0", "PCI identity mapping");
    var cardA = new GpuInfo("Same model", @"PCI\VEN_10DE&A", "ad102", "0000:01:00.0");
    var cardB = new GpuInfo("Same model", @"PCI\VEN_10DE&B", "ad102", "0000:02:00.0");
    CudaGpu[] reversed = [new(0, "GPU-B", "Same model", "0000:02:00.0", 8, 9, 34), new(1, "GPU-A", "Same model", "0000:01:00.0", 8, 9, 34)];
    var mapped = GpuIdentity.Map([cardA, cardB], reversed);
    Assert(mapped[0].Cuda?.Uuid == "GPU-A" && mapped[1].Cuda?.Uuid == "GPU-B", "Same-model devices mapped independently of ordinal");
    Assert(GpuIdentity.Map([cardA, cardB with { PciBusId = cardA.PciBusId }], reversed).All(g => g.Cuda is null), "Ambiguous PnP addresses rejected");
    Assert(GpuIdentity.Map([cardA with { PciBusId = null }], reversed)[0].Cuda is null, "Unknown address not guessed by model name");
    double[] times = [1000, 1001, 1002, 1003, 1004, 1005, 1006];
    double[] rates = times.Select(ms => GemmProtocol.Flops(8192, 20) / (ms / 1000) / 1e12).ToArray();
    var sampleReport = new BenchmarkReport("GPU-fixture", "0000:01:00.0", "GPU", "sm_89", new string('A', 64), 12080, DateTimeOffset.Now,
        GemmProtocol.Modes.Select(m => new ThroughputResult(m, "通过", CudaKernels.Median(rates), "", rates, true)
        { Milliseconds = times, LaunchesPerSample = 20, Validation = "reference dot products" }).ToArray())
    { Method = GemmProtocol.Method, Rounds = 7, MatrixSize = 8192, WarmupMilliseconds = 2000,
        TargetSampleMilliseconds = 1000, CublasVersion = 120804, RuntimeArchiveSha256 = GemmRuntime.Archive.Sha256, Configuration = "fixture", DriverRelease = "610.88" };
    string Envelope(BenchmarkReport report) => BenchmarkWorker.Prefix + System.Text.Json.JsonSerializer.Serialize(new { success = true, data = report }, new System.Text.Json.JsonSerializerOptions(WireJson.Options) { WriteIndented = false });
    Assert(BenchmarkWorker.Parse(new(0, [Envelope(sampleReport)])).Uuid == sampleReport.Uuid, "Worker JSON protocol");
    await Reject(() => { BenchmarkWorker.Parse(new(7, [Envelope(sampleReport)])); return Task.CompletedTask; }, "Worker exit failure");
    await Reject(() => { BenchmarkWorker.Parse(new(0, [Envelope(sampleReport with { Results = [sampleReport.Results[0]] })])); return Task.CompletedTask; }, "Partial modes rejected");
    await Reject(() => { BenchmarkWorker.Parse(new(0, [Envelope(sampleReport with { Results = sampleReport.Results.Select(r => r with { CorrectnessPassed = false }).ToArray() })])); return Task.CompletedTask; }, "Failed numeric check rejected");
    Assert(GemmProtocol.DefaultRounds == 7 && GemmProtocol.Flops(8192, 1) == 1099511627776.0, "Dense GEMM FMA count");
    Assert(GemmProtocol.Supported(75, "FP32") && GemmProtocol.Supported(75, "FP16") && !GemmProtocol.Supported(75, "BF16") && !GemmProtocol.Supported(75, "TF32"), "GEMM Turing hardware modes");
    foreach (int rounds in new[] { 0, 2, 4, 32, 99 })
        await Reject(() => { GemmProtocol.ValidateRounds(rounds); return Task.CompletedTask; }, "Invalid round count rejected");
    await Reject(() => { BenchmarkWorker.Parse(new(0, [Envelope(sampleReport with { Rounds = 3 })])); return Task.CompletedTask; }, "Round/sample mismatch rejected");
    await Reject(() => { BenchmarkWorker.Parse(new(0, [Envelope(sampleReport with { Method = "register-mma" })])); return Task.CompletedTask; }, "Legacy microbenchmark not accepted as GEMM");
    await Reject(() => { BenchmarkWorker.Parse(new(0, [Envelope(sampleReport with { Results = sampleReport.Results.Select(r => r with { Milliseconds = [1, 1, 1, 1, 1, 1, 1] }).ToArray() })])); return Task.CompletedTask; }, "Inflated FLOPS inconsistent with raw timing rejected");
    string baselineDirectory = Path.Combine(root, "baselines");
    var history = new BenchmarkHistory(baselineDirectory);
    var lockedEvidence = new BenchmarkStateEvidence(UnlockState.Locked, sampleReport.Uuid, sampleReport.PciBusId, pnp, sampleReport.Timestamp.AddSeconds(-1));
    await history.RecordAsync(sampleReport, null);
    Assert(!Directory.Exists(baselineDirectory), "Unknown state cannot create a locked baseline");
    await history.RecordAsync(sampleReport, lockedEvidence with { Uuid = "GPU-other" });
    Assert(!Directory.Exists(baselineDirectory), "Another GPU's detection cannot create a baseline");
    await history.RecordAsync(sampleReport with { InitialUtilizationPercent = 80 }, lockedEvidence);
    Assert(!Directory.Exists(baselineDirectory), "Busy GPU cannot overwrite baseline");
    await history.RecordAsync(sampleReport, lockedEvidence);
    Assert(Directory.GetFiles(baselineDirectory, "*.json").Length == 1, "Locked baseline persisted");
    var unlockedReport = sampleReport with { Timestamp = sampleReport.Timestamp.AddMinutes(2), Results = sampleReport.Results.Select(r => r with {
        Tflops = r.Tflops * 2, Samples = r.Samples.Select(x => x * 2).ToArray(), Milliseconds = r.Milliseconds.Select(x => x / 2).ToArray()
    }).ToArray() };
    var unlockedEvidence = lockedEvidence with { State = UnlockState.Unlocked, CheckedAt = sampleReport.Timestamp.AddMinutes(1) };
    // New store instance simulates a restart; the detected state itself is never restored from disk.
    var compared = await new BenchmarkHistory(baselineDirectory).RecordAsync(unlockedReport, unlockedEvidence);
    Assert(compared.Report.Results.All(r => r.LockedTflops == CudaKernels.Median(rates) && Math.Abs(r.ImprovementPercent!.Value - 100) < 1e-9), "Persistent baseline produces 100 percent uplift for 2x throughput");
    Assert(compared.Report.BaselineTimestamp == sampleReport.Timestamp, "Comparison records baseline timestamp");
    var slower = unlockedReport with { Results = sampleReport.Results.Select(r => r with {
        Tflops = r.Tflops / 2, Samples = r.Samples.Select(x => x / 2).ToArray(), Milliseconds = r.Milliseconds.Select(x => x * 2).ToArray()
    }).ToArray() };
    Assert((await history.RecordAsync(slower, unlockedEvidence)).Report.Results.All(r => r.ImprovementPercent == -50), "Negative uplift is displayed without clamping");
    foreach (var incompatible in new[] { unlockedReport with { DriverRelease = "611.00" }, unlockedReport with { Configuration = "different accumulation" }, unlockedReport with { Uuid = "GPU-different" } })
    {
        var evidence = unlockedEvidence with { Uuid = incompatible.Uuid };
        Assert((await history.RecordAsync(incompatible, evidence)).Report.Results.All(r => r.LockedTflops is null), "Incompatible GPU/driver/protocol not compared");
    }
    var threeRounds = unlockedReport with { Rounds = 3, Results = unlockedReport.Results.Select(r => r with { Samples = r.Samples.Take(3).ToArray(), Milliseconds = r.Milliseconds.Take(3).ToArray(), Tflops = CudaKernels.Median(r.Samples.Take(3)) }).ToArray() };
    Assert((await history.RecordAsync(threeRounds, unlockedEvidence)).Report.Results.All(r => r.LockedTflops is null), "Different round counts require a compatible baseline");
    await File.WriteAllTextAsync(Path.Combine(baselineDirectory, BenchmarkHistory.Key(sampleReport) + ".json"), "corrupt baseline");
    Assert((await history.RecordAsync(unlockedReport, unlockedEvidence)).Report.Results.All(r => r.LockedTflops is null), "Damaged baseline is ignored without inventing uplift");
    using (var canceledHistory = new CancellationTokenSource())
    {
        canceledHistory.Cancel();
        await Reject(() => history.RecordAsync(sampleReport, lockedEvidence, canceledHistory.Token), "Canceled baseline save is not published");
        Assert(Directory.GetFiles(baselineDirectory, "*.part").Length == 0, "Atomic baseline save cleans partial files");
    }
    Assert(Families.Classify("NVIDIA GeForce RTX 4060 Ti", "PCI\\VEN_10DE&DEV_2805") == "ad102", "4060 Ti classification");
    Assert(Families.Classify("NVIDIA GeForce RTX 3090", "PCI\\VEN_10DE&DEV_2204") == "ga102", "3090 classification");
    Assert(Families.Classify("NVIDIA GeForce GTX 1660", "PCI\\VEN_10DE&DEV_2184") == "tu10x", "1660 classification");
    Assert(Families.Classify("NVIDIA GeForce RTX 5090", "PCI\\VEN_10DE&DEV_2B85") is null, "Unsupported GPU must not be mapped");
    Assert(Families.Classify("RTX 4060", "PCI\\VEN_1002&DEV_2805") is null, "Vendor ID required");
    Assert(Families.Preferred([new("2070", "", "tu10x"), new("3090", "", "ga102"), new("4060", "", "ad102")]) == "ad102", "Mixed GPU priority");
    foreach (string family in Families.All)
    {
        var assets = Families.Assets(family);
        Assert(assets.Length == 7 && assets.Any(a => a == $"v67-{family}-four-plm.bin"), "Required family payload");
        Assert(Families.DeploymentPaths(family).Length == 10, "Exactly ten deployment files");
        Assert(assets.Contains(family == "tu10x" ? "sec2_bl_tu10x.bin" : "scrubber_ad10x.bin"), "Family-specific SEC2 resource");
    }
    using (var handler = DependencyDownloader.CreateHandler(new(ProxyMode.System))) Assert(handler.UseProxy && handler.Proxy is null, "System proxy");
    using (var handler = DependencyDownloader.CreateHandler(new(ProxyMode.None))) Assert(!handler.UseProxy, "No proxy");
    foreach (string address in new[] { "http://127.0.0.1:7890", "socks5://127.0.0.1:1080" })
        using (var handler = DependencyDownloader.CreateHandler(new(ProxyMode.Custom, address))) Assert(handler.UseProxy && handler.Proxy is not null, "Custom proxy");
    await Reject(() => { using var h = DependencyDownloader.CreateHandler(new(ProxyMode.Custom, "not-a-proxy")); return Task.CompletedTask; }, "Invalid proxy rejected");
    var environment = new EnvironmentReport { IsAdmin = true, Gpt = true, EspFound = true, EspWritable = true, SecureBootEnabled = false };
    Assert(environment.CanInstall, "Valid environment");
    Assert(!(environment with { HasUnlockFiles = true }).CanInstall, "Residue blocks install");
    Assert(!(environment with { SecureBootEnabled = null }).CanInstall, "Unknown Secure Boot blocks install");
    Assert(!(environment with { EspWritable = false }).CanInstall, "ESP permission failure blocks install");
    var priorityReady = new VerificationReport { FilesValid = true, ControlsValid = true, BootEntryValid = true, BootOrderKnown = true, WindowsInBootOrder = true };
    Assert(!BootPriorityGate.CanExecute(priorityReady, true, false, false, true, true), "Unchecked priority checkbox must keep button disabled even with a valid installation");
    Assert(BootPriorityGate.CanExecute(priorityReady, true, true, false, true, true), "Acknowledged valid priority action can be enabled");
    Assert(!BootPriorityGate.CanExecute(priorityReady, true, true, true, true, true), "Priority action disabled during another operation");
    Assert(!BootPriorityGate.CanExecute(priorityReady, true, true, false, false, true), "Priority action requires administrator permissions");
    Assert(!BootPriorityGate.CanExecute(priorityReady with { BootOrderKnown = false }, true, true, false, true, true), "Unknown order cannot unlock priority button");
    Assert(!BootPriorityGate.CanExecute(priorityReady with { FilesValid = false }, true, true, false, true, true), "Invalid installation cannot unlock priority button");
    Assert(!BootPriorityGate.CanExecute(priorityReady with { UnlockFirst = true }, true, true, false, true, true), "Already-first state does not need another write");
    Assert(BootPriorityGate.CanExecute(priorityReady with { UnlockFirst = true, WindowsInBootOrder = false }, true, true, false, true, true), "Missing fallback can be repaired even when unlock is already first");

    byte[] body = Encoding.UTF8.GetBytes("verified release payload");
    var asset = new DownloadAsset("fixture.zip", "https://example.invalid/fixture", Hash(body));
    string destination = Path.Combine(root, "fixture.zip");
    using var http = new HttpClient(new FakeHandler(body));
    await DependencyDownloader.DownloadWithClientAsync(http, asset, destination);
    Assert(File.ReadAllBytes(destination).SequenceEqual(body), "Downloaded bytes");
    using var offline = new HttpClient(new FakeHandler(body, HttpStatusCode.ServiceUnavailable));
    await DependencyDownloader.DownloadWithClientAsync(offline, asset, destination);
    Assert(File.Exists(destination), "Verified cache usable offline");
    await File.WriteAllTextAsync(destination, "corrupt cache");
    await DependencyDownloader.DownloadWithClientAsync(http, asset, destination);
    await FileIntegrity.RequireAsync(destination, asset.Sha256);
    count++;
    await Reject(() => DependencyDownloader.DownloadWithClientAsync(http, asset with { Sha256 = new string('0', 64) }, Path.Combine(root, "bad.zip")), "Wrong digest rejected");
    Assert(!File.Exists(Path.Combine(root, "bad.zip")) && Directory.GetFiles(root, "*.part").Length == 0, "Unverified download not published");
    await Reject(() => DependencyDownloader.DownloadWithClientAsync(offline, asset, Path.Combine(root, "retry.zip")), "HTTP failure reported");
    await DependencyDownloader.DownloadWithClientAsync(http, asset, Path.Combine(root, "retry.zip")); count++;
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await Reject(() => DependencyDownloader.DownloadWithClientAsync(http, asset, Path.Combine(root, "cancel.zip"), token: canceled.Token), "Download cancellation");
    }

    string validZip = Path.Combine(root, "good.zip");
    using (var zip = ZipFile.Open(validZip, ZipArchiveMode.Create))
    { using var writer = new StreamWriter(zip.CreateEntry("nested/tool.txt").Open()); writer.Write("tool"); }
    string unpacked = Path.Combine(root, "unpacked");
    await DependencyDownloader.ExtractVerifiedAsync(validZip, unpacked);
    Assert(File.Exists(Path.Combine(unpacked, "nested", "tool.txt")), "ZIP extraction");
    foreach (string badName in new[] { "../outside.txt", "C:/outside.txt", "nested/../../outside.txt" })
    {
        string zipPath = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(badName).Open()); writer.Write("bad"); }
        await Reject(() => DependencyDownloader.ExtractVerifiedAsync(zipPath, Path.Combine(root, "escape")), "ZIP traversal rejected");
    }
    Assert(!File.Exists(Path.Combine(root, "outside.txt")), "No ZIP escape");
    using (var cancel = new CancellationTokenSource(200))
        await Reject(() => ProcessRunner.RunAsync(WindowsBackend.PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 20"], null, cancel.Token), "Process cancellation");
    await Reject(() => ProcessRunner.RunAsync(WindowsBackend.PowerShell, ["-NoProfile", "-Command", "exit 7"], null), "Process failure");

    if (Environment.GetEnvironmentVariable("GPUUNLOCKER_RELEASE_ZIP") is { Length: > 0 } realZip &&
        Environment.GetEnvironmentVariable("GPUUNLOCKER_INPUTS") is { Length: > 0 } realInputs)
    {
        var cache = new CacheLayout(Path.Combine(root, "integration"));
        Directory.CreateDirectory(Path.GetDirectoryName(cache.ReleaseZip)!);
        Directory.CreateDirectory(cache.Inputs);
        File.Copy(realZip, cache.ReleaseZip);
        foreach (var input in ReleaseCatalog.Inputs) File.Copy(Path.Combine(realInputs, input.Name), Path.Combine(cache.Inputs, input.Name));
        var downloader = new DependencyDownloader(cache);
        await downloader.PrepareReleaseAsync(new(ProxyMode.None), null, CancellationToken.None);
        await downloader.VerifyExecutablesAsync(CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(cache.ReleaseDirectory, "make-usbroot.ps1"), "\n# modified cache\n");
        await Reject(() => downloader.VerifyExecutablesAsync(CancellationToken.None), "Modified executable cache rejected");
        await downloader.PrepareReleaseAsync(new(ProxyMode.None), null, CancellationToken.None);
        var builder = new PatchBuilder(cache, downloader, new WindowsBackend());
        foreach (string family in new[] { "ad102", "ga102" })
        {
            var built = await builder.BuildAsync(family, new(ProxyMode.None), null, Console.WriteLine, CancellationToken.None);
            Assert(built.Manifest.Files.Length == 10 && built.Manifest.Family == family, "Real upstream package generation");
            foreach (var file in built.Manifest.Files) await FileIntegrity.RequireAsync(FileIntegrity.SafePath(built.Directory, file.Path), file.Sha256);
            Assert((await File.ReadAllTextAsync(Path.Combine(built.Directory, "chain-target.txt"))).Trim() == @"\EFI\Microsoft\Boot\bootmgfw.efi", "Real chain target");
        }
        Console.WriteLine("PASS real v2.1.5 ZIP, executable-cache verification and AD102/GA102 generation (no ESP operations).");
    }
    if (Environment.GetEnvironmentVariable("GPUUNLOCKER_SSDIAG_ZIP") is { Length: > 0 } ssdiagZip)
    {
        var cache = new CacheLayout(Path.Combine(root, "ssdiag-help"));
        Directory.CreateDirectory(Path.GetDirectoryName(cache.ReleaseZip)!);
        File.Copy(ssdiagZip, cache.ReleaseZip);
        var dependency = new DependencyDownloader(cache);
        await dependency.PrepareReleaseAsync(new(ProxyMode.None), null, CancellationToken.None);
        await dependency.VerifyExecutablesAsync(CancellationToken.None);
        var help = await ProcessRunner.RunCapturedAsync(Path.Combine(cache.ReleaseDirectory, "ssdiag-x86_64-windows.exe"), ["--help"], null);
        Assert(help.ExitCode == 0 && help.Lines.Any(l => l.Contains("--select")), "Verified upstream ssdiag help contract (no driver load)");
    }
    if (Environment.GetEnvironmentVariable("GPUUNLOCKER_DEVICE_ENUM_TEST") == "1")
    {
        var cuda = CudaBenchmark.Enumerate();
        var windows = await new WindowsBackend().RunAsync<GpuInfo[]>("Gpus");
        foreach (var device in cuda)
        {
            int matches = windows.Count(g => g.PciBusId is not null && CudaKernels.NormalizePci(g.PciBusId) == device.PciBusId);
            Assert(matches == 1, "PnP-to-CUDA identity must be unique");
        }
        Console.WriteLine($"PASS actual device mapping: {cuda.Length} CUDA GPU(s), unique PnP/PCI mapping. No compute or register probe.");
    }
    var output = await ProcessRunner.RunAsync(WindowsBackend.PowerShell,
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "EspWorkflow.Tests.ps1"), "-Scripts", Path.Combine(AppContext.BaseDirectory, "Scripts")], Console.WriteLine);
    Assert(output.Any(x => x.StartsWith("PASS ESP")), "PowerShell deployment workflow tests completed");
    Console.WriteLine($"PASS: {count} C# checks. Temporary fixtures: {root}");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed class FakeHandler(byte[] content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(content) });
    }
}
