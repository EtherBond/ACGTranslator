# ACGTranslator：识别架构升级与 Galgame 自动评测方案

日期：2026-10-14。状态：方案 + 第一版离线评测工具；尚未改变正式软件的识别逻辑。

## 1. 建议的突破方向

把目前“每段音频一律完整识别”的方式，升级为**普通音频自适应识别 + 可选的游戏资源定位**。评测先行，架构逐步落地。不能承诺在未知硬件上同时达到任意精度和延迟。

### 普通模式：把算力用在真正不确定的片段上

1. 保留现有离线 ASR worker，以本机已有 base、small、large-v3-turbo 建立质量/耗时曲线。日语增加 Kotoba-Whisper v2 的候选对照；英语继续用通用模型。不要用日语专用模型覆盖英文模式。
2. 用语音活动检测、最大段长和稳定前缀共同控制流式切分。先在影子模式试验短窗口初稿、末尾重叠与提交边界；避免 6 秒截断丢字，去重只作用于时间重叠部分，保留真实重复台词。
3. 初稿采用较小 beam；只在低置信度、语言冲突、异常重复、短时间声线变化等条件下复核不稳定片段。不能把模型分数直接当作“正确率”，须在 dev 集校准复核阈值。
4. 快速接话只触发局部边界复核；测量现有“整段识别 + 两个半段重识别”的收益和成本。常规单人句子不付出同样代价，继续不保存人物身份。
5. 翻译只处理稳定文本；带版本号取消过时任务，缓存已确认台词。默认维持现有逐句显示，可选提供淡色原文初稿，避免字幕反复改写影响阅读。
6. 根据实际测得的实时率和队列积压选择配置；限制并发和显存占用，给游戏留出资源。模型驻留策略区分 CPU、低显存和高显存，避免两个大模型同时驻留造成卡顿。

候选模型的定位：Kotoba-Whisper v2 是日语蒸馏模型，有官方 CTranslate2/faster-whisper 权重，但保留了 large-v3 编码器，不能由“蒸馏”推断它一定比 base 更轻。模型卡上的速度比不代表这台 Windows 电脑的结果。先测本地已有模型，再决定是否下载、引入新权重。

### 游戏资源模式：把“听写”转化为“定位已经存在的台词”

离线预处理语音资产，建立与原文、可选官方中文译文相连的索引。运行时用短音频指纹/频谱特征搜索候选，再用更精细的时间对齐验证。应先试验鲁棒音频指纹，再比较声学 embedding + DTW；普通说话人 embedding 只能说明声线相似，不能证明是同一句台词。

流程：系统音频 → 语音检测 → 游戏索引候选检索 → 声学匹配及候选差距校验 → 命中后显示确切原文；低置信度、未知游戏、未收录句子则进入普通 ASR。短句“嗯/好/诶”、重复台词和多个相似候选必须允许延迟决定或拒识。剧情顺序只用于候选排序，不能替代声音证据。重叠发声先作为困难样本回退，不宣称解决了声音分离。

潜在收益来自减少完整解码次数、人名和专有名词能还原为脚本原文，以及可复用已有译文；收益取决于匹配覆盖率、误匹配率、索引成本和背景音乐干扰。**这是一项独立的资源辅助能力，不能把它的命中率包装成通用 ASR 准确率。**

后续管理界面：每个游戏展示索引大小、路径、资源版本、有效配对数、覆盖率；支持重建、停用、删除索引。索引、评测音频缓存、原始游戏目录分开管理；清理缓存只删除工具自己生成的目录。正式交付时沿用现有模型管理和历史管理的习惯。

## 2. 对 TenShiSouZou 的实机检查

只读检查对象为本地安装的 TenShiSouZou；其安装目录由使用者自行提供，以下示例以 `<game-directory>` 代指。

目录包含 data、voice、upgrade 系列等 XP3 包。只读检查已确认：XP3 使用连续索引，主要资源带保护标记，索引中的大部分名称无可识别扩展名。**保护标记并不能说明具体加密算法；目前没有可验证的“语音—日文原文”映射。** 不能将 voice.xp3 的索引条目数直接称作有效语音样本数。

