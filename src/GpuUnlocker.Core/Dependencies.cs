using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace GpuUnlocker.Core;

public sealed class CacheLayout
{
    public string Root { get; }
    public string ReleaseZip => Path.Combine(Root, "downloads", ReleaseCatalog.Release.Name);
    public string ReleaseDirectory => Path.Combine(Root, "releases", "v2.1.5");
    public string Inputs => Path.Combine(Root, "inputs");
    public CacheLayout(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "gpuunlocker"));
    public void EnsureSecure()
    {
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存目录不能是链接或重解析点。");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
    }
    public string NewOutput(string category) => Path.Combine(Root, category, Guid.NewGuid().ToString("N"));
}

public static class ReleaseCatalog
{
    public static readonly DownloadAsset Release = new("nv-uefi-unlocker-v2.1.5.zip",
        "https://github.com/physicsdolphin/nv-uefi-unlocker/releases/download/v2.1.5/nv-uefi-unlocker-v2.1.5.zip",
        "82da87394cd2f619cdb85c44c76f2b514e81f908941d91698427961a5eec648e");
    public static readonly DownloadAsset[] Inputs = [
        new("NVIDIA-kernel-module-source-610.43.03.tar.xz", "https://download.nvidia.com/XFree86/NVIDIA-kernel-module-source/NVIDIA-kernel-module-source-610.43.03.tar.xz", "7e118923c7a23edc36114d63273a46e3e04e9af98695a42203e7ac2dfE9fc1dc"),
        new("nvidia-utils-610.43.03-1-x86_64_v3.pkg.tar.zst", "https://archive.cachyos.org/archive/cachyos-v3/nvidia-utils-610.43.03-1-x86_64_v3.pkg.tar.zst", "e17e7c92eeffa178b0fe1f40eb17a47095f8fc3ce4a77bf115cf5bd5a4df86e9")
    ];
    public static readonly DownloadAsset TuringBoot = new("gsp_rm_boot_dbg.bin", "https://raw.githubusercontent.com/xrip/cmp50hx-unlock/master/efi-unlock/blobs/gsp_rm_boot_dbg.bin", "fa43239bcee7b97ca62f007cc68487560a39e19f74f3dde7486db3f98df8e471");
}

public static class FileIntegrity
{
    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    public static async Task RequireAsync(string path, string hash, CancellationToken token = default)
    {
        if (!File.Exists(path) || !string.Equals(await HashAsync(path, token), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 校验失败：{Path.GetFileName(path)}");
    }
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("非法相对路径。");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径不能越出目标目录。");
        return full;
    }
}

public sealed class DependencyDownloader(CacheLayout cache)
{
    public static HttpClientHandler CreateHandler(ProxySettings settings)
    {
        var handler = new HttpClientHandler { UseProxy = settings.Mode != ProxyMode.None };
        if (settings.Mode == ProxyMode.Custom)
        {
            if (!Uri.TryCreate(settings.Address, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "socks5")) throw new ArgumentException("代理地址需要使用 http://、https:// 或 socks5://。");
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            var proxy = new WebProxy(builder.Uri);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            handler.Proxy = proxy;
        }
        return handler;
    }
    public async Task DownloadAsync(DownloadAsset asset, string destination, ProxySettings proxy,
        IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        using var handler = CreateHandler(proxy);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GPUUnlocker/1.0");
        await DownloadWithClientAsync(client, asset, destination, progress, token);
    }
    public static async Task DownloadWithClientAsync(HttpClient client, DownloadAsset asset, string destination,
        IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (string.Equals(await FileIntegrity.HashAsync(destination, token), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            { long length = new FileInfo(destination).Length; progress?.Report(new(asset.Name, length, length)); return; }
            File.Delete(destination);
        }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072]; long count = 0; long nextReport = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), token); count += n;
                    if (Environment.TickCount64 >= nextReport)
                    { progress?.Report(new(asset.Name, count, response.Content.Headers.ContentLength)); nextReport = Environment.TickCount64 + 120; }
                }
                progress?.Report(new(asset.Name, count, response.Content.Headers.ContentLength));
            }
            await FileIntegrity.RequireAsync(temporary, asset.Sha256, token);
            File.Move(temporary, destination, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) { throw new IOException($"下载 {asset.Name} 失败，HTTP 状态：{ex.StatusCode?.ToString() ?? "连接错误"}。请检查网络或代理后重试。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task PrepareReleaseAsync(ProxySettings proxy, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        await DownloadAsync(ReleaseCatalog.Release, cache.ReleaseZip, proxy, progress, token);
        await ExtractVerifiedAsync(cache.ReleaseZip, cache.ReleaseDirectory, token);
    }
    public static async Task ExtractVerifiedAsync(string zip, string destination, CancellationToken token = default)
    {
        // The caller verifies the release digest before this generic ZIP utility.
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".staging";
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string target = FileIntegrity.SafePath(staging, entry.FullName);
                if (!paths.Add(target) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("ZIP 含重复路径或链接。");
                total += entry.Length;
                if (total > 128 * 1024 * 1024) throw new InvalidDataException("依赖 ZIP 解压大小异常。");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = File.Create(target);
                await input.CopyToAsync(output, token);
            }
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public async Task VerifyExecutablesAsync(CancellationToken token)
    {
        await FileIntegrity.RequireAsync(cache.ReleaseZip, ReleaseCatalog.Release.Sha256, token);
        using var archive = ZipFile.OpenRead(cache.ReleaseZip);
        foreach (string name in new[] { "make-usbroot.ps1", "BOOTX64.EFI", "assets-x86_64-windows.exe", "ssdiag-x86_64-windows.exe" })
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Release 缺少 {name}");
            await using var source = entry.Open();
            string expected = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
            await FileIntegrity.RequireAsync(Path.Combine(cache.ReleaseDirectory, name), expected, token);
        }
    }
    public async Task PrepareInputsAsync(string family, ProxySettings proxy, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        foreach (var asset in ReleaseCatalog.Inputs.Concat(family == "tu10x" ? [ReleaseCatalog.TuringBoot] : Array.Empty<DownloadAsset>()))
            await DownloadAsync(asset, Path.Combine(cache.Inputs, asset.Name), proxy, progress, token);
    }
}
