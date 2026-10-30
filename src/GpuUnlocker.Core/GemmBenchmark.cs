using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GpuUnlocker.Core;

public static class GemmProtocol
{
    public const int DefaultRounds = 7, Size = 8192, WarmupMs = 2000, SampleMs = 1000;
    public const string Method = "dense-cublas-gemm-v1";
    public static readonly string[] Modes = ["FP32", "TF32", "FP16", "BF16"];
    public static void ValidateRounds(int rounds)
    {
        if (rounds < 3 || rounds > 31 || rounds % 2 == 0) throw new ArgumentException("测速轮次须为 3–31 之间的奇数。");
    }
    public static double Flops(int n, int launches) => 2.0 * n * n * n * launches;
    public static bool Supported(int architecture, string mode) => architecture >= 75 &&
        (mode is "FP32" or "FP16" || architecture >= 80 && mode is "TF32" or "BF16");
}

// Direct cuBLAS calls: no Python, framework, autotuning sweep or CUDA Graph replay.
public static class GemmBenchmark
{
    public static BenchmarkReport Run(string uuid, Action<string>? log, int rounds)
    {
        GemmProtocol.ValidateRounds(rounds);
        string libraryHash = GemmRuntime.Verify();
        Blas.Load();
        var gpu = CudaBenchmark.Enumerate().SingleOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("选定 CUDA UUID 不存在，已停止测速。");
        int arch = gpu.Major * 10 + gpu.Minor;
        if (arch < 75) throw new NotSupportedException("GEMM 测试需要 SM75 或更新架构。");
        Native.Check(Native.cuDriverGetVersion(out int driver));
        Native.Check(Native.cuDeviceGet(out int device, gpu.Ordinal));
        nint context = 0, handle = 0, start = 0, end = 0;
        var results = new List<ThroughputResult>();
        int version = 0;
        try
        {
            Native.Check(Native.cuCtxCreate(out context, 0, device));
            Blas.Check(Blas.cublasCreate_v2(out handle));
            Blas.Check(Blas.cublasGetVersion_v2(handle, out version));
            Native.Check(Native.cuEventCreate(out start, 0)); Native.Check(Native.cuEventCreate(out end, 0));
            foreach (string mode in GemmProtocol.Modes)
            {
                if (!GemmProtocol.Supported(arch, mode))
                { results.Add(new(mode, "不支持", null, "此架构不提供该硬件精度模式。", [], false)); continue; }
                results.Add(RunMode(handle, start, end, mode, rounds, log));
            }
            return new(gpu.Uuid, gpu.PciBusId, gpu.Name, $"sm_{arch}", libraryHash, driver, DateTimeOffset.Now, results.ToArray())
            {
                Method = GemmProtocol.Method, Rounds = rounds, MatrixSize = GemmProtocol.Size,
                WarmupMilliseconds = GemmProtocol.WarmupMs, TargetSampleMilliseconds = GemmProtocol.SampleMs,
                CublasVersion = version, RuntimeArchiveSha256 = GemmRuntime.Archive.Sha256,
                Configuration = "column-major NN; alpha=1; beta=0; A/B/C same dtype; FP32 accumulation; reduced-precision reduction disabled; default cuBLAS algorithm; seed=0x12345678; quantized uniform [-1,1)"
            };
        }
        finally
        {
            if (end != 0) Native.cuEventDestroy(end);
            if (start != 0) Native.cuEventDestroy(start);
            if (handle != 0) Blas.cublasDestroy_v2(handle);
            if (context != 0) Native.cuCtxDestroy(context);
        }
    }