工具不执行游戏脚本，不修改游戏或存档，不批量展开游戏资源。`probe_galgame.py` 输出元数据盘点，不是解包器。当前 `import_galgame.py` 只能处理已经可读的 KAG 脚本子集，不能直接处理这份受保护资源。

该游戏下一步有两条接入路径：

- 获得可读取的原始日文剧本和对应语音，保留 patch 覆盖次序、voice ID、角色、场景和来源行，再写游戏专用适配器。
- 若离线文件不能直接获取映射，设计专门的运行时导出适配器：在正常游戏流程中记录当前场景、语音资源标识、原文和播放事件，构建清单。该适配器尚未实现，也未启动或注入游戏进程。若只能录到系统混音，应作为“实际混音测试集”，不能当作干净语音资产；中文画面 OCR 也不能充当日语金标准。

当前未提取到该游戏的语料，未测得游戏识别准确率。先完成资源映射，才能自动跑真实语音评测。

## 3. 自动化评测流水线

### A. 资源配对与冻结

1. `probe_galgame.py`：只读盘点 XP3，检查连续索引、条目数和保护标记，报告不支持的格式。
2. `import_galgame.py`：读取解出的 `.ks`，根据明确的语音命令生成候选清单。默认只接受 `[voice storage="a.ogg"]` / `@voice storage="a.ogg"` 后一行以 `[p]` 或 `[l]` 结尾的台词。支持 `[name name="角色"]`。动态表达式、多行文本、未知标签、重名语音均不猜测配对。
3. 首次适配按角色、场景、长短句抽样听读核验，确认实际说出的日文/英文与脚本一致。原文语言、整句边界、旁白/人名排除、ruby 和自定义宏规则都属于适配器验收。导入器输出 `verified_original=false`；只有完成来源和配对核验后才改为 true。这个字段是来源声明，不是自动证据。
4. `galgame_bench.py prepare`：音频转成 16 kHz 单声道 float32 后做 SHA256；检查字段和重复 ID。按场景分组，再将相同音频或相同规范化台词关联的场景合并，确定性划分 train/dev/test（约 60/20/20）。相同音频却有不同原文会报错。
5. 去重关系可能将许多场景连成一个组，脚本会拒绝假造独立的测试集。此时补充更多独立场景/游戏，或仅用 `--smoke-only` 检查工具能否运行。不要为了凑出测试集而随机打散句子。

语音和字幕可有省略、口癖、喊叫与显示文字不一致；原始显示文本应保留在来源记录中，评测 reference 使用审定后的实际说出文本。不能用待测 ASR 输出自动生成自己的标准答案。

输入 CSV/JSONL 字段：

| 字段 | 用途 |
|---|---|
| id | 唯一样本 ID |
| audio | 音频路径；相对路径相对于输入清单 |
| text / language | 实际说出的原文；语言只允许 ja/en |
| game / scene | 游戏和场景分组 |
| source | 来源脚本及行号或引擎导出事件 |
| verified_original | 配对和原文已核验时为 true |
| order | 可选：场景中的台词顺序，须包括无语音台词占据的位置 |
| speaker | 可选：脚本角色，用于合成测试标签；不会发送给 ASR |

### B. 自动构造困难用例

`cases` 使用同场景、同语言、同划分且 order 连续的两句真实语音，生成间隔为 0/80/150/300 ms 的接话测试。保留源音频内部静音，报告的是“文件之间插入的间隔”，不能当作真实发声间隔。角色齐全时记录是否换人；缺失时保持未知。

支持静音负样本、用户提供的无演唱 BGM、固定全片 RMS 信噪比混音。保存 source_ids、源划分和 hash，派生样本不能重新随机划分。文件拼接点不是人工标注的声学边界。

