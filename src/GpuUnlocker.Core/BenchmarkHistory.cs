using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GpuUnlocker.Core;

public sealed record BenchmarkStateEvidence(UnlockState State, string Uuid, string PciBusId, string DeviceId, DateTimeOffset CheckedAt);
public enum BenchmarkHistoryStatus { UnknownState, Busy, UnknownDriver, BaselineSaved, BaselineMissing, BaselineInvalid, Compared }
public sealed record BenchmarkHistoryResult(BenchmarkReport Report, string Message)
{
    public BenchmarkHistoryStatus Status { get; init; }
}

public sealed class BenchmarkHistory(string? directory = null)
{
    private readonly string root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPUUnlocker", "Baselines");

    // Include the complete measurement protocol. A new driver, library or round count requires its own baseline.
    public static string Key(BenchmarkReport report)
    {
        string config = JsonSerializer.Serialize(new {
            Uuid = report.Uuid.ToLowerInvariant(), Pci = report.PciBusId.ToLowerInvariant(), report.Architecture,
            report.Method, report.DriverVersion, report.DriverRelease, report.CublasVersion, report.LibrarySha256,
            report.RuntimeArchiveSha256, report.MatrixSize, report.Rounds, report.WarmupMilliseconds,
            report.TargetSampleMilliseconds, report.Configuration
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
    }
    public async Task<BenchmarkHistoryResult> RecordAsync(BenchmarkReport report, BenchmarkStateEvidence? evidence, CancellationToken token = default)
    {
        BenchmarkWorker.ValidateReport(report);
        report = report with { StateEvidence = evidence };
        if (evidence is null || evidence.State == UnlockState.Unknown || evidence.CheckedAt > report.Timestamp ||
            !evidence.Uuid.Equals(report.Uuid, StringComparison.OrdinalIgnoreCase) ||
            !evidence.PciBusId.Equals(report.PciBusId, StringComparison.OrdinalIgnoreCase))
            return new(report with { StateEvidence = null }, "未取得所选 GPU 的有效解锁状态检测，本次仅保存普通测速报告，不建立或比较基线。") { Status = BenchmarkHistoryStatus.UnknownState };
        if (report.InitialUtilizationPercent >= 25)
            return new(report, "测试前 GPU 存在其他负载，本次不更新未解锁基线，也不计算提升比例。") { Status = BenchmarkHistoryStatus.Busy };
        if (string.IsNullOrWhiteSpace(report.DriverRelease))
            return new(report, "无法读取 NVIDIA 驱动版本，本次不建立或比较基线，避免混用不同驱动的成绩。") { Status = BenchmarkHistoryStatus.UnknownDriver };
        string path = Path.Combine(root, Key(report) + ".json");
        if (evidence.State == UnlockState.Locked)
        {
            Directory.CreateDirectory(root);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, WireJson.Options), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(report, "已保存本 GPU、当前测试配置的未解锁性能基线；重启并检测为已解锁后，以相同轮次测速即可显示提升比例。") { Status = BenchmarkHistoryStatus.BaselineSaved };
        }
        if (!File.Exists(path)) return new(report, "没有同 GPU、同驱动与同测试配置的未解锁基线；请先在未解锁状态检测并完成测速。") { Status = BenchmarkHistoryStatus.BaselineMissing };
        BenchmarkReport baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(path, token), WireJson.Options)
                ?? throw new InvalidDataException("基线为空。");
            BenchmarkWorker.ValidateReport(baseline);
            if (Key(baseline) != Key(report) || baseline.StateEvidence is not { State: UnlockState.Locked } old ||
                !old.Uuid.Equals(report.Uuid, StringComparison.OrdinalIgnoreCase) ||
                !old.PciBusId.Equals(report.PciBusId, StringComparison.OrdinalIgnoreCase) ||
                !old.DeviceId.Equals(evidence.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                old.CheckedAt > baseline.Timestamp || baseline.Timestamp >= report.Timestamp || baseline.InitialUtilizationPercent >= 25)
                throw new InvalidDataException("基线设备、状态证据或测试配置不匹配。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NullReferenceException)
        { return new(report, "未解锁基线损坏或不匹配，未计算提升比例：" + ex.Message) { Status = BenchmarkHistoryStatus.BaselineInvalid }; }
        var rows = report.Results.Select(row =>
        {
            var previous = baseline.Results.Single(b => b.Mode == row.Mode);
            return row.Status == "通过" && previous.Status == "通过" && previous.Tflops is double before && before > 0 && row.Tflops is double after
                ? row with { LockedTflops = before, ImprovementPercent = (after / before - 1) * 100 }
                : row;
        }).ToArray();
        return new(report with { Results = rows, BaselineTimestamp = baseline.Timestamp },
            $"已对比未解锁基线（{baseline.Timestamp:yyyy-MM-dd HH:mm:ss}）；提升 = (本次 / 未解锁 − 1) × 100%。负值表示性能下降。") { Status = BenchmarkHistoryStatus.Compared };
    }
}
