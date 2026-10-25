# Azki Simulator for Windows（WinUI 3）

这是 Azki Simulator 的 Windows 原生桌面端移植（WinUI 3 + .NET 11），位于 `windows-winui` 分支。
macOS 版（Swift，`Sources/AzkiSimulator`）不受影响；Python 运行时与 QEMU 引擎继续使用仓库原位置的代码。

> 当前进度：**M2 画面显示**。在 App 里选择固件、选择机型、启动，就能看到实时刷新的设备画面（见下文“App（M2）”）。
> 触控（M3，已有一个简单预览）、实例库、设置、日志等将在后续里程碑中实现。引擎也可以单独通过命令行运行（见“引擎（M1）”）。

## 目录结构

```
windows/
├── AzkiSimulator.slnx          解决方案（VS 2026 / dotnet CLI）
├── global.json                 .NET SDK 版本（允许向上滚动到本机更新的 SDK）
├── Directory.Build.props       公共 MSBuild 设置（TFM、Nullable、分析器等）
├── Directory.Packages.props    NuGet 包版本集中管理（Windows App SDK 2.5.1 组件包等）
├── src/
│   ├── Azki.Core/              Swift Core 的 C# 移植（net11.0，无 UI 依赖，可跨平台测试）
│   └── Azki.App/               WinUI 3 桌面程序（net11.0-windows10.0.26100.0，非打包）
├── tests/
│   └── Azki.Core.Tests/        Azki.Core 单元测试（xUnit v3，Microsoft.Testing.Platform）
├── engine/                     引擎命令行入口 azki-engine.cmd / azki-engine.py（打包进引擎包）
└── scripts/                    引擎构建：build-qemu.ps1/.sh（MSYS2 编译 QEMU）、assemble-engine.ps1/.py（组装引擎包）
```

## 环境要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 11，x64 或 ARM64。推荐 Windows 11 26H2（build 26300）及以上；更低版本启动时只显示一次警告，不会阻止运行 |
| .NET SDK | **.NET 11 RC2**（`11.0.100-rc.2.26504.105`）或更新的 .NET 11 / 更高版本 SDK。下载：<https://dotnet.microsoft.com/download/dotnet/11.0>，ARM64 设备请装 **Arm64** 版安装包 |
| IDE（可选） | Visual Studio 2026，勾选工作负载 **“WinUI 应用程序开发”**（Windows App SDK / WinUI 3 模板与工具）以及 **“.NET 桌面开发”**；也可只用命令行 |
| Windows App SDK | 通过 NuGet 自动获取（只引用 2.5.1 对应的组件包 WinUI、Foundation、InteractiveExperiences，不含 AI/ML/Search/Widgets），**不需要**单独安装运行时：程序以自包含方式发布 |

`global.json` 设置了 `rollForward: latestMajor` 和 `allowPrerelease: true`：只要本机装的 SDK 不低于 RC2，就会自动使用本机最新的 SDK。
如果本机 SDK 比 RC2 还旧，`dotnet` 会报找不到 SDK，此时请升级 SDK，或把 `global.json` 中的 `version` 改成 `dotnet --version` 的输出。

检查 SDK：

```powershell
dotnet --list-sdks
```

## 获取代码

```powershell
git clone https://github.com/heDuke/Azki-Simulator.git
cd Azki-Simulator
git switch windows-winui
cd windows
```

## 命令行构建与运行

以下命令在 `windows\` 目录下执行。`Azki.App` 不指定架构时会自动使用本机架构（x64 机器用 `win-x64`，ARM64 机器用 `win-arm64`）。

```powershell
# 1. 运行 Core 单元测试
dotnet test --project tests\Azki.Core.Tests\Azki.Core.Tests.csproj

# 2. 直接运行（Debug，本机架构）
dotnet run --project src\Azki.App\Azki.App.csproj

# 3. 构建整个解决方案（请指定平台：x64 或 ARM64）
dotnet build AzkiSimulator.slnx -p:Platform=x64      # x64 机器
dotnet build AzkiSimulator.slnx -p:Platform=ARM64    # ARM64 机器
```

### 发布自包含版本（解压即用）

```powershell
# x64
dotnet publish src\Azki.App\Azki.App.csproj -c Release -r win-x64 -p:Platform=x64 -o publish\win-x64

