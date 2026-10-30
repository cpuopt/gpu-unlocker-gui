# 吞吐测试口径检查（2026-10-29）

更新：检查后已将默认测试替换为原生 cuBLAS GEMM，固定 8192³、预热 2 秒、默认 7 轮（可选 11/15），每轮目标 1 秒。新增 FP32 模式，FP16/BF16 使用同类型输出、FP32 累加并禁止低精度归约。以下检查结论描述替换前的 MMA 测试；实施后的正式口径以 README 为准。

检查对象：`CudaBenchmark.cs`、`tools/build-kernels.ps1`、SM89 PTX 与现有 cubin。此次仅检查代码及 cubin 指令，没有重新测量性能，也没有社区对照原始报告，因此不能量化具体偏差。

## 结论

当前实现是寄存器驻留的 Tensor Core 指令微基准，不是社区常见的 PyTorch `torch.mm` / `torch.matmul` 大矩阵 GEMM。二者即使标注同样的 FP16/BF16 和 TFLOPS，也不能直接用数值判断高低异常。未发现运算量公式、毫秒换算或中位数统计的明显错误；不能仅为贴近某个社区成绩而给结果乘折扣。

## 已核查

- 输入为寄存器中的常量 1，每轮执行八条独立累加链，使用 dense `mma.sync`，没有 `mma.sp` 的稀疏倍率。没有从显存读取实际大矩阵。
- 每条 `m16n8k16` 每 warp 是 `2*16*8*16 = 4096` FLOP；八条链为 32768 FLOP。TF32 与 SM75 的 K 为 8，相应减半。宿主公式使用 `threads/32`，没有按每线程重复计整条 warp 的工作量。
- SM89 cubin 的反汇编存在实际 HMMA 指令，循环执行计算；编译器没有把全部结果折叠为一个常量存储。
- 开始/结束事件位于同一默认 stream，等待结束事件后读取 GPU 毫秒，计算时除以 1000 转秒；三轮取中位数，非最高值。
- 预热约 220 ms，每轮目标约 600 ms。较短预热可能未达到温度/功耗稳态；现有报告没有记录每轮频率、功耗与温度，不能据此排除 Boost 差异。
- 结果校验读取每线程一个汇总值，核对 `32*K*iterations`。这能检测执行错误，但不等价于对随机输入的完整 GEMM 正确性验证。
- 当前只有 FP16/BF16/TF32，均为 FP32 累加。没有完整精度 FP32 测试。TF32 输入精度与 FP32 不同。

## 与 PyTorch 同类的原生实现方案

PyTorch CUDA BLAS 后端调用 NVIDIA cuBLAS/cuBLASLt，并不是必须由 Python 实现的矩阵乘法算法。C# 可通过原生互操作直接调用 cuBLAS；运行不需要 Python，但 NVIDIA 驱动本身不附带这些 cuBLAS DLL，需要额外随包提供或下载。

建议新增独立 GEMM 基准并保留原指令测试：

1. 固定并记录矩阵 M/N/K（例如 8192，各尺寸单独报告）、布局及转置，dense GEMM，alpha=1、beta=0；不搜索最优尺寸后只报告最快值。
2. 分别报告 FP32（禁用 TF32）、TF32、FP16/FP32 累加、BF16/FP32 累加。输出类型也须记录：低精度输出不能和 FP32 输出默认视为同一配置。
3. 与对照脚本一致地设置是否允许低精度中间归约。FP32 累加标签本身不足以说明这个开关；严格禁止归约与 PyTorch 默认配置应分别标注。
4. 在计时外分配和初始化矩阵，预热至少约 1 秒；同一 stream 的 CUDA Event 包围多次 GEMM，等待完成。三轮或更多轮报告原始耗时与中位数，使用 `2*M*N*K*调用次数/秒/1e12`。
5. 计时外以非均匀、有正负的输入验证若干元素的 CPU 参考点积与误差界；报告库版本、驱动、GPU UUID、模式、尺寸、归约设置、采样时长、每轮成绩。固定版本、功耗及温度条件才能做前后对比。

原生调用省去部分框架调度开销，cuBLAS 版本、算法选择与 workspace 也可能不同，因此“同类口径”并不承诺与任意 PyTorch 脚本逐位或逐分相同。社区没有唯一统一的 FP16/BF16 跑分方法，需要指定对照脚本与环境。

## 参考

- [PyTorch v2.9.0 CUDA BLAS 实现](https://github.com/pytorch/pytorch/blob/v2.9.0/aten/src/ATen/cuda/CUDABlas.cpp)：FP16/BF16 的 GEMM 与累加、归约设置。
- [PyTorch CUDA semantics](https://docs.pytorch.org/docs/2.9/notes/cuda.html)：TF32、低精度归约与异步执行计时。
- [NVIDIA cuBLAS](https://docs.nvidia.com/cuda/cublas/index.html)：数据与计算类型、math mode、GEMM 接口。
- [NVIDIA PTX ISA](https://docs.nvidia.com/cuda/parallel-thread-execution/index.html#warp-level-matrix-instructions-mma)：warp 级 MMA 语义与形状。
