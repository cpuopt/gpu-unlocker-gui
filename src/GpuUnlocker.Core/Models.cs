using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GpuUnlocker.Core;

public enum CheckStatus { Pass, Fail, Unknown }
public sealed record CheckResult(string Key, string Label, CheckStatus Status, string Detail);
public sealed record EnvironmentReport
{
    public bool IsAdmin { get; init; }
    public bool Gpt { get; init; }
    public bool EspFound { get; init; }
    public bool EspWritable { get; init; }
    public bool? SecureBootEnabled { get; init; }
    public bool HasUnlockFiles { get; init; }
    public string[] UnlockFiles { get; init; } = [];
    public string InstallationKind { get; init; } = "未知";
    public CheckResult[] Checks { get; init; } = [];
    public bool CanInstall => IsAdmin && Gpt && EspFound && EspWritable && SecureBootEnabled == false && !HasUnlockFiles;
    public bool CanRemove => IsAdmin && EspWritable && HasUnlockFiles;
}
public sealed record GpuInfo(string Name, string DeviceId, string? Family, string? PciBusId = null);
public sealed record VerificationReport
{
    public bool FilesValid { get; init; }
    public bool ControlsValid { get; init; }
    public bool BootEntryValid { get; init; }
    public bool BootOrderKnown { get; init; }
    public bool UnlockInBootOrder { get; init; }
    public bool UnlockFirst { get; init; }
    public bool WindowsInBootOrder { get; init; }
    public string[] FirmwareOrder { get; init; } = [];
    public string[] BootSequence { get; init; } = [];
    public string BootOrderMessage { get; init; } = "尚未检查启动顺序。";
    public string Message { get; init; } = "";
    public bool Complete => FilesValid && ControlsValid && BootEntryValid;
    public bool CanSetFirst => Complete && BootOrderKnown && (!UnlockFirst || !WindowsInBootOrder);
}
public sealed record FileEntry(string Path, long Bytes, string Sha256);
public static class BootPriorityGate
{
    public static bool CanExecute(VerificationReport installation, bool appConsent, bool priorityConsent, bool busy, bool admin, bool writable)
        => appConsent && priorityConsent && !busy && admin && writable && installation.CanSetFirst;
}
public sealed record PatchManifest(int Schema, string Mode, string Family, string Release, FileEntry[] Files);
public sealed record PatchPackage(string Directory, PatchManifest Manifest);
public sealed record DownloadProgress(string FileName, long Received, long? Total)
{
    public double Percent => Total is > 0 ? Received * 100.0 / Total.Value : 0;
    public string Description => Total is > 0
        ? $"{FileName}：{Received:N0} / {Total:N0} 字节（{Percent:F1}%）"
        : $"{FileName}：{Received:N0} 字节";
}
public enum ProxyMode { System, None, Custom }
public sealed record ProxySettings(ProxyMode Mode, string Address = "");
public sealed record DownloadAsset(string Name, string Url, string Sha256);

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class Families
{
    public static readonly string[] All = ["ad102", "ga102", "tu10x"];
    public static string? Classify(string name, string pnpId)
    {
        if (!pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return null;
        if (Regex.IsMatch(name, @"\bRTX\s*40\d{2}\b", RegexOptions.IgnoreCase)) return "ad102";
        if (Regex.IsMatch(name, @"\bRTX\s*30\d{2}\b", RegexOptions.IgnoreCase)) return "ga102";
        if (Regex.IsMatch(name, @"\b(?:RTX\s*20|GTX\s*16)\d{2}\b", RegexOptions.IgnoreCase)) return "tu10x";
        // Exact upstream device ranges: reject other vendors and unrecognised generations.
        var match = Regex.Match(pnpId, @"DEV_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        int device = Convert.ToInt32(match.Groups[1].Value, 16);
        if (device is >= 0x2680 and <= 0x28ff) return "ad102";
        if (device is >= 0x2200 and <= 0x22ff or >= 0x2400 and <= 0x25ff) return "ga102";
        if (device is >= 0x1e02 and <= 0x1fff or >= 0x2182 and <= 0x21d1) return "tu10x";
        return null;
    }
    public static string? Preferred(IEnumerable<GpuInfo> gpus) => All.FirstOrDefault(f => gpus.Any(g => g.Family == f));
    public static string[] Assets(string family)
    {
        if (!All.Contains(family)) throw new ArgumentException("未知 GPU 家族。");
        return [$"booter_load_{family}.bin", $"gsp_boot_desc_{family}.bin", $"gsp_boot_{family}.bin",
            $"fwimage_{family}.bin", $"fwsignature_{family}.bin",
            family == "tu10x" ? "sec2_bl_tu10x.bin" : "scrubber_ad10x.bin", $"v67-{family}-four-plm.bin"];
    }
    public static string[] DeploymentPaths(string family) => ["EFI/GPUUnlock/BOOTX64.EFI", .. Assets(family), "i_know_what_i_am_doing.txt", "chain-target.txt"];
}
