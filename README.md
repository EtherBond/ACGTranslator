# ACGTranslator v1.0.1（实验性接话增强） — 离线实时翻译器

Windows 桌面应用：捕获电脑正在播放的日语或英语语音，实时识别、翻译为简体中文，并在始终置顶的悬浮字幕窗口显示。运行时完全离线。

## 功能

- 悬浮字幕窗口：原文/中文双行显示，可拖动、缩放，记住位置和大小。
- WASAPI Loopback 捕获所选输出设备的系统声音，转换为 16 kHz 单声道后按能量分段。
- 本地 Whisper（faster-whisper）识别，Silero VAD 过滤非语音片段，可固定语音语言或自动检测日语/英语。
- 本地 Argos Translate 翻译；日语经英语中转到中文。
- 模型管理窗口支持识别模型、翻译引擎和显卡运行库的下载、安装、删除与切换；下载时联网，运行时保持离线。
- 识别与翻译流水线并行，字幕按语音顺序输出；识别跟不上时丢弃最旧的待处理片段并在状态栏提示。
- 历史窗口支持搜索、多选删除、清空，以及按会话导出中文或双语 SRT。
- 原始音频只在内存中流转，不保存到磁盘。

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
确认换人后分别识别、翻译并显示两句；历史和双语 SRT 也分别保存。

本版每个音频片段最多核查出一个换人边界，极短回应和同时说话暂不能可靠处理。
正常片段也有少量词时间戳/特征检测成本；确认边界后会多做两次识别。
英语合成语音测试已验证分段路径，真实日语动漫/英语对话精度仍待评测。

详见 [开发方案](docs/v1.0.1-plan.md) 与 [验证记录](docs/v1.0.1-validation.md)。
运行功能回归：python scripts/test_turns.py；模型冒烟：python scripts/smoke_turns.py。

## 当前限制

- 只能捕获整个输出设备的混音，还不能按应用捕获。
- 只支持日语和英语输入。
- 启动时会预加载模型，CPU 上大约需要数秒；首个片段仍会比后续稍慢。
- CPU 模式优先保证兼容性；CUDA 模式需要自行准备对应的 CUDA/cuDNN 运行库。
- 外部翻译服务尚未实现，`ITextTranslator` 接口是预留的接入点。

更改记录见 [CHANGELOG.md](CHANGELOG.md)。
