"""Local ASR JSON-lines worker. Model files must already exist locally."""
import argparse, base64, sys

from worker_protocol import add_dll_directories, emit, requests

# Only these languages have translation support.
SUPPORTED_LANGUAGES = ("ja", "en")
SAMPLE_RATE = 16000


def transcribe(model, audio, language, options):
    forced = language if language in SUPPORTED_LANGUAGES else None
    segments, info = model.transcribe(audio, language=forced, **options)
    if forced is None and info.language not in SUPPORTED_LANGUAGES:
        # Re-decode with the more likely supported language instead of producing untranslatable text.
        probabilities = dict(info.all_language_probs or [])
        forced = max(SUPPORTED_LANGUAGES, key=lambda code: probabilities.get(code, 0.0))
        segments, info = model.transcribe(audio, language=forced, **options)
    parts, start, end = [], None, None
    for segment in segments:
        if not segment.text.strip():
            continue
        parts.append(segment.text)
        start = segment.start if start is None else start
        end = segment.end
    return {
        "language": forced or info.language,
        "text": "".join(parts).strip(),
        "start": start or 0.0,
        "end": end if end is not None else len(audio) / SAMPLE_RATE,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--dll-dir", action="append", default=[])
    parser.add_argument("--beam-size", type=int, default=0, help="0 picks 5 on CUDA and 2 on CPU")
    args = parser.parse_args()
    add_dll_directories(args.dll_dir)
    try:
        import numpy as np
        from faster_whisper import WhisperModel
    except Exception as exc:
        emit({"error": f"无法加载本地 ASR 依赖，请安装 faster-whisper 和 numpy：{exc}"})
        return 2
    compute_type = "int8" if args.device == "cpu" else "float16"
    options = {
        # Silero VAD (bundled with faster-whisper) drops non-speech, which suppresses hallucinated text on music or noise.
        "vad_filter": True,
        "condition_on_previous_text": False,
        "beam_size": args.beam_size or (5 if args.device == "cuda" else 2),
    }
    try:
        model = WhisperModel(args.model, device=args.device, compute_type=compute_type)
        # CUDA libraries load lazily; decode once so a missing DLL is reported now rather than on the first
        # subtitle. VAD is off here because it would discard the silent clip before the encoder runs.
        warm_up = np.zeros(SAMPLE_RATE, dtype=np.float32)
        list(model.transcribe(warm_up, language="ja", beam_size=1, vad_filter=False, condition_on_previous_text=False)[0])
    except Exception as exc:
        hint = "（缺少 CUDA 运行库，请在模型管理中下载“CUDA 识别加速”，或改用 CPU）" if "dll" in str(exc).lower() else ""
        emit({"error": f"无法加载 Whisper 模型 {args.model}：{exc}{hint}"})
        return 3
    emit({"ready": True})
    for request in requests():
        try:
            if request.get("type") != "transcribe":
                emit({"error": "未知请求类型"})
                continue
            if request.get("sample_rate", SAMPLE_RATE) != SAMPLE_RATE:
                emit({"error": "只支持 16 kHz 单声道音频"})
                continue
            audio = np.frombuffer(base64.b64decode(request["audio_base64"]), dtype=np.float32)
            emit(transcribe(model, audio, request.get("language", "auto"), options))
        except Exception as exc:
            emit({"error": str(exc)})
    return 0


if __name__ == "__main__": sys.exit(main())
