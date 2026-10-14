"""Local ASR JSON-lines worker. Model files must already exist locally."""
import argparse, base64, sys, time

from worker_protocol import add_dll_directories, emit, requests

# Only these languages have translation support.
SUPPORTED_LANGUAGES = ("ja", "en")
SAMPLE_RATE = 16000
AUTO_LANGUAGE_MIN_PROBABILITY = 0.35


def transcribe(model, audio, language, options, detector=None):
    forced = language if language in SUPPORTED_LANGUAGES else None
    decode_options = dict(options, word_timestamps=True) if detector is not None else options
    segments, info = model.transcribe(audio, language=forced, **decode_options)
    if forced is None and info.language not in SUPPORTED_LANGUAGES:
        # Only retry when the model has meaningful evidence for one of the two
        # supported languages. For music/noise, returning an empty result is
        # safer than forcing Japanese and displaying hallucinated subtitles.
        probabilities = dict(info.all_language_probs or [])
        candidate = max(SUPPORTED_LANGUAGES, key=lambda code: probabilities.get(code, 0.0))
        if probabilities.get(candidate, 0.0) >= AUTO_LANGUAGE_MIN_PROBABILITY:
            forced = candidate
            segments, info = model.transcribe(audio, language=forced, **decode_options)
        else:
            return {"language": info.language, "text": "", "start": 0.0, "end": len(audio) / SAMPLE_RATE}
    parts, start, end, words = [], None, None, []
    for segment in segments:
        if not segment.text.strip():
            continue
        parts.append(segment.text)
        for word in getattr(segment, 'words', None) or []:
            words.append({'start': word.start, 'end': word.end, 'text': word.word})
        start = segment.start if start is None else start
        end = segment.end
    result = {
        "language": forced or info.language,
        "text": "".join(parts).strip(),
        "start": max(0., min(start or 0., len(audio) / SAMPLE_RATE)),
        "end": max(0., min(end if end is not None else len(audio) / SAMPLE_RATE, len(audio) / SAMPLE_RATE)),
    }
    if detector is not None and result["text"]:
        try:
            boundary = detector.detect(audio, words)
            if boundary is not None:
                cut = round(boundary * SAMPLE_RATE)
                split = []
                # At most two retries. Keep the original if either side fails.
                for offset, chunk in ((0., audio[:cut]), (cut / SAMPLE_RATE, audio[cut:])):
                    piece = transcribe(model, chunk, result["language"], options)
                    if not piece["text"]:
                        raise ValueError("分段复核没有得到完整文字")
                    piece["start"] = max(0., min(len(chunk)/SAMPLE_RATE, piece["start"])) + offset
                    piece["end"] = max(piece["start"], min(len(chunk)/SAMPLE_RATE, piece["end"]) + offset)
                    split.append(piece)
                result["segments"] = split
                result["turn_status"] = "split"
            else:
                result["turn_status"] = "unchanged"
        except TimeoutError:
            result["turn_status"] = "budget"
        except Exception as exc:
            result["turn_status"] = "fallback"
            result["warning"] = f"接话增强已回退到普通识别：{exc}"
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--dll-dir", action="append", default=[])
    parser.add_argument("--beam-size", type=int, default=0, help="0 picks 5 on CUDA and 2 on CPU")
    parser.add_argument("--turn-model", default="", help="Optional local WeSpeaker ONNX model")
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
        "temperature": 0.0,
        "compression_ratio_threshold": 2.4,
        "log_prob_threshold": -1.0,
        "no_speech_threshold": 0.55,
        "vad_parameters": {
            "min_speech_duration_ms": 250,
            "min_silence_duration_ms": 320,
            "speech_pad_ms": 120,
        },
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

    detector, turn_warning = None, ""
    if args.turn_model:
        try:
            from turn_detector import SpeakerEmbedding, TurnDetector
            embed = SpeakerEmbedding(args.turn_model)
            embed(np.zeros(SAMPLE_RATE, dtype=np.float32))
            detector = TurnDetector(embed)
        except Exception as exc:
            turn_warning = f"接话增强不可用，继续普通识别：{exc}"
    emit({"ready": True})
    cooldown = 0

    for request in requests():
        try:
            if request.get("type") != "transcribe":
                emit({"error": "未知请求类型"})
                continue
            if request.get("sample_rate", SAMPLE_RATE) != SAMPLE_RATE:
                emit({"error": "只支持 16 kHz 单声道音频"})
                continue
            audio = np.frombuffer(base64.b64decode(request["audio_base64"]), dtype=np.float32)

            started = time.monotonic()
            use_turns = detector is not None and request.get("turn_enhancement", True) and cooldown == 0
            try:
                result = transcribe(model, audio, request.get("language", "auto"), options,
                                    detector if use_turns else None)
            except Exception as exc:
                if not use_turns:
                    raise
                result = transcribe(model, audio, request.get("language", "auto"), options)
                result["warning"] = f"接话增强已停用，继续普通识别：{exc}"
                result["turn_status"] = "fallback"
                detector = None
            if cooldown:
                cooldown -= 1
                result["turn_status"] = "cooldown"
            elif use_turns and (result.get("turn_status") == "budget" or
                               time.monotonic() - started > max(1., len(audio) / SAMPLE_RATE)):
                cooldown = 3
            if turn_warning:
                result["warning"] = turn_warning
                turn_warning = ""
            emit(result)

        except Exception as exc:
            emit({"error": str(exc)})
    return 0


if __name__ == "__main__": sys.exit(main())