第一版尚未包含重叠语音、变速、压缩编码扰动或自动游戏操作。后续加入时必须保留可追溯的随机种子、父样本和参考标签，区分人工合成混音与真实 WASAPI 录音。带人声的音乐不能标成“应当无文字”。

### C. 运行与对比

`run` 通过正式 `asr_worker.py` 的 JSON Lines 接口逐条识别。只发音频、语言及接话开关，绝不发送原文、文件名、voice ID、角色或场景。模型必须已在本地，环境启用离线模式；脚本不下载模型。

- 日语 CER 保留汉字/假名差异；英语额外报告 WER。统一 NFKC，忽略标点和符号，英语忽略大小写，不做可能掩盖错误的日语读音归一化。
- 静音/纯音乐单独统计错误出字数，无参考文字时 CER 为 null，不伪造 0% 错误率。
- 统计 p50/p95、总推理时间/总音频时长（RTF），按用例类型分组。模型启动和 worker 自带预热单独计时。
- 使用分句后的最终输出评分，记录原始响应、接话状态、警告和失败；失败不可静默丢弃。超时终止整个本次运行，防止错配后续响应。
- 对角色明确的合成接话用例统计换人检出的精确率/召回率；未知角色不计入。该指标只评价拼接用例上的换人检出，不代表真实声学边界精度或真实游戏里的说话人分离能力。
- 保存模型内容 hash、worker 源码 hash、依赖版本、参数、输入清单 hash。`compare` 拒绝未完成、有失败、清单不同或样本顺序不同的结果。
- 单次对比不是统计显著性结论。正式验收需固定硬件、控制游戏负载、同一配置重复测量至少 3 次，报告各场景差异及场景分组置信区间；第一版工具尚未实现置信区间。

这测的是**worker 识别延迟**，包括进程通信，不含音频文件解码、C# VAD、队列等待、翻译、悬浮窗绘制。下一阶段增加 C# PCM 流回放入口，按 20 ms 块送入真实预处理/切句管线，测原文稳定提交延迟、中文显示延迟、漏句率、队列丢弃数和字幕抖动次数。最后用实际游戏 + WASAPI 做端到端验收。

资源检索模式另设评测：索引内台词遭遇混音、短前缀和重复候选时的正确命中率、错误命中率、拒识率，以及完全未收录语音的回退表现。索引含目标原文在此任务中是允许的，但必须与普通 ASR 的隐藏答案评测分开。

## 4. Windows 命令

在项目根目录执行，使用现有 `.venv`。输出放 `artifacts/galgame-bench`（已被 Git 忽略），不要提交游戏语音、剧本、私有路径清单或评测原文。

只读资源检查（当前即可运行）：

```powershell
.\.venv\Scripts\python.exe scripts\probe_galgame.py '<game-directory>' --out artifacts\galgame-bench\tenshisouzou-probe.json
```

以下 `data\tenshisouzou` 是未来准备资源的示例目录，当前尚不存在可用语料。候选导入不等于配对已核验：

```powershell
.\.venv\Scripts\python.exe scripts\import_galgame.py --scripts data\tenshisouzou\scripts --audio data\tenshisouzou\voice --game TenShiSouZou --language ja --voice-tag voice --out artifacts\galgame-bench\import
# 核验候选清单和来源后，将已确认的样本写入 verified.jsonl。
.\.venv\Scripts\python.exe scripts\galgame_bench.py prepare --input data\tenshisouzou\verified.jsonl --out artifacts\galgame-bench\frozen.jsonl
.\.venv\Scripts\python.exe scripts\galgame_bench.py cases --manifest artifacts\galgame-bench\frozen.jsonl --out artifacts\galgame-bench\cases
.\.venv\Scripts\python.exe scripts\galgame_bench.py run --manifest artifacts\galgame-bench\frozen.jsonl --model models\asr\whisper-base --split test --device cpu --beam 2 --out artifacts\galgame-bench\base-cpu
.\.venv\Scripts\python.exe scripts\galgame_bench.py run --manifest artifacts\galgame-bench\frozen.jsonl --model models\asr\whisper-small --split test --device cpu --beam 2 --out artifacts\galgame-bench\small-cpu
.\.venv\Scripts\python.exe scripts\galgame_bench.py compare --baseline artifacts\galgame-bench\base-cpu --candidate artifacts\galgame-bench\small-cpu --out artifacts\galgame-bench\comparison.json
```

