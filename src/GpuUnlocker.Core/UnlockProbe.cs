using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace GpuUnlocker.Core;

public enum UnlockState { Unknown, Locked, Unlocked }
public sealed record UnlockProbeReport(UnlockState State, string Detail, bool CleanupPassed, DateTimeOffset Timestamp);

public static class SsdiagOutput
{
    public static UnlockState Parse(IEnumerable<string> output, string pnpId, int exitCode)
    {
        if (exitCode != 0) return UnlockState.Unknown;
        string text = Regex.Replace(string.Join("\n", output), @"\x1b\[[0-?]*[ -/]*[@-~]", "");
        var headers = Regex.Matches(text, @"(?m)^===== GPU \d+/\d+: (.+?) =====\s*$");
        if (headers.Count != 1 || !headers[0].Groups[1].Value.Trim().Equals(pnpId, StringComparison.OrdinalIgnoreCase)) return UnlockState.Unknown;
        var verdicts = Regex.Matches(text, @"(?m)^VERDICT \[[^\]\r\n]+\]: (UNLOCKED|NOT_UNLOCKED)\b");
        if (verdicts.Count != 1) return UnlockState.Unknown;
        return verdicts[0].Groups[1].Value == "UNLOCKED" ? UnlockState.Unlocked : UnlockState.Locked;
    }
}

public sealed class UnlockProbe(CacheLayout cache, DependencyDownloader downloader)
{
    public async Task<UnlockProbeReport> RunAsync(string pnpId, Action<string> log)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException("ssdiag 检测需要管理员权限。");
        if (!pnpId.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("只能检测选定的 NVIDIA PCI 设备。");
        await downloader.VerifyExecutablesAsync(CancellationToken.None);
        DriverCleanup.RequireNoExistingService();
        string run = cache.NewOutput("diagnostics"); Directory.CreateDirectory(run);
        string driverPath = Path.Combine(run, "ssdiag-pmxdrv.sys");
        var state = UnlockState.Unknown; string detail; bool cleanup = true;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await ProcessRunner.RunCapturedAsync(Path.Combine(cache.ReleaseDirectory, "ssdiag-x86_64-windows.exe"),
                ["--select", pnpId], log, deadline.Token, run,
                new Dictionary<string, string> { ["TEMP"] = run, ["TMP"] = run, ["NO_COLOR"] = "1" });
            await File.WriteAllLinesAsync(Path.Combine(run, "ssdiag-output.txt"), result.Lines);
            state = SsdiagOutput.Parse(result.Lines, pnpId, result.ExitCode);
            detail = state switch
            {
                UnlockState.Unlocked => "当前寄存器覆盖值有效：已解锁。",
                UnlockState.Locked => "ssdiag 当前判定：未解锁。",
                _ => $"未取得唯一、成功的设备判定（退出码 {result.ExitCode}）。请查看终端；驱动加载可能受 Windows 策略限制。"
            };
        }
        catch (OperationCanceledException) { detail = "ssdiag 检测超时，已终止子进程并执行驱动清理；当前状态无法判断。"; }
        finally
        {
            try { await DriverCleanup.CleanupAsync(driverPath); log("ssdiag 本次驱动与临时文件清理检查完成。"); }
            catch (Exception ex) { cleanup = false; log("ssdiag 清理未完成：" + ex.Message); }
        }
        if (!cleanup) detail += " 驱动清理未完成，请查看终端日志。";
        return new(state, detail, cleanup, DateTimeOffset.Now);
    }
}

internal static class DriverCleanup
{
    private const string Service = "SSDIAG_PMXDRV";
    private const string DriverHash = "b1a8ee1222eea5f199028d90b9b77c2acf46d6d84a9e125403b2888c6f681c72";
    public static void RequireNoExistingService()
    {
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new IOException("无法访问 Windows 服务管理器。");
        try
        {
            nint service = OpenService(manager, Service, 4);
            if (service != 0) { CloseServiceHandle(service); throw new IOException("发现已有 SSDIAG_PMXDRV 服务。请先确认原检测进程或残留状态，避免影响其他操作。"); }
            if (Marshal.GetLastWin32Error() != 1060) throw new IOException("无法确认 SSDIAG_PMXDRV 服务状态。");
        }
        finally { CloseServiceHandle(manager); }
    }
    public static async Task CleanupAsync(string expectedPath)
    {
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new IOException("无法访问服务管理器检查清理状态。");
        try
        {
            nint service = OpenService(manager, Service, 1 | 4 | 32 | 0x10000);
            if (service != 0)
            {
                try
                {
                    QueryServiceConfig(service, 0, 0, out int bytes);
                    nint buffer = Marshal.AllocHGlobal(bytes);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, bytes, out _)) throw new IOException("无法核对残留服务路径。");
                        var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                        string path = (Marshal.PtrToStringUni(config.BinaryPath) ?? "").Trim('"');
                        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("服务路径不属于本次检测，已保留。");
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    if (File.Exists(expectedPath)) await FileIntegrity.RequireAsync(expectedPath, DriverHash);
                    if (!QueryServiceStatus(service, out var status)) throw new IOException("无法读取驱动服务状态。");
                    if (status.State != 1 && !ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062)
                        throw new IOException("停止本次驱动失败。");
                    for (int i = 0; i < 30; i++)
                    {
                        if (!QueryServiceStatus(service, out status)) throw new IOException("无法确认驱动停止状态。");
                        if (status.State == 1) break;
                        await Task.Delay(150);
                    }
                    if (status.State != 1 || !DeleteService(service)) throw new IOException("本次驱动停止或服务删除尚未完成。");
                }
                finally { CloseServiceHandle(service); }
            }
            else if (Marshal.GetLastWin32Error() != 1060) throw new IOException("无法确认本次驱动服务已删除。");
        }
        finally { CloseServiceHandle(manager); }
        if (File.Exists(expectedPath)) { await FileIntegrity.RequireAsync(expectedPath, DriverHash); File.Delete(expectedPath); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { public uint Type, Start, Error; public nint BinaryPath, Group; public uint Tag; public nint Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(nint service, nint config, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
