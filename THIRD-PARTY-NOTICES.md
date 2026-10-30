# Third-party components

GPU Unlocker GUI downloads the following independent upstream release only after the user selects **下载依赖**:

- Project: physicsdolphin/nv-uefi-unlocker
- Release: https://github.com/physicsdolphin/nv-uefi-unlocker/releases/tag/v2.1.5
- Archive SHA-256: `82da87394cd2f619cdb85c44c76f2b514e81f908941d91698427961a5eec648e`
- The release includes a GNU GPL version 2 license document. Its original `LICENSE` and `README.md` are retained in the extracted dependency directory. Consult the upstream project's licensing terms for use and redistribution.

The desktop package does not bundle that release, NVIDIA firmware, or the SSDIAG kernel driver/tool. The SSDIAG executable in the downloaded, verified archive runs only when the user explicitly requests current unlock-state detection. No `--reapply` operation is exposed. See README for its temporary kernel-driver lifecycle and the upstream v2.1.5 cleanup limitations.

The upstream generation process extracts resources from SHA-256-pinned NVIDIA 610.43.03 source and utility packages. Turing additionally uses the pinned cmp50hx boot resource. Download URLs and digests are recorded in `ReleaseCatalog`; upstream tooling checks its own expected inputs and derived assets. Original resources remain subject to their respective authors' terms.

The Windows self-contained distribution includes the .NET runtime and its included license notices from Microsoft.

The register-resident MMA microbenchmark PTX and host runner are authored in this project. The cubin files are assembled with NVIDIA ptxas 12.8.93 and loaded through the installed NVIDIA CUDA Driver API. They contain benchmark compute kernels, not GPU firmware. NVIDIA's assembler and cuBLAS libraries are not included in the desktop package.

## NVIDIA cuBLAS runtime for GEMM benchmarking

On the first explicit GEMM benchmark request, the application downloads NVIDIA cuBLAS 12.8.4.1 from NVIDIA's official CUDA redistribution service using the selected proxy. It does not require Python or install the CUDA Toolkit. The archive is not bundled in the desktop EXE.

- Archive: https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.4.1-archive.zip
- SHA-256: `57a470112cec7e112c95253dde8b3c7184d795dbd92b0bde77a4cb7f8c94c8aa`
- Extracted components: `cublas64_12.dll`, `cublasLt64_12.dll`, and original `LICENSE`.
- NVIDIA's original license is retained beside the cached libraries. These libraries remain subject to NVIDIA's CUDA Toolkit license terms. The SHA-256-pinned DLLs are verified before loading.

The register-resident MMA runner described above is historical; the current default benchmark uses direct cuBLAS dense GEMM calls with separately reported precisions and FP32 accumulation.
