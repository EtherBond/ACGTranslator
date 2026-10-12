本地 ASR 与翻译依赖

在项目根目录准备 Python 环境和模型（需要联网）：

  python -m venv .venv
  .\.venv\Scripts\python.exe -m pip install -r requirements-asr.txt
  .\.venv\Scripts\python.exe scripts\prepare_models.py

默认模型目录是项目根目录下的 models\asr\whisper-base，也可以在设置窗口的“高级”中改为其他 faster-whisper 格式的模型目录。

依赖或模型缺失时应用仍可启动，悬浮窗会显示“模型未就绪”和具体原因。
