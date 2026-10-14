# 第三方组件

本包内置 Microsoft .NET 8 Windows Desktop Runtime、NAudio 2.2.1。保留随运行时分发的 LICENSE 和 ThirdPartyNotices 文件。

初始化脚本从 Python 包索引安装 requirements-asr.txt 中的依赖及其传递依赖，包括 faster-whisper、CTranslate2、Argos Translate、SentencePiece 和 Hugging Face Hub。各依赖的许可信息随其安装包保存于 .venv/Lib/site-packages。

模型权重、Python 安装器和 GPU 运行组件未包含在发布包中。通过初始化脚本或模型管理下载的组件受各自许可约束，详情请查看来源仓库和模型卡。本文件不为项目选择或授予新的开源许可。

## v1.0.1 接话增强
- ONNX Runtime：MIT，https://github.com/microsoft/onnxruntime
- kaldi-native-fbank：Apache-2.0，https://github.com/csukuangfj/kaldi-native-fbank
- WeSpeaker ResNet34-LM 官方模型（可选下载，不在发布包内）：CC-BY-4.0，
  https://huggingface.co/Wespeaker/wespeaker-voxceleb-resnet34-LM
  作者/来源：WeSpeaker 项目；https://github.com/wenet-e2e/wespeaker
  权重未经修改，仅在本地重命名为 speaker.onnx；授权条款 https://creativecommons.org/licenses/by/4.0/
  固定修订 f0c48c298fd835726c27956a5d617bad7115627e。
