using System.Text;
using System.Text.Json;

namespace GpuUnlocker.Core;

public sealed class PatchBuilder(CacheLayout cache, DependencyDownloader downloader, WindowsBackend backend)
{
    public async Task<PatchPackage> BuildAsync(string family, ProxySettings proxy, IProgress<DownloadProgress>? progress,
        Action<string> log, CancellationToken token)
    {
        _ = Families.Assets(family);
        await downloader.VerifyExecutablesAsync(token);
        if (family == "tu10x") await backend.RunAsync<bool>("Turing", log, token: token);
        await downloader.PrepareInputsAsync(family, proxy, progress, token);
        string output = cache.NewOutput("generated");
        Directory.CreateDirectory(output);
        // UTF-8 wrapper is application-owned, not part of the downloaded dependency.
        string wrapper = Path.Combine(WindowsBackend.ScriptDirectory, "generate.ps1");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper,
            "-ReleaseDirectory", cache.ReleaseDirectory, "-Family", family, "-OutputDirectory", output, "-InputDirectory", cache.Inputs };
        await ProcessRunner.RunAsync(WindowsBackend.PowerShell, args, log, token);
        token.ThrowIfCancellationRequested();
        string package = cache.NewOutput("packages");
        Directory.CreateDirectory(Path.Combine(package, "EFI", "GPUUnlock"));
        foreach (string asset in Families.Assets(family)) File.Copy(Path.Combine(output, asset), Path.Combine(package, asset));
        File.Copy(Path.Combine(output, "BOOTX64.EFI"), Path.Combine(package, "EFI", "GPUUnlock", "BOOTX64.EFI"));
        await File.WriteAllTextAsync(Path.Combine(package, "i_know_what_i_am_doing.txt"), "auto-run at every boot\r\n", Encoding.ASCII, token);
        await File.WriteAllTextAsync(Path.Combine(package, "chain-target.txt"), "\\EFI\\Microsoft\\Boot\\bootmgfw.efi\r\n", Encoding.ASCII, token);
        var files = new List<FileEntry>();
        foreach (string path in Families.DeploymentPaths(family))
        {
            string full = FileIntegrity.SafePath(package, path);
            files.Add(new(path, new FileInfo(full).Length, await FileIntegrity.HashAsync(full, token)));
        }
        var manifest = new PatchManifest(2, "gpu-unlocker", family, "v2.1.5", files.ToArray());
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), JsonSerializer.Serialize(manifest, WireJson.Options), token);
        log($"已生成 {family} 部署包，{files.Sum(x => x.Bytes) / 1048576.0:F1} MiB。");
        return new(package, manifest);
    }
}
