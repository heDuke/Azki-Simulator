# windows/scripts

Windows 引擎（M1）构建脚本。用法与引擎包说明见上级目录的 `README.md`“引擎（M1）”一节。

| 文件 | 作用 |
| --- | --- |
| `build-qemu.ps1` / `build-qemu.sh` | 在 MSYS2（x64：UCRT64，ARM64：CLANGARM64）中下载并校验 QEMU 10.1.0 源码，叠加 `engine/qemu-overlay`，只编译 `qemu-system-arm.exe`，再编译 `yaffs-image` / `legacy-ui-db` / `instance-props`，收集依赖 DLL 到 `build\windows-engine\qemu-win-<arch>` |
| `assemble-engine.ps1` / `assemble-engine.py` | 下载并按 SHA-256 校验 CPython 3.12.10 embeddable 与 pillow / littlefs-python / psutil 轮子，组装 `build\windows-engine\AzkiEngine-win-<arch>`（含 `Engine\manifest.json`） |
| `run-windows-tests.py` | 运行适用于 Windows 的上游 Python 单元测试（CI 使用） |

脚本不会下载、读取或打包任何固件。
