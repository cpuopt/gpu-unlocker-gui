using System.Text.Json;

namespace GpuUnlocker.Core;

public static class BenchmarkWorker
{
    public const string Prefix = "GPUUNLOCKER_BENCH:";
    public static int Execute(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length is not (2 or 3) || args[0] != "--benchmark-worker" || !args[1].StartsWith("GPU-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("测速子进程参数无效。");
            int rounds = args.Length == 3 ? int.Parse(args[2]) : GemmProtocol.DefaultRounds;
            GemmProtocol.ValidateRounds(rounds);
            var initial = GetDeviceState(args[1]);
            int? utilization = initial.Utilization;
            if (utilization >= 25) Console.WriteLine($"测试前 GPU 利用率 {utilization}%；存在其他负载，结果仅供参考，不适合作前后对比。");
            var result = CudaBenchmark.Run(args[1], Console.WriteLine, rounds) with { InitialUtilizationPercent = utilization, DriverRelease = initial.Driver };
            Console.WriteLine(Prefix + JsonSerializer.Serialize(new { success = true, data = result }, CompactOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(Prefix + JsonSerializer.Serialize(new { success = false, error = ex.Message }, CompactOptions));
            return 1;
        }
    }
    private static (int? Utilization, string Driver) GetDeviceState(string uuid)
    {
        string smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(smi)) return (null, "");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = ProcessRunner.RunCapturedAsync(smi, ["--id=" + uuid, "--query-gpu=utilization.gpu,driver_version", "--format=csv,noheader,nounits"], null, deadline.Token).GetAwaiter().GetResult();
            if (result.ExitCode == 0 && result.Lines.Length == 1)
            {
                var parts = result.Lines[0].Split(',');
                if (parts.Length == 2) return (int.TryParse(parts[0].Trim(), out int value) && value is >= 0 and <= 100 ? value : null, parts[1].Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        return (null, "");
    }
    private static readonly JsonSerializerOptions CompactOptions = new(WireJson.Options) { WriteIndented = false };
    public static BenchmarkReport Parse(ProcessRunner.Captured result)
    {
        var tagged = result.Lines.Where(x => x.StartsWith(Prefix)).ToArray();
        if (tagged.Length != 1) throw new IOException("测速子进程未返回唯一结果，可能已被取消或驱动返回异常。");
        using var doc = JsonDocument.Parse(tagged[0][Prefix.Length..]);
        if (!doc.RootElement.GetProperty("success").GetBoolean()) throw new IOException(doc.RootElement.GetProperty("error").GetString());
        if (result.ExitCode != 0) throw new IOException("测速子进程未正常退出，不接纳结果。");
        var report = doc.RootElement.GetProperty("data").Deserialize<BenchmarkReport>(WireJson.Options) ?? throw new IOException("测速结果为空。");
        ValidateReport(report);
        return report;
    }
    public static void ValidateReport(BenchmarkReport report)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(report.LibrarySha256, "^[A-Fa-f0-9]{64}$") ||
            report.Method != GemmProtocol.Method || report.MatrixSize != GemmProtocol.Size ||
            report.Rounds < 3 || report.Rounds > 31 || report.Rounds % 2 == 0 ||
            report.WarmupMilliseconds != GemmProtocol.WarmupMs || report.TargetSampleMilliseconds != GemmProtocol.SampleMs ||
            report.CublasVersion <= 0 || report.RuntimeArchiveSha256 != GemmRuntime.Archive.Sha256 ||
            string.IsNullOrWhiteSpace(report.Configuration) || report.Results.Length != 4 || !report.Results.Select(r => r.Mode).Order().SequenceEqual(new[] { "BF16", "FP16", "FP32", "TF32" }))
            throw new IOException("测速结果元数据或模式不完整。");
        foreach (var row in report.Results)
        {
            bool valid = row.Status == "通过" && row.CorrectnessPassed && row.Samples.Length == report.Rounds && row.LaunchesPerSample > 0 &&
                row.Milliseconds.Length == report.Rounds && row.Milliseconds.All(x => double.IsFinite(x) && x > 0) &&
                row.Samples.Zip(row.Milliseconds).All(p => Math.Abs(p.First - GemmProtocol.Flops(report.MatrixSize, row.LaunchesPerSample) / (p.Second / 1000) / 1e12) < 0.000001) &&
                !string.IsNullOrWhiteSpace(row.Validation) && row.Samples.All(x => double.IsFinite(x) && x > 0) &&
                row.Tflops is double t && double.IsFinite(t) && Math.Abs(CudaKernels.Median(row.Samples) - t) < 0.000001;
            bool skipped = row.Status == "不支持" && row.Tflops is null && row.Samples.Length == 0 && !row.CorrectnessPassed;
            if (!valid && !skipped) throw new IOException("测速结果未通过数据和数值校验规则。");
        }
    }
}

public sealed class BenchmarkClient(string executable, string? managedAssembly = null)
{
    public async Task<BenchmarkReport> RunAsync(string uuid, Action<string> log, CancellationToken token, int rounds = GemmProtocol.DefaultRounds)
    {
        var args = new List<string>();
        if (managedAssembly is not null) args.Add(managedAssembly);
        GemmProtocol.ValidateRounds(rounds);
        args.Add("--benchmark-worker"); args.Add(uuid); args.Add(rounds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(360));
        ProcessRunner.Captured captured;
        try { captured = await ProcessRunner.RunCapturedAsync(executable, args, line => { if (!line.StartsWith(BenchmarkWorker.Prefix)) log(line); }, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("GEMM 测速超时，已终止测速进程并释放其 CUDA 上下文。"); }
        var report = BenchmarkWorker.Parse(captured);
        if (report.Rounds != rounds) throw new IOException("返回结果轮次与请求不一致。");
        if (!report.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)) throw new IOException("返回结果 UUID 与选定 GPU 不一致。");
        return report;
    }
}
