# ACGTranslator v1.0.0 — Windows x64 离线实时翻译器

捕获电脑正在播放的英语或日语，显示原文和中文字幕，支持置顶悬浮窗、模型管理、会话历史、搜索清理和 SRT 导出。

## 首次运行

1. 完整解压 ZIP 到可写目录，例如 `E:\Apps\ACGTranslator`。不要在压缩包内直接启动，也不要放到 Program Files。
2. 安装 64 位 Python 3.10–3.13（建议 3.12），安装时启用 Python Launcher。下载入口：https://www.python.org/downloads/windows/
3. 联网双击 `Setup.cmd`。它会在当前目录创建 `.venv`、安装依赖、下载 Whisper base 和 Argos 日英中翻译包，并运行 worker 冒烟测试。下载耗时取决于网络，需为 Python 依赖和模型预留数 GB 空间。
4. 出现 `Setup completed` 后双击 `Start.cmd`。在设置中选择输出设备和输入语言，播放带语音的音频，点击“开始识别”。
5. 已知音频语言时固定为日语或英语，可避免短句自动检测误判。可以通过“模型”下载其他识别/翻译模型，首次下载仍需联网。

本包已内置 .NET 8 Windows x64 运行时，不要求安装 .NET SDK。**本包不含 Python、Python 依赖或模型权重，不是解压即可在全新断网电脑上使用的全量离线包。** 完成初始化后，使用已安装模型的识别和翻译可离线运行。Setup 只创建本地虚拟环境，不修改全局 Python 包。

## 文件与数据管理

- 程序和 .NET 运行时：包根目录；worker 和模型目录：`Resources`。
- Python 环境：`.venv`；模型：`models`；下载缓存：`.cache`。
- 用户设置和历史：`%LOCALAPPDATA%\ACGTranslator`。旧版 `%LOCALAPPDATA%\OfflineSubtitleTranslator` 会自动读取；在历史窗口搜索、删除或导出记录；删除程序目录不会删除历史。
- 模型请通过模型管理窗口清理。关闭程序后可删除 `.cache` 下载缓存。
- `.venv` 引用安装它的基础 Python，不能直接复制到另一台电脑使用。换电脑后重新运行 Setup。
- 使用其他模型时，可运行 `Setup.cmd -SkipModels` 仅安装依赖，再通过应用的模型管理下载所需模型。
- 已有开发版设置如果包含旧 Python 或模型绝对路径，请在设置的高级选项清空这些覆盖路径，或改为当前发布目录。

## 已知限制

- 面向 Windows 10/11 x64；捕获整个输出设备混音，暂不支持只捕获一个应用。
- 仅面向英语和日语输入，外部翻译服务尚未实现。
- CPU 可以运行默认模型，处理速度取决于硬件。GPU 加速需额外下载相应运行组件；GPU 驱动需由用户安装。
- 未提供代码签名，Windows 可能显示未知发布者提示；下载后可用随附 SHA256SUMS.txt 检查文件完整性。
- 当前识别阈值的改动已通过构建及代码回归检查，但尚未用大规模真实语料评测准确率提升。

## 故障排查

初始化失败时查看终端错误并重新运行 Setup.cmd。无需删除历史。模型加载失败时检查高级设置路径和模型是否完整。断网前应至少成功运行一次初始化和英日翻译测试。

查看构建来源：`BUILD-INFO.json`；修改记录：`CHANGELOG.md`；第三方声明：`THIRD-PARTY.md`。