对快速接话用例，将 manifest 改成 `cases\manifest.jsonl`；以完全相同模型和参数分别关闭/开启 `--turns --turn-model models\speakers\wespeaker-resnet34\speaker.onnx`。GPU 运行使用 `--device cuda`，并按现有安装目录显式传 `--dll-dir`。不要把 CPU/GPU 差异归因于模型或声线增强算法。评测输出目录需为新目录，避免覆盖已有结果。

测试工具自身：

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s scripts -p test_galgame_bench.py -v
```

## 5. 开发次序与拟定验收门槛

1. **数据接入**：完成 TenShiSouZou 的可验证映射，先选择若干正常剧情场景，逐步扩大到不同角色、语气和长度；留出独立场景，之后增加第二款游戏测试泛化能力。
2. **基线**：至少 base/small/turbo × 接话开/关；CPU/GPU 分开比较；先用 dev 选参数，最终 test 冻结后一次验收，不反复根据 test 调参。
3. **普通识别升级**：先修切段/重复计算和选择性复核，再考虑新的日语模型。建议目标：相对 CER 降低 20% 或同等 CER 下 p95 延迟降低 30%，另一个指标不显著退化；这些是拟定门槛，不是已实现的成绩。中文翻译准确度须独立验收。
4. **资源定位原型**：先测 0.5/1/2 秒前缀、BGM、重复短句、未收录语音和剧情跳转。优先压低错误命中，允许更多回退。只有覆盖率和延迟收益足够大，才接入正式 UI。
5. **回归与交付**：普通单人句、同人连续两句、不同人快速接话、静音/BGM均不得出现明显退化。端到端报告至少包括游戏帧率/CPU/内存/显存影响；当前工具没有自动采集这些指标。

## 6. 参考依据

- [Kotoba-Whisper v2 官方模型卡](https://huggingface.co/kotoba-tech/kotoba-whisper-v2.0)：日语蒸馏模型架构、公开数据集结果；不替代游戏域测试。
- [官方 faster-whisper 转换权重](https://huggingface.co/kotoba-tech/kotoba-whisper-v2.0-faster)：可沿用当前 CTranslate2 推理栈。
- [faster-whisper 项目](https://github.com/SYSTRAN/faster-whisper)：本地推理、量化和批处理。离线批处理吞吐提升不等于实时首字延迟下降。
- [吉里吉里 XP3 实现](https://github.com/krkrz/krkrz/blob/master/base/XP3Archive.cpp)：索引、连续块及文件元数据结构；游戏定制保护不属于通用配对能力。

实现状态：已提供只读盘点、保守 KAG 候选导入、清单冻结、派生用例、现有 worker 运行及成对对比；资源检索引擎、TenShiSouZou 专用资源映射、C# 流回放和管理界面均属于后续开发内容。

## 7. 本次验证记录

- 工具自动测试：13 项通过，覆盖编辑距离、空参考、场景去重、防止音频被替换、含糊配对拒绝、进程超时、分句输出评分、答案不进入请求、换人检出指标及 XP3 连续/循环索引。
- 游戏资源盘点：11 个 XP3 包索引读取成功，已确认配对样本数仍为 0。报告：`artifacts/galgame-bench/tenshisouzou-probe.json`。
- 真实 worker 冒烟验证：本地 Whisper base、CPU、beam 2，对自生成的 3 秒静音完成离线推理，返回空文本。报告位于 `artifacts/galgame-bench/tool-smoke`。这不是游戏准确率或有语音时的性能测试。
- 正式 WPF/ASR 逻辑未修改；新增文件尚未提交和推送 GitHub。实际语音、剧本和本地测试产物不进入 Git。