    private static ThroughputResult RunMode(nint handle, nint start, nint end, string mode, int rounds, Action<string>? log)
    {
        const int n = GemmProtocol.Size;
        bool low = mode is "FP16" or "BF16";
        int type = mode == "FP16" ? 2 : mode == "BF16" ? 14 : 0; // cudaDataType_t
        int compute = mode == "TF32" ? 77 : 68; // CUBLAS_COMPUTE_32F_FAST_TF32 / 32F
        // Same-width output as torch.matmul; force FP32 reduction rather than silently permit FP16/BF16 reductions.
        Blas.Check(Blas.cublasSetMathMode(handle, 16)); // CUBLAS_MATH_DISALLOW_REDUCED_PRECISION_REDUCTION
        ulong a = 0, b = 0, c = 0;
        nuint bytes = (nuint)((long)n * n * (low ? 2 : 4));
        try
        {
            Native.Check(Native.cuMemAlloc(out a, bytes)); Native.Check(Native.cuMemAlloc(out b, bytes)); Native.Check(Native.cuMemAlloc(out c, bytes));
            log?.Invoke($"{mode}: {n}×{n} GEMM，初始化矩阵；输出 {mode switch { "TF32" => "FP32", _ => mode }}，FP32 累加，禁止低精度归约。");
            // Identical nonuniform, signed inputs in every mode; all values are exactly representable in BF16/FP16/TF32.
            uint state = 0x12345678;
            var inputA = new sbyte[n * n]; var inputB = new sbyte[n * n];
            Fill(inputA, ref state); Fill(inputB, ref state);
            Upload(a, inputA, mode); Upload(b, inputB, mode);
            float alpha = 1, beta = 0;
            void Launch() => Blas.Check(Blas.cublasGemmEx(handle, 0, 0, n, n, n, ref alpha,
                a, type, n, b, type, n, ref beta, c, type, n, compute, -1));
            double Measure(int launches)
            {
                Native.Check(Native.cuEventRecord(start, 0));
                for (int i = 0; i < launches; i++) Launch();
                Native.Check(Native.cuEventRecord(end, 0)); Native.Check(Native.cuEventSynchronize(end));
                Native.Check(Native.cuEventElapsedTime(out float ms, start, end));
                if (!float.IsFinite(ms) || ms <= 0) throw new IOException("CUDA 计时无效。");
                return ms;
            }
            var warmup = Stopwatch.StartNew();
            do { Measure(1); } while (warmup.ElapsedMilliseconds < GemmProtocol.WarmupMs);
            // Calibrate outside the reported samples. Use multiple calls to avoid a single-call timing anomaly.
            double calibration = Measure(5) / 5;
            int launches = Math.Clamp((int)Math.Ceiling(GemmProtocol.SampleMs / calibration), 1, 10000);
            var points = ReferencePoints(inputA, inputB, n);
            var samples = new List<double>(); var times = new List<double>();
            for (int rep = 0; rep < rounds; rep++)
            {
                double ms = Measure(launches);
                Validate(c, points, mode); // CPU reference dot products and readback are outside GPU timing.
                double tflops = GemmProtocol.Flops(n, launches) / (ms / 1000) / 1e12;
                samples.Add(tflops); times.Add(ms);
                log?.Invoke($"{mode} 第 {rep + 1}/{rounds} 轮：{tflops:F2} TFLOPS，{ms:F1} ms / {launches} 次 GEMM，参考点积校验通过。");
            }
            return new(mode, "通过", CudaKernels.Median(samples), $"8192³ dense GEMM；FP32 累加；{(low ? mode : "FP32")} 输出；禁止低精度归约；{rounds} 轮中位数。", samples.ToArray(), true)
            { Milliseconds = times.ToArray(), LaunchesPerSample = launches, Validation = "32 deterministic output elements checked against CPU dot products after every sample" };
        }
        finally
        {
            if (c != 0) Native.cuMemFree(c);
            if (b != 0) Native.cuMemFree(b);
            if (a != 0) Native.cuMemFree(a);
        }
    }