# ARM64
dotnet publish src\Azki.App\Azki.App.csproj -c Release -r win-arm64 -p:Platform=ARM64 -o publish\win-arm64
```

发布目录中的 `AzkiSimulator.exe` 可直接双击运行，目标机器不需要安装 .NET 或 Windows App SDK 运行时。
x64 版本也能在 ARM64 设备上通过仿真运行，但请在 ARM64 设备上测试 `win-arm64` 版本。

## 使用 Visual Studio 2026

1. 打开 `windows\AzkiSimulator.slnx`。
2. 工具栏平台选择 **x64** 或 **ARM64**（与本机一致），启动项目选 `Azki.App`，配置文件为 “Azki.App (Unpackaged)”。
3. 按 F5 运行；“测试资源管理器”中可运行 `Azki.Core.Tests`。

## App（M2）：在窗口里运行模拟器

### 下载

在 GitHub Actions 的 **Windows engine (QEMU + Python runtime)** 运行页面下载与本机架构一致的产物：

| 产物 | 内容 |
| --- | --- |
| `AzkiSimulator-full-win-x64` / `AzkiSimulator-full-win-arm64` | **推荐**：`AzkiSimulator.exe` 加同级的 `engine\`（完整引擎包），解压即可运行 |
| `AzkiEngine-win-*` | 只有引擎（命令行用，见下文 M1） |
| `AzkiSimulator-win-*`（Windows build 工作流） | 只有 App，不含引擎 |

解压到**只含 ASCII 字符、不含逗号**的目录（例如 `C:\Azki\AzkiSimulator-full-win-x64`）。QEMU 只能接收 ASCII 路径。

### 程序如何找到引擎

按顺序查找，找到第一个完整的引擎包（含 `Engine\manifest.json`、Python 和 `Runtime\azki-simulator.py`）就使用它：

1. 环境变量 `AZKI_ENGINE_BUNDLE` 指向的目录；
2. `AzkiSimulator.exe` 同级的 `engine\`、`AzkiEngine-win-<架构>\`、`AzkiEngine\`；
3. 上一级目录中的同名目录（例如把 `AzkiSimulator-win-x64` 和 `AzkiEngine-win-x64` 两个产物解压到同一个父目录下）。

如果都没找到，工作区顶部会显示“引擎缺失”，并列出查找过的路径。

数据（导入的固件库、会话）放在 `%LOCALAPPDATA%\AzkiSimulator\{Library,Sessions}`。如果这个路径含非 ASCII 字符
（例如中文用户名），会改用 `%SystemDrive%\AzkiSimulatorData`。也可以用环境变量 `AZKI_DATA_ROOT` 指定其他目录。

### 使用

1. 运行 `AzkiSimulator.exe`，进入“工作区”。
2. 点“浏览…”选择 OTA 包（例如 `xxxx_upd_miwear.zip`），或者把路径粘贴进输入框后按 Enter。程序会导入固件，并识别它适用的机型。
3. 如果固件适用于多个机型（例如小米手环 10 Pro 的金属版和陶瓷版），在“设备型号”里选择一个。只有一个机型时会自动选中。
4. 点“启动”。状态行依次显示“正在准备会话并启动引擎…”和“运行中 mm:ss · 状态 running/running · 336×480 · …”。
   **固件开机需要时间**：第一帧（黑屏上的启动动画）大约在 5–10 秒出现，表盘大约在 20–60 秒后出现，ARM64 上可能更慢。
   程序在这期间不会超时。
5. 右侧是实时画面（按设备比例缩放）。用鼠标在画面上点按或拖动可以操作手环；这是 M3 的初步预览，例如从上往下拖动会打开消息页。
6. 点“停止”，或者直接关闭窗口（会先停止模拟器，最多等待 20 秒）。

出错时，工作区会显示红色的“出错了”提示，内容为引擎或桥接返回的原因。会话目录 `Sessions\s-<时间>-<随机>\` 下有
`serve.log`（桥接）、`session\m-<id>\adapter-runner.log`、`qemu.log`、`uart1.log`、`uart2.log`，可用于排错。
小米手环 10 Pro 的固件串口日志在 `uart1.log`、`uart2.log`，`uart0.log` 通常为空。

### 进程与清理

`serve`、运行器和 QEMU 都在 App 创建的 Job Object 里运行，并设置了 `KILL_ON_JOB_CLOSE`。即使 App 崩溃或被任务管理器结束，
系统也会结束整棵进程树，不会留下 `python.exe` 或 `qemu-system-arm.exe`。正常停止时，App 先通过控制协议发送 `stop`，
由运行器回收 QEMU，再结束 `serve`。

### 帧与日志（实现说明）

- 帧文件 `session\frame.raw`：32 字节头（`Q66FRAME`、版本、序号、宽、高、行宽、格式）加小端 RGB565 像素。App 以
  `FileShare.ReadWrite | FileShare.Delete` 打开文件，读完立即关闭，这样不会阻塞 Python 端用 `os.replace` 发布新帧。
  App 按头部序号判断是否是新帧（偶数表示内容完整），不依赖文件修改时间。画面约 60 Hz 轮询，同一时刻最多只有一次读取。
- `qemu.log`：Windows 上运行器会给 BEST1503 机型加 `quiet-panel-te-log=on`，不再为每个 60 Hz 面板 TE 信号写一行日志
  （信号和中断本身不变）。实测开机阶段一次性写入约 1.9 MB，之后每分钟约 17 KiB（不加此选项时每分钟约 250 KB）。
  因此 `result.json` 中的 `panel_te_edge_count` 在 Windows 上为 0。

### M2 验收清单（x64 与 ARM64 实机各做一次）

- [ ] 解压 `AzkiSimulator-full-win-<架构>`，运行 `AzkiSimulator.exe`，工作区没有“引擎缺失”提示
- [ ] 选择 OTA 包后列出机型；多个机型时可以选择
- [ ] 点“启动”后几秒内出现画面，之后显示表盘，画面持续刷新（状态行中的帧数和序号不断增加）
- [ ] 点“停止”后状态回到“就绪”，任务管理器中没有残留的 `python.exe` / `qemu-system-arm.exe`
- [ ] 运行中直接关闭窗口，同样没有残留进程
- [ ] ARM64：记下表盘出现所需的时间和大致帧率，并与 x64 对比

## 引擎（M1）：命令行运行模拟器

引擎包 `AzkiEngine-win-x64` / `AzkiEngine-win-arm64` 包含：

| 目录 | 内容 |
| --- | --- |
| `Engine\project\backend\build\` | `qemu-system-arm.exe`（QEMU 10.1.0 + `engine/qemu-overlay`，仅 arm-softmmu）、MSYS2 运行库 DLL、`yaffs-image.exe` / `legacy-ui-db.exe` / `instance-props.exe` |
| `Engine\python\` | CPython 3.12.10 embeddable（标准库解压在 `Lib\`），`Lib\site-packages` 内含 pillow 12.3.0、littlefs-python 0.19.0、psutil 7.2.2 |
| `Engine\manifest.json` | 引擎文件清单（每个文件的大小与 SHA-256），运行前校验 |
| `Runtime\` | 与 macOS 共用的 Python 运行时（`azki-simulator.py` + `azki_simulator\`） |
| `Firmware\catalog.json` | 只有硬件预设元数据，**不含任何固件** |
| `azki-engine.cmd` | 命令行入口 |

固件不在仓库和引擎包里，也不会被下载：请使用你自己的 OTA 固件包。

### 获取引擎包

方式一（推荐）：在 GitHub Actions 的 **Windows engine (QEMU + Python runtime)** 运行页面下载产物
`AzkiEngine-win-x64` 或 `AzkiEngine-win-arm64`（与本机架构一致），解压到一个**只含 ASCII 字符、不含逗号**的路径，例如 `C:\Azki\AzkiEngine-win-arm64`。

方式二：本机构建（需要 [MSYS2](https://www.msys2.org/)，默认装在 `C:\msys64`；x64 用 UCRT64，ARM64 用 CLANGARM64）：

```powershell
# 在仓库根目录；第一次加 -InstallDependencies 用 pacman 安装编译依赖
pwsh windows\scripts\build-qemu.ps1 -Arch arm64 -InstallDependencies   # x64 机器写 -Arch x64
pwsh windows\scripts\assemble-engine.ps1 -Arch arm64                    # 输出 build\windows-engine\AzkiEngine-win-arm64
```

### 自检（不需要固件）

```powershell
cd C:\Azki\AzkiEngine-win-arm64
.\azki-engine.cmd selftest
```

输出 `"ok": true` 表示：引擎清单全部文件哈希正确、QEMU 含 `best1503` / `azki-q65` / `azki-p65` 机型、
控制通道（127.0.0.1 动态端口 + 每会话随机 token）握手正常且拒绝无 token 的连接、Ctrl+Break 能正常停止服务、Job Object 进程组可整体结束。

### 用自己的固件运行

```powershell
cd C:\Azki\AzkiEngine-win-arm64
.\azki-engine.cmd run --firmware D:\Firmware\你的OTA包.zip
# 一个 OTA 适用多个机型时会列出候选，按提示加 --model，例如：
.\azki-engine.cmd run --firmware D:\Firmware\ota.zip --model miwear.watch.n66cn
# 只跑 120 秒后自动停止：
.\azki-engine.cmd run --firmware D:\Firmware\ota.zip --seconds 120
```

`run` 依次执行 `library-import` → `prepare-bundled` → `serve` → `start`，之后每 2 秒打印一次会话状态和 `frame.raw`（画面帧文件）是否在更新；按 **Ctrl+C** 停止。
固件库默认在 `%LOCALAPPDATA%\AzkiSimulator\Library`，会话目录在 `%LOCALAPPDATA%\AzkiSimulator\Sessions\s-<时间>-<随机>\`（可用 `--library` / `--sessions` 修改，同样只用 ASCII 路径）。

判断是否启动成功：状态行出现 `state=running adapter=running`，且 `frame.raw=... bytes, updated` 的时间不断变化。
排错日志（都在会话目录下）：`serve.log`（桥接进程）、`session\m-<id>\adapter-runner.log`（运行器）、`session\m-<id>\stderr.log` / `qemu.log` / `uart0.log`（QEMU 与固件串口）。

另开一个终端可以对运行中的会话发命令：

```powershell
.\azki-engine.cmd call "$env:LOCALAPPDATA\AzkiSimulator\Sessions\s-...\session" status
.\azki-engine.cmd call "$env:LOCALAPPDATA\AzkiSimulator\Sessions\s-...\session" screenshot
```

### M1 已知限制

- 没有 macOS 那样的文件系统沙箱：运行器与 QEMU 在一个“关闭即结束”的 Job Object 中运行，输入文件仍按计划固定，写入只发生在会话目录。
- 虚拟蓝牙对端（virtual peer）依赖 macOS 加密库，Windows 上暂不可用；`run` 不创建实例数据盘（不传 `--instance-id`），因此不涉及它。
- QMP 由 QEMU 监听 127.0.0.1 上预先选好的空闲端口（QMP 本身没有 token）；选端口与 QEMU 绑定之间有极短的竞争窗口。
- 路径需为 ASCII（QEMU 通过 ANSI 代码页接收路径）。

### M1 验收清单（x64 与 ARM64 实机各做一次）

- [ ] `azki-engine.cmd selftest` 输出 `"ok": true`
- [ ] `azki-engine.cmd run --firmware <你的固件>` 出现 `state=running`，`frame.raw` 持续更新
- [ ] Ctrl+C 后显示 `stop:`，任务管理器中没有残留的 `qemu-system-arm.exe` / `python.exe`

## CI 产物

`.github/workflows/windows-build.yml` 在推送到 `windows-winui` 分支（仅当 `windows/` 或该工作流变化）、向该分支提 PR、或手动触发时运行，
分别在 `windows-latest`（x64）和 `windows-11-arm`（ARM64）上还原、构建、测试、自包含发布，并短暂启动程序做冒烟测试。
产物 `AzkiSimulator-win-x64`、`AzkiSimulator-win-arm64` 可在 Actions 运行页面下载，解压后运行 `AzkiSimulator.exe`。

产物未签名，首次运行时 SmartScreen 可能提示，选择“更多信息 → 仍要运行”即可。

`.github/workflows/windows-engine.yml` 在 `engine/`、`Sources/AzkiSimulator/Runtime/`、`tests/`、`windows/scripts/`、`windows/engine/` 变化时运行：
用 MSYS2 编译 QEMU 与辅助工具，组装引擎包，运行 `selftest --require-graceful-stop` 和适用于 Windows 的上游 Python 单元测试，
上传产物 `AzkiEngine-win-x64`、`AzkiEngine-win-arm64`；然后针对这个引擎包运行 Azki.Core 测试（含真实 `serve` 的
TCP + token 控制连接、错误 token 被拒、Job Object 结束整棵进程树），发布 App，把引擎放进 `engine\`，冒烟启动后上传
`AzkiSimulator-full-win-x64`、`AzkiSimulator-full-win-arm64`。该工作流在 `windows/src/`、`windows/tests/` 变化时也会运行。

## M0 验收清单（x64 与 ARM64 实机各做一次）

- [ ] `dotnet --version` 显示本机 SDK（≥ 11.0.100-rc.2），`dotnet build` 通过
- [ ] `dotnet test` 通过
- [ ] `dotnet run --project src\Azki.App\Azki.App.csproj` 打开带 Mica 背景和左侧导航的空窗口
- [ ] `win-x64` 与 `win-arm64` 都能 `dotnet publish`，发布目录中的 exe 可直接运行
- [ ] 系统 build 低于 26300 时，窗口顶部只显示一条可关闭的警告，程序照常运行；“设置”页显示系统版本、进程架构和 .NET 运行时版本

## 许可

与仓库其余部分一致，GPL-2.0-only（见仓库根目录 `LICENSE`）。
