# Azki Simulator for Windows（WinUI 3）

这是 Azki Simulator 的 Windows 原生桌面端移植（WinUI 3 + .NET 11），位于 `windows-winui` 分支。
macOS 版（Swift，`Sources/AzkiSimulator`）不受影响；Python 运行时与 QEMU 引擎继续使用仓库原位置的代码。

> 当前进度：**M0 分支与骨架**。程序只有一个带 Mica 背景和 NavigationView 的空壳窗口，还不能启动模拟器。
> 引擎（M1）、画面显示（M2）等将在后续里程碑中实现。

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
└── scripts/                    Windows 引擎构建脚本（M1 起）
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

## CI 产物

`.github/workflows/windows-build.yml` 在推送到 `windows-winui` 分支（仅当 `windows/` 或该工作流变化）、向该分支提 PR、或手动触发时运行，
分别在 `windows-latest`（x64）和 `windows-11-arm`（ARM64）上还原、构建、测试、自包含发布，并短暂启动程序做冒烟测试。
产物 `AzkiSimulator-win-x64`、`AzkiSimulator-win-arm64` 可在 Actions 运行页面下载，解压后运行 `AzkiSimulator.exe`。

产物未签名，首次运行时 SmartScreen 可能提示，选择“更多信息 → 仍要运行”即可。

## M0 验收清单（x64 与 ARM64 实机各做一次）

- [ ] `dotnet --version` 显示本机 SDK（≥ 11.0.100-rc.2），`dotnet build` 通过
- [ ] `dotnet test` 通过
- [ ] `dotnet run --project src\Azki.App\Azki.App.csproj` 打开带 Mica 背景和左侧导航的空窗口
- [ ] `win-x64` 与 `win-arm64` 都能 `dotnet publish`，发布目录中的 exe 可直接运行
- [ ] 系统 build 低于 26300 时，窗口顶部只显示一条可关闭的警告，程序照常运行；“设置”页显示系统版本、进程架构和 .NET 运行时版本

## 许可

与仓库其余部分一致，GPL-2.0-only（见仓库根目录 `LICENSE`）。
