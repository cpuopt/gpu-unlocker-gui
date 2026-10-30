# GPU Unlocker GUI

[English](README.md) | 简体中文

**当前版本：v1.3.0**（2026-10-30）

GPU Unlocker GUI 是面向 Windows 的显卡解锁图形界面工具，整合开机解锁安装、当前状态检测和算力测试，支持对比解锁前后的 FP32 / TF32 / FP16 / BF16 性能。

发布包采用单文件形式，内置 .NET 运行时，直接运行 `GPU-Unlocker-GUI-v1.3.0.exe` 即可。EXE 文件名包含实际版本号，Nightly 则形如 `GPU-Unlocker-GUI-v1.3.0-nightly.运行序号.exe`，便于区分。解锁资源和测速库按需下载，无需额外安装 Python 或 CUDA Toolkit。

Nightly 构建可通过 **Actions → Nightly build and release → Run workflow** 手动触发。测试通过后，工作流自动更新 [nightly 预发布](https://github.com/cpuopt/gpu-unlocker-gui/releases/tag/nightly)，提供 EXE、SHA-256 校验文件和依赖清单。版本格式为 `1.3.0-nightly.运行序号`，每次成功构建替换上一份 Nightly。

![GPU Unlocker GUI 主界面：环境检查与依赖下载](docs/images/gui-main.png)

## 界面语言

右上角提供 **中文 / English** 语言选择，切换后立即生效。首次启动跟随 Windows 界面语言：中文系统默认中文，其余默认英文。选择会保存到 `%LocalAppData%\GPUUnlocker\language.txt`，下次启动及以管理员权限重新打开时继续使用。切换不会重置当前任务、显卡选择或风险确认。

国际化使用 .NET 内置成熟组件 `ResourceManager`、`.resx` 和卫星资源程序集，以英文资源作为回退。资源位于 `src/GpuUnlocker.App/Resources/Strings.resx` 和 `Strings.zh-CN.resx`。底层后端、第三方工具和系统的诊断输出保留原文，部分详细诊断消息可能仍使用原始语言；JSON 报告及基线标识保持原有格式。

## 使用方法

运行环境要求为 **Windows x64、NVIDIA 驱动和 GPT/UEFI 启动**，安装前需在 BIOS 中关闭 **Secure Boot（安全启动）**。当前安装流程主要面向 NVIDIA 20 / 30 / 40 系，具体兼容性以上游说明和程序检测为准；20 系还需满足上游 GSP 条件。

安装涉及系统启动分区写入，操作前应具备进入 BIOS 和选择启动项的基本能力。安装流程如下：

1. 打开程序，阅读风险告知并勾选确认；按提示以管理员权限重新打开。
2. 点击 **环境检查**，根据提示处理未通过的项目。
3. 选择合适的网络代理，点击 **下载依赖**。下载失败可重试。
4. 确认显卡家族，点击 **生成补丁**。
5. 点击 **写入 ESP**，完成后点击 **检查安装**。若提示旧补丁残留，先使用 **移除 unlock 补丁** 清理。
6. 在 **设为第一启动项** 中勾选风险提示，再点击 **将 GPU Unlock 设为第一启动项**。程序会备份当前顺序、设置并读取验证，同时保留 Windows 回退入口。写入 ESP 本身不会自动前置启动项。
7. 点击 **检查补丁与配置**，确认安装文件、启动项和当前启动顺序，再重启 Windows。若主板未保持该设置，可在 BIOS 中手动选择 GPU Unlock。
8. 回到程序，在 **当前状态与算力测试** 中选择显卡，点击 **检测当前解锁状态**，确认本次是否已解锁。

<details>
<summary>启动顺序设置界面</summary>

![勾选风险提示后才能启用首选启动项设置按钮](docs/images/gui-boot-order.png)

</details>

“检查安装”用于验证文件和启动配置，实际解锁状态由重启后的状态检测确认。检测过程中会临时加载上游驱动，需等待检测及清理结束。若结果为“无法判断”，可通过界面下方的终端查看具体原因。

安装有效性与启动优先级分别显示。启动顺序被 BIOS、Windows 或其他程序改动后，重新检查会显示 GPU Unlock 是否仍为首选、Windows 回退入口是否仍在顺序中。一次性启动序列会单独提示，可能优先于长期顺序影响下一次启动。

## 测速与性能对比

![GPU Unlocker GUI 测速界面：采样轮次、未解锁基线与提升比例](docs/images/gui-benchmark.png)

选择显卡后，点击 **开始 GEMM 测试**。默认采样 **7 轮并取中位数**，可选 11 或 15 轮。首次测速需下载约 **538 MiB** 的运算库，后续使用本地缓存。测试前应关闭其他占用显卡的程序，以减少外部负载对结果的影响。

性能对比以未解锁状态下的测试结果作为基线，流程如下：

1. **解锁前**：先检测为“未解锁”，再完成一次测速，程序自动保存基线。
2. **解锁后**：重启后重新检测为“已解锁”，使用相同轮次测速。
3. 结果表会显示 **本次成绩、未解锁成绩和提升百分比**，基线跨重启保留。

已解锁但尚未保存基线时，可在开机时选择 **Windows Boot Manager** 直接进入系统，检测确认处于未解锁状态后再测速。显卡、驱动或测试配置发生变化后，需要重新建立对应基线。

比较前，程序会核对两次测试的设备、驱动和配置。缺少有效基线、状态不明或测试前显卡明显繁忙时，提升栏留空并说明原因。TFLOPS 成绩用于性能对比，解锁状态以状态检测结果为准。

## 卸载与恢复

安装保留原有的 Windows 启动入口，卸载与恢复方式如下：

- **正常卸载**：点击 **移除 unlock 补丁**。程序按设备、路径及安装记录识别启动项，清除其长期与一次性启动引用后再移除文件，保留其他启动项的当前相对顺序和 Windows 回退入口。无法确认固件修改结果时，保留 ESP 文件并提示重试。
- **遇到启动问题**：进入 BIOS 启动菜单，选择原有的 **Windows Boot Manager**。
- **备份位置**：`%ProgramData%\GPUUnlocker\Backups`。
- **测速报告与基线**：`%LocalAppData%\GPUUnlocker` 下的 `Reports` 和 `Baselines` 文件夹。

## 简单原理

安装流程将上游 EFI 解锁器部署到现有系统启动分区，并新增 **GPU Unlock** 启动项。独立的首选设置步骤通过 Windows BCDEdit 修改 UEFI 固件启动顺序，需勾选提示后手动执行。通过该入口开机时，系统先执行解锁器，再启动 Windows。原有 Windows 启动入口予以保留，安装过程不调整分区。

状态检测通过上游 `ssdiag` 读取所选显卡的当前解锁判定。测速直接调用 NVIDIA cuBLAS，执行 `8192 × 8192` 稠密矩阵乘法；每项预热 2 秒后，通过 GPU 计时计算 TFLOPS，并抽样校验计算结果。FP32 与 TF32 分别测试，FP16/BF16 使用 FP32 累加。测试结果反映矩阵运算吞吐，实际模型性能仍取决于具体任务。

## 使用与引用的项目

项目使用或参考的主要组件如下：

- [nv-uefi-unlocker v2.1.5](https://github.com/physicsdolphin/nv-uefi-unlocker/releases/tag/v2.1.5)：实际 EFI 解锁器、资源生成工具及 `ssdiag`，由程序按需下载。
- [cmp50hx-unlock](https://github.com/xrip/cmp50hx-unlock)：20 系流程所需的一项上游启动资源。
- [NVIDIA cuBLAS](https://developer.nvidia.com/cublas)：矩阵乘法测速库，固定使用 12.8.4.1，首次测速时下载。
- [.NET / WPF](https://github.com/dotnet/wpf)：桌面界面与运行环境，发布 EXE 自带 .NET 运行时；系统操作使用 Windows 自带 PowerShell。
- [PyTorch CUDA BLAS 实现](https://github.com/pytorch/pytorch/blob/v2.9.0/aten/src/ATen/cuda/CUDABlas.cpp)：测速调用方式的参考，不是运行依赖。

本项目提供桌面界面、依赖管理、安装与恢复、测速及结果对比功能，实际解锁逻辑来自上游。各组件的使用与许可说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 版本与依赖锁定

项目采用 `主版本.次版本.修订号` 管理发布：不兼容变更递增主版本，新增兼容功能递增次版本，修复与文档调整在发布时递增修订号。当前发布版本 **v1.3.0** 增加带确认勾选的首选启动项设置，并改进启动顺序变化后的检测和移除处理。GEMM 测速、7/11/15 轮采样和基线对比保持原有口径。GUI 与上游解锁器的版本分别管理，对应关系如下：

| v1.3.0 对应组件 | 锁定版本 / 关系 |
| --- | --- |
| nv-uefi-unlocker（含 EFI、生成工具、ssdiag） | **v2.1.5**，整个发布包校验 SHA-256 |
| 固件生成输入 | NVIDIA **610.43.03**、CachyOS 工具包 **610.43.03-1**；不代表用户必须安装此驱动 |
| cuBLAS 测速库 | **12.8.4.1**，归档与 DLL 均校验 SHA-256 |
| cmp50hx-unlock 的 20 系启动资源 | 无声明版本，以固定文件 SHA-256 锁定 |
| .NET SDK / EXE 内置运行时 | **10.0.403 / 10.0.14**，构建时固定 |
| PyTorch | **v2.9.0**，仅参考，不安装或包含其运行库 |
| 历史 MMA 内核构建工具 | ptxas **12.8.93**；不参与当前 GEMM 测速 |
| Windows PowerShell / NVIDIA 驱动 | 使用系统 PowerShell **5.1**；驱动不锁定，但前后性能对比要求同版本 |

应用版本集中定义在 [Version.props](Version.props)，下载地址和文件哈希记录在 [dependencies.lock.json](dependencies.lock.json)。升级依赖时需同步更新清单与代码，并运行 `powershell -File tools/check-versions.ps1`；CI 会检查相关信息的一致性。测速方法发生变化时，还需更新测试协议，以避免不同口径的基线混用。

## 构建与国际化验证

使用 `global.json` 固定的 SDK。需要时运行 `powershell -File tools/bootstrap-sdk.ps1` 安装到本地，然后使用 `.tools\dotnet\dotnet.exe` 替代 `dotnet`。

```powershell
dotnet build GpuUnlocker.sln -c Release
dotnet run --project tests/GpuUnlocker.Tests -c Release
dotnet src/GpuUnlocker.App/bin/Release/net10.0-windows/GpuUnlocker.dll --smoke-test
powershell -File tools/check-versions.ps1
```

UI 冒烟测试验证中英文资源、语言切换及偏好持久化，并截取主界面、启动优先级及测速区域，不安装补丁或修改启动配置。新增界面文案时，需向两份 `.resx` 添加相同的资源键；XAML 使用资源绑定，C# 使用延迟求值的 `Localization.T(key, args)`，使已显示的提示也能随语言切换刷新。
