# ASR 优化与本地微调

本轮实现日期：2026-10-18。下文命令在项目根目录执行；数据、环境和实验产物仅保留在本地，不随仓库分发。

## 当前功能

设置窗口增加了以下独立选项，默认关闭实验功能，现有用户设置和模型不会自动被替换。

- **保留完整片段上下文**：先使用 Silero 判断是否含语音，无语音时直接返回空结果；有语音时保留完整输入，避免再次裁剪句首、句尾和中间停顿。
- **按需复核**：先使用不超过 beam=2 的解码，对低对数概率、重复文本、异常文本密度或有语音却无输出的片段最多重试一次 beam=5。它是启发式策略，不是准确率保证，也不保证每次都更快。
- **语音分段**：32 ms Silero 帧，0.35/0.20 启停阈值，512 ms 前后缓冲；状态最长保留 4.096 秒，避免长时间背景音乐造成状态偏移。仍为实验项，不能将它丢弃的所有音频认定为无配音。
- **计算精度**：设备默认、FP16、INT8_FLOAT16、INT8。CPU 应选择默认或 INT8。
- **日语人物名与术语**：最多 256 字符，仅在语言明确设为日语时应用。不会传递到英语或自动检测；换游戏时需要更换或清空。当前实测并非所有词表都会改善结果。
- 接话增强对不足 1.6 秒、不可能满足两侧各 0.8 秒要求的片段跳过词级时间对齐。确认接话边界后，若词级时间完整、有序且覆盖全部文字，则直接拆分整句识别结果，避免两次半句重识别；对齐不可靠时保留原两侧复核。声线模型、阈值和检测预算不变。

旧自动校准器尚不覆盖这些新选项，开启它们时会拒绝以不匹配的配置进行校准。微调实验不依赖自动校准器。

## 实测与限制

完整结果见 `artifacts/optimization-20261018/实验报告.md`，逐句预测和参数、哈希、测量数据均保存在同目录。

后续接话优化见 `artifacts/turn-optimization-20261018/实验报告.md`。同一主模型、FP16、beam=5、接话增强开启时，122 条旧样本的 CER 从 9.93% 降为 7.84%，耗时从 72.06 秒降为 56.72 秒；保留上下文的同条件对照为 8.82% → 5.50%、83.97 秒 → 60.84 秒。这是一次同机顺序测量，存在个别样本退化，不能外推为普遍准确率或端到端显示延迟提升。新预览版为 `artifacts/turn-optimization-preview/ACGTranslator.App.exe`，不自动修改用户设置或替换模型。新的 454 条采集候选单独整理，未用于本轮规则选择。

优化前后可使用 `optimization_bench.py --profiles baseline aligned baseline-context context --baseline-worker artifacts/turn-optimization-20261018/before/asr_worker.py`，其余参数沿用下方示例。英语分段预测按空格连接，日语按原顺序连接。新 worker 的 `turn` 字段提供检测/声纹计算耗时及额外解码次数，便于继续定位瓶颈。

目前只有一段录制提供的 122 条、558.27 秒已整理样本，标签来自截图修正和两个 Whisper 模型交叉核对，未逐条人工听音。样本筛选偏向模型已经容易识别的句子。

评估保留汉字、假名的区别；不通过人物名读音映射降低 CER。当前所有准确率数据都是同一场景的探索性结果，不是跨游戏或英语泛化结果。

连续回放使用实际 C# 分段器。连续录音只有稀疏标签，因此只报告已标注区间覆盖和 ASR 工作量，不能据此计算完整录音 CER 或无配音误报率。排队指标为离线测量服务时间驱动的模拟，不包含游戏争抢 GPU、实时采集抖动、翻译耗时，也不是句末到中文字幕的完整延迟。

## 环境

推理继续使用 `.venv`；训练使用独立 `.venv-training`。运行时不联网，训练脚本也强制离线。联网只发生在明确执行环境安装、官方模型下载时。

```powershell
.\scripts\setup_training.ps1
.\.venv-training\Scripts\python.exe scripts/download_training_model.py whisper-small
.\.venv-training\Scripts\python.exe scripts/download_training_model.py whisper-large-v3-turbo
```

模型下载固定官方仓库提交；不会在训练时自动拉取新版本。训练依赖锁定结果位于实验目录的 `training-environment.txt`。

## 数据划分与训练

```powershell
.\.venv-training\Scripts\python.exe scripts/finetune_asr.py prepare --dataset data/training-datasets/<dataset>/train.jsonl --out artifacts/new-training-data --exploratory
.\.venv-training\Scripts\python.exe scripts/finetune_asr.py train --data artifacts/new-training-data --model models/training/whisper-small --out artifacts/new-small-experiment --epochs 3
```