    private static void Fill(sbyte[] values, ref uint state)
    {
        for (int i = 0; i < values.Length; i++)
        { state ^= state << 13; state ^= state >> 17; state ^= state << 5; values[i] = unchecked((sbyte)(state >> 24)); }
    }
    private static void Upload(ulong pointer, sbyte[] values, string mode)
    {
        Array buffer;
        if (mode is "FP16" or "BF16")
        {
            var data = new ushort[values.Length];
            for (int i = 0; i < data.Length; i++)
            {
                float x = values[i] / 128f;
                data[i] = mode == "FP16" ? BitConverter.HalfToUInt16Bits((Half)x) : (ushort)(BitConverter.SingleToUInt32Bits(x) >> 16);
            }
            buffer = data;
        }
        else
        {
            var data = new float[values.Length];
            for (int i = 0; i < data.Length; i++) data[i] = values[i] / 128f;
            buffer = data;
        }
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { Native.Check(Native.cuMemcpyHtoD(pointer, pinned.AddrOfPinnedObject(), (nuint)Buffer.ByteLength(buffer))); }
        finally { pinned.Free(); }
    }
    private static (long Index, double Value)[] ReferencePoints(sbyte[] a, sbyte[] b, int n)
    {
        return Enumerable.Range(0, 32).Select(i =>
        {
            int row = i == 31 ? n - 1 : (i * 1237) % n, col = i == 31 ? n - 1 : (i * 2341) % n;
            long sum = 0;
            for (int k = 0; k < n; k++) sum += (long)a[row + k * n] * b[k + col * n];
            return ((long)row + (long)col * n, sum / 16384.0);
        }).ToArray();
    }
    private static void Validate(ulong c, (long Index, double Value)[] points, string mode)
    {
        bool low = mode is "FP16" or "BF16";
        var data = new byte[4];
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            foreach (var point in points)
            {
                Native.Check(Native.cuMemcpyDtoHRaw(pinned.AddrOfPinnedObject(), c + (ulong)(point.Index * (low ? 2 : 4)), (nuint)(low ? 2 : 4)));
                float actual = mode == "FP16" ? (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data)) :
                    mode == "BF16" ? BitConverter.UInt32BitsToSingle((uint)BitConverter.ToUInt16(data) << 16) : BitConverter.ToSingle(data);
                double tolerance = 0.002 + Math.Abs(point.Value) * (mode == "BF16" ? 0.005 : mode == "FP16" ? 0.001 : 0.0001);
                if (!float.IsFinite(actual) || Math.Abs(actual - point.Value) > tolerance)
                    throw new IOException($"{mode} GEMM 参考点积校验失败：index={point.Index}, expected={point.Value}, actual={actual}；不展示吞吐。");
            }
        }
        finally { pinned.Free(); }
    }

    private static class Blas
    {
        const string Dll = "gpuunlocker_cublas";
        private static nint module;
        public static void Load()
        {
            if (module != 0) return;
            NativeLibrary.Load(Path.Combine(GemmRuntime.DirectoryPath, "cublasLt64_12.dll"));
            module = NativeLibrary.Load(Path.Combine(GemmRuntime.DirectoryPath, "cublas64_12.dll"));
            NativeLibrary.SetDllImportResolver(typeof(Blas).Assembly, (name, _, _) => name == Dll ? module : 0);
        }
        public static void Check(int status) { if (status != 0) throw new IOException($"cuBLAS 返回错误 {status}。"); }
        [DllImport(Dll)] public static extern int cublasCreate_v2(out nint handle);
        [DllImport(Dll)] public static extern int cublasDestroy_v2(nint handle);
        [DllImport(Dll)] public static extern int cublasGetVersion_v2(nint handle, out int version);
        [DllImport(Dll)] public static extern int cublasSetMathMode(nint handle, int mode);
        [DllImport(Dll)] public static extern int cublasGemmEx(nint handle, int transA, int transB, int m, int n, int k,
            ref float alpha, ulong a, int aType, int lda, ulong b, int bType, int ldb, ref float beta,
            ulong c, int cType, int ldc, int computeType, int algorithm);
    }
}
