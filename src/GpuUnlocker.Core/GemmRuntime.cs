using System.IO.Compression;
using System.Security.Cryptography;

namespace GpuUnlocker.Core;

public static class GemmRuntime
{
    public static readonly DownloadAsset Archive = new("libcublas-windows-x86_64-12.8.4.1-archive.zip",
        "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.4.1-archive.zip",
        "57a470112cec7e112c95253dde8b3c7184d795dbd92b0bde77a4cb7f8c94c8aa");
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPUUnlocker", "cuBLAS", "12.8.4.1");
    public static string ZipPath => Path.Combine(DirectoryPath, Archive.Name);
    // DLL hashes are pinned independently of the archive so cached libraries can be verified before loading.
    private static readonly Dictionary<string, string> Files = new()
    {
        ["cublas64_12.dll"] = "9513540E4EC4C51EE9E7304138C2CC255C29A8C181F9E80C38EFA25738BECD99",
        ["cublasLt64_12.dll"] = "B199D1FF892A81B7FD3D57BA1781549609B41500B36008FEF326038393AD46C7"
    };
    public static async Task PrepareAsync(ProxySettings proxy, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        try { await Task.Run(Verify, token); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var downloader = new DependencyDownloader(new CacheLayout());
        await downloader.DownloadAsync(Archive, ZipPath, proxy, progress, token);
        using var zip = ZipFile.OpenRead(ZipPath);
        string prefix = "libcublas-windows-x86_64-12.8.4.1-archive/";
        foreach (string name in Files.Keys.Append("LICENSE"))
        {
            token.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(prefix + (name == "LICENSE" ? "LICENSE" : "bin/" + name))
                ?? throw new InvalidDataException("cuBLAS 归档缺少 " + name);
            string destination = Path.Combine(DirectoryPath, name);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    await input.CopyToAsync(output, token);
                string hash = Files.TryGetValue(name, out string? libraryHash) ? libraryHash : LicenseSha256;
                await FileIntegrity.RequireAsync(temporary, hash, token);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await Task.Run(Verify, token);
    }
    public static string Verify()
    {
        foreach (var file in Files.Append(new KeyValuePair<string, string>("LICENSE", LicenseSha256)))
        {
            string path = Path.Combine(DirectoryPath, file.Key);
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("cuBLAS DLL 哈希不匹配：" + file.Key);
        }
        return Files["cublas64_12.dll"];
    }
    private const string LicenseSha256 = "E2C71BABFD18A8E69542DD7E9CA018F9CAA438094001A58E6BC4D8C999BF0D07";
}
