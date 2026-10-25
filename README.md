# ACGTranslator v1.0.2 — 离线实时翻译器

Windows 桌面应用：捕获电脑正在播放的日语或英语语音，实时识别、翻译为简体中文，并在始终置顶的悬浮字幕窗口显示。运行时完全离线。

[下载 Windows x64 发布包](https://github.com/EtherBond/ACGTranslator/releases/latest) · [首次安装与升级](release-tools/README-Release.md) · [v1.0.2 更新说明](release-tools/RELEASE-NOTES-v1.0.2.md)

v1.0.2 增加字幕延迟统计、游戏双语 OCR 采集和录音校准工具，优化快速接话后的文字拆分与声线计算。采集、校准与识别实验选项仍有各自限制，详见下方说明。发布包内置 .NET 运行时，Python 与模型需首次联网安装。

## 功能

- 悬浮字幕窗口：原文/中文双行显示，可拖动、缩放，记住位置和大小。
- WASAPI Loopback 捕获所选输出设备的系统声音，转换为 16 kHz 单声道；默认按能量分段，可选实验性 Silero 语音分段。
- 本地 Whisper（faster-whisper）识别，Silero VAD 过滤非语音片段，可固定语音语言或自动检测日语/英语。
- 本地 Argos Translate 翻译；日语经英语中转到中文。
- 模型管理窗口支持识别模型、翻译引擎和显卡运行库的下载、安装、删除与切换；下载时联网，运行时保持离线。
- 识别与翻译流水线并行，字幕按语音顺序输出；识别跟不上时丢弃最旧的待处理片段并在状态栏提示。
- 历史窗口支持搜索、多选删除、清空，以及按会话导出中文或双语 SRT。
- 实时翻译时原始音频只在内存中流转；主动使用游戏采集或校准时会保存录音/切片和测试记录。

## 目录

```
src/ACGTranslator.App     WPF 应用
  Resources/*.py                      本地识别/翻译 worker（JSON Lines 协议）
tests/ACGTranslator.Tests xUnit 单元测试
scripts/prepare_models.py             联网准备模型
scripts/smoke_test.py                 worker 端到端冒烟测试
```

设置与历史保存在 `%LOCALAPPDATA%\ACGTranslator\`（可用环境变量 `SUBTITLE_DATA_DIR` 覆盖）。旧版 `%LOCALAPPDATA%\OfflineSubtitleTranslator\` 中的设置和历史会自动读取。

## 准备运行环境

应用目标为 .NET 8 Windows，需要 Python 3.10 以上。仓库的 .slnx 解决方案需 .NET SDK 9.0.200 或更新版本；使用 .NET 8 SDK 时可直接构建和测试对应的 .csproj。模型准备需要联网，之后运行应用不再联网。

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements-asr.txt
.\.venv\Scripts\python.exe .\scripts\prepare_models.py
```

脚本会下载 Whisper base 模型到 `models/asr/whisper-base`，并安装英语→中文、日语→英语两个 Argos 翻译包到 `models/translators/argos`。

应用会从可执行文件所在目录向上查找同时包含 `.venv` 和 `models` 的目录作为项目根目录，也可以用环境变量 `SUBTITLE_ROOT` 指定，或在设置窗口的“高级”中直接选择 Python 和模型目录。

## 构建、测试与运行

```powershell
dotnet build .\ACGTranslator.slnx
dotnet test .\ACGTranslator.slnx
.\.venv\Scripts\python.exe .\scripts\smoke_test.py
.\src\ACGTranslator.App\bin\Debug\net8.0-windows\ACGTranslator.App.exe
```

## v1.0.1 快速接话增强

设置中的“快速接话增强”只临时比较当前片段的声音变化，不注册身份、不保存声纹。
在模型管理下载 **WeSpeaker 接话模型**，更新 Python 依赖后保存设置。
未安装模型或关闭增强时保持普通流程。模型推理出错时提示并回退。
确认换人后，词级时间对齐可靠时直接拆分整句识别结果；不可靠时保留两侧重识别。两句分别翻译、显示并写入历史和双语 SRT。

本版每个音频片段最多核查出一个换人边界，极短回应和同时说话暂不能可靠处理。
正常片段也有词时间戳/特征检测成本；较多核心的设备默认增加声线 CPU 并行度以缩短等待，可能增加 CPU 总用量。
英语合成语音测试已验证分段路径，真实日语动漫/英语对话精度仍待评测。

原始方案与旧版验证见 [开发方案](docs/v1.0.1-plan.md) 和 [v1.0.1 验证](docs/v1.0.1-validation.md)；本版见 [v1.0.2 验证](docs/v1.0.2-validation.md)。
运行功能回归：python scripts/test_turns.py；模型冒烟：python scripts/smoke_turns.py。

## 当前限制

- 只能捕获整个输出设备的混音，还不能按应用捕获。
- 只支持日语和英语输入。
- 启动时会预加载模型，CPU 上大约需要数秒；首个片段仍会比后续稍慢。
- CPU 模式优先保证兼容性；CUDA 模式需要自行准备对应的 CUDA/cuDNN 运行库。
- 外部翻译服务尚未实现，`ITextTranslator` 接口是预留的接入点。

更改记录见 [CHANGELOG.md](CHANGELOG.md)。

## 延迟统计

主窗口“延迟统计”可查看识别/翻译排队、处理耗时、控件更新及丢弃、失败，支持导出 JSON 和清空。只在内存保留最近 512 条片段/字幕记录，不包含台词或音频。关闭程序即清除；需要留档时先导出。句末为分段器估计，控件更新时间不等于显示器呈现时间。详见 [统计口径](docs/latency-diagnostics.md)。

## 游戏训练素材采集（开发预览）

在“游戏采集”窗口框选日文/中文字幕。停止保存后自动生成包含全部候选、截图和连续录音的整理包，无需逐条确认；在助手对话中交接后进行纠错、核对配音并导出训练数据。也可用 `ACGTranslator.App.exe --capture` 独立启动。使用方法、数据目录和验证限制见 [游戏采集说明](docs/game-capture.md)。

## 录音自动校准（开发预览）

点击“自动校准”，导入原文 SRT 与对应录音，或多份已复核的游戏采集清单。比较本地模型、CPU/GPU 和解码搜索宽度，独立样本验证通过后自动应用并备份设置；没有改善则保留原配置。支持取消、报告查看和恢复。独立入口为 `ACGTranslator.App.exe --calibrate`。素材要求、使用步骤及限制见 [自动校准说明](docs/automatic-calibration.md)。

本地识别优化、连续录音回放与 LoRA 实验：[开发说明](docs/asr-optimization.md)。新选项可在设置中启用，实验模型不会自动替换当前模型。
