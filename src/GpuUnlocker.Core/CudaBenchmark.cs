using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GpuUnlocker.Core;

public sealed record CudaGpu(int Ordinal, string Uuid, string Name, string PciBusId, int Major, int Minor, int Multiprocessors);
public sealed record GpuTarget(GpuInfo WindowsGpu, CudaGpu? Cuda)
{
    public string DisplayName => $"{WindowsGpu.Name} · {Cuda?.PciBusId ?? WindowsGpu.PciBusId ?? "PCI 地址未知"}";
}
public static class GpuIdentity
{
    public static GpuTarget[] Map(IEnumerable<GpuInfo> windows, IEnumerable<CudaGpu> cuda)
    {
        var devices = windows.Where(g => g.DeviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = cuda.ToArray();
        return devices.Select(g =>
        {
            string? pci = g.PciBusId is null ? null : CudaKernels.NormalizePci(g.PciBusId);
            var matches = candidates.Where(c => pci is not null && c.PciBusId == pci).ToArray();
            int owners = pci is null ? 0 : devices.Count(other => other.PciBusId is not null && CudaKernels.NormalizePci(other.PciBusId) == pci);
            return new GpuTarget(g, matches.Length == 1 && owners == 1 ? matches[0] : null);
        }).ToArray();
    }
}
public sealed record ThroughputResult(string Mode, string Status, double? Tflops, string Detail, double[] Samples, bool CorrectnessPassed)
{
    public double[] Milliseconds { get; init; } = [];
    public int LaunchesPerSample { get; init; }
    public string Validation { get; init; } = "";
    public double? LockedTflops { get; init; }
    public double? ImprovementPercent { get; init; }
}
public sealed record BenchmarkReport(string Uuid, string PciBusId, string Name, string Architecture, string LibrarySha256,
    int DriverVersion, DateTimeOffset Timestamp, ThroughputResult[] Results, int? InitialUtilizationPercent = null)
{
    public string Method { get; init; } = "";
    public int Rounds { get; init; }
    public int MatrixSize { get; init; }
    public int WarmupMilliseconds { get; init; }
    public int TargetSampleMilliseconds { get; init; }
    public int CublasVersion { get; init; }
    public string RuntimeArchiveSha256 { get; init; } = "";
    public string Configuration { get; init; } = "";
    public string DriverRelease { get; init; } = "";
    public BenchmarkStateEvidence? StateEvidence { get; init; }
    public DateTimeOffset? BaselineTimestamp { get; init; }
}

public static class CudaKernels
{
    public static (byte[] Bytes, string Hash) Load(int architecture)
    {
        if (architecture is not (75 or 80 or 86 or 89)) throw new NotSupportedException($"未内置 sm_{architecture} 内核；支持 SM75/80/86/89。");
        var assembly = typeof(CudaKernels).Assembly;
        using var stream = assembly.GetManifestResourceStream($"GpuUnlocker.Core.Kernels.tensor_sm{architecture}.cubin") ?? throw new IOException("预编译内核缺失。");
        using var memory = new MemoryStream(); stream.CopyTo(memory); byte[] bytes = memory.ToArray();
        using var manifestStream = assembly.GetManifestResourceStream("GpuUnlocker.Core.Kernels.kernel-lock.json") ?? throw new IOException("内核校验清单缺失。");
        using var manifest = JsonDocument.Parse(manifestStream);
        string expected = manifest.RootElement.GetProperty("kernels").GetProperty($"sm{architecture}").GetString()!;
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("预编译内核 SHA-256 不匹配。");
        return (bytes, actual);
    }
    public static int K(int architecture, string mode) => architecture == 75 || mode == "TF32" ? 8 : 16;
    public static double Flops(int blocks, int threads, int iterations, int architecture, string mode) =>
        (double)blocks * (threads / 32) * iterations * 8 * 2 * 16 * 8 * K(architecture, mode);
    public static bool Supported(int architecture, string mode) => architecture is 75 or 80 or 86 or 89 && mode is "FP16" or "BF16" or "TF32" && (mode == "FP16" || architecture >= 80);
    public static double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    public static string NormalizePci(string value)
    {
        var p = value.Split(':');
        if (p.Length != 3) return value.ToLowerInvariant();
        return $"{Convert.ToUInt32(p[0], 16):x4}:{Convert.ToUInt32(p[1], 16):x2}:{p[2].ToLowerInvariant()}";
    }
}

public static class CudaBenchmark
{
    public static CudaGpu[] Enumerate()
    {
        Native.Check(Native.cuInit(0)); Native.Check(Native.cuDeviceGetCount(out int count));
        var result = new List<CudaGpu>();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            Native.Check(Native.cuDeviceGet(out int device, ordinal));
            byte[] uuid = new byte[16]; Native.Check(Native.cuDeviceGetUuid(uuid, device));
            string hex = Convert.ToHexString(uuid).ToLowerInvariant();
            string id = $"GPU-{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
            var name = new StringBuilder(256); var pci = new StringBuilder(64);
            Native.Check(Native.cuDeviceGetName(name, name.Capacity, device));
            Native.Check(Native.cuDeviceGetPCIBusId(pci, pci.Capacity, device));
            Native.Check(Native.cuDeviceGetAttribute(out int major, 75, device));
            Native.Check(Native.cuDeviceGetAttribute(out int minor, 76, device));
            Native.Check(Native.cuDeviceGetAttribute(out int sms, 16, device));
            result.Add(new(ordinal, id, name.ToString(), CudaKernels.NormalizePci(pci.ToString()), major, minor, sms));
        }
        return result.ToArray();
    }
    // Synchronous and thread-affine: run only in an isolated benchmark worker.
    public static BenchmarkReport Run(string uuid, Action<string>? log = null, int rounds = GemmProtocol.DefaultRounds)
        => GemmBenchmark.Run(uuid, log, rounds);
}
internal static class Native
{
    const string Dll = "nvcuda.dll";
    public static void Check(int status) { if (status != 0) throw new IOException($"CUDA Driver API 返回错误 {status}。"); }
    [DllImport(Dll)] public static extern int cuInit(uint flags);
    [DllImport(Dll)] public static extern int cuDeviceGetCount(out int count);
    [DllImport(Dll)] public static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport(Dll)] public static extern int cuDeviceGetUuid([Out] byte[] uuid, int device);
    [DllImport(Dll)] public static extern int cuDeviceGetName(StringBuilder name, int length, int device);
    [DllImport(Dll)] public static extern int cuDeviceGetPCIBusId(StringBuilder pci, int length, int device);
    [DllImport(Dll)] public static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport(Dll)] public static extern int cuDriverGetVersion(out int value);
    [DllImport(Dll, EntryPoint = "cuCtxCreate_v2")] public static extern int cuCtxCreate(out nint ctx, uint flags, int device);
    [DllImport(Dll, EntryPoint = "cuCtxDestroy_v2")] public static extern int cuCtxDestroy(nint ctx);
    [DllImport(Dll)] public static extern int cuModuleLoadData(out nint module, byte[] image);
    [DllImport(Dll)] public static extern int cuModuleUnload(nint module);
    [DllImport(Dll)] public static extern int cuModuleGetFunction(out nint function, nint module, string name);
    [DllImport(Dll, EntryPoint = "cuMemAlloc_v2")] public static extern int cuMemAlloc(out ulong pointer, nuint bytes);
    [DllImport(Dll, EntryPoint = "cuMemFree_v2")] public static extern int cuMemFree(ulong pointer);
    [DllImport(Dll, EntryPoint = "cuMemcpyDtoH_v2")] public static extern int cuMemcpyDtoH([Out] float[] destination, ulong source, nuint bytes);
    [DllImport(Dll, EntryPoint = "cuMemcpyHtoD_v2")] public static extern int cuMemcpyHtoD(ulong destination, nint source, nuint bytes);
    [DllImport(Dll, EntryPoint = "cuMemcpyDtoH_v2")] public static extern int cuMemcpyDtoHRaw(nint destination, ulong source, nuint bytes);
    [DllImport(Dll)] public static extern int cuEventCreate(out nint ev, uint flags);
    [DllImport(Dll)] public static extern int cuEventRecord(nint ev, nint stream);
    [DllImport(Dll)] public static extern int cuEventSynchronize(nint ev);
    [DllImport(Dll)] public static extern int cuEventElapsedTime(out float milliseconds, nint start, nint end);
    [DllImport(Dll, EntryPoint = "cuEventDestroy_v2")] public static extern int cuEventDestroy(nint ev);
    [DllImport(Dll)] public static extern int cuLaunchKernel(nint function, uint gx, uint gy, uint gz, uint bx, uint by, uint bz, uint shared, nint stream, [In] nint[] arguments, nint extra);
}