输出目录必须不存在，防止覆盖过去实验。准备阶段检查 PCM 哈希、语言、配对状态和重复文本/音频。正常模式要求至少三个录制组，这是本项目的评估策略，不是微调的理论最低要求。

单录制实验必须显式指定 `--exploratory`，按时间排序并在划分边界两侧留出 15 秒隔离区。当前实际为 78 条训练、18 条开发、21 条保留测试、5 条隔离。跨划分的重复内容会排除。

训练只更新 decoder self/cross attention 的 q/v LoRA，默认 rank=4、学习率 1e-4、累计 8 条样本。冻结编码器并缓存其输出，当前没有数据增强，缓存不能直接复用于增强后的音频。最多 3 轮，连续两轮验证 CER 不改善即早停。只以开发集选择模型，保留测试在选择结束后评估。

两次初始实验都未改善开发集 CER，已保留最后一轮 adapter 供审计，未合并或替换应用模型。如果未来实验改善开发集，脚本会生成 `best-adapter` 和 `merged`；仍需独立录制与英语回归验证才能考虑上线。导出可用训练环境的 `ct2-transformers-converter --model <merged> --output_dir <new-output> --quantization float16 --copy_files tokenizer.json preprocessor_config.json`，并单独验证转换前后结果。

## 复现评估

```powershell
dotnet run --project tests/ACGTranslator.Replay -c Release -- <capture.wav> <new-replay-folder> .venv/Lib/site-packages/faster_whisper/assets/silero_vad_v6.onnx
.\.venv\Scripts\python.exe scripts/optimization_bench.py --dataset <train.jsonl> --out <new-report-folder> --baseline-worker artifacts/optimization-20261018/before/src/ACGTranslator.App/Resources/asr_worker.py --profiles baseline context context-fast
```

`--turn-model models/speakers/wespeaker-resnet34/speaker.onnx` 用于开启接话增强的对照。连续回放增加 `--suite replay --replay <new-replay-folder>`。不带 `--turn-model` 的结果必须注明接话增强关闭。

测试：`dotnet test tests/ACGTranslator.Tests -c Release`，以及 `.venv/Scripts/python.exe -m unittest discover -s scripts -p 'test_*.py'`。隐藏窗口联通测试位于 `tests/ACGTranslator.OptimizationSmoke`。

## 后续需要的数据

优先整理剩余困难样本，并补充不同场景、角色、背景音乐和真实快速接话；另留独立录制用于最终验收。中文译文尚未核验，不用于训练翻译模型。当前未进行模型蒸馏、删层、通用日英混合训练或自动上线；这些步骤应在数据扩充和基线验证后进行。


## 2026-10-19：另一段录制的固定配置复测

未修改应用实现、模型权重或用户配置，使用第二段录制的70条样本按不同顺序重复三轮。未开启保留上下文时，接话优化后的CER为5.45%（旧流程7.29%），总耗时中位数42.80秒 → 33.10秒。保留完整上下文组合的优化后CER为2.93%；旧流程的该组合受到一条严重重复输出影响，不能只看整体改善倍数。

新70条有6条台词在旧122条中出现，去重64条结果、三轮全部结果、逐句退化案例、异常值敏感性分析与耗时分解见`artifacts/cross-recording-20261019/复测报告.md`。不同录制不等于不同音源；此集仍受Whisper筛选偏差影响，不能称无偏独立金标。没有重新打包程序，现有`artifacts/turn-optimization-preview/ACGTranslator.App.exe`与本次复测实现一致。

## 2026-10-25：声线提速与字幕延迟统计

保持主模型、声线模型、候选、窗口、阈值与预算不变；较多核心的机器采用4线程声线推理，复用fbank配置并减少复制。两轮反序、同70条录音的普通配置耗时中位数30.29→24.29秒（减少19.8%），保留上下文33.89→26.54秒（减少21.7%）。逐条文本、边界、调用数均一致；这是降低等待时间，不是准确率改善。单独声线测试的进程CPU总时间约增加23%，游戏资源竞争仍需验证。

新增“延迟统计”窗口，可查看、导出、清空最近512条片段/字幕阶段记录。实时输入记录分段器估计句末到控件更新；离线录音不伪造采集时钟。统计口径见 [latency-diagnostics.md](latency-diagnostics.md)。详细报告在 `artifacts/speaker-latency-20261025/实验报告.md`，新可运行预览版在 `artifacts/speaker-latency-preview/ACGTranslator.App.exe`。该次实验未修改用户设置或进行训练；软件发布信息以仓库版本和 Release 说明为准。
