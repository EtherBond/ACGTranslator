"""Local ASR JSON-lines worker. Model files must already exist locally."""
import argparse, base64, math, sys, time

from worker_protocol import add_dll_directories, emit, requests

# Only these languages have translation support.
SUPPORTED_LANGUAGES = ("ja", "en")
SAMPLE_RATE = 16000
AUTO_LANGUAGE_MIN_PROBABILITY = 0.35


def retry_reasons(segments, info, text, duration):
    """Heuristics, not calibrated probabilities. Exposed in diagnostics for audit."""
    reasons = []
    if not text.strip() and getattr(info, 'duration_after_vad', 0) > .5:
        reasons.append('empty_on_speech')
    if any(getattr(s, 'avg_logprob', 0) < -.65 for s in segments):
        reasons.append('low_logprob')
    if any(getattr(s, 'compression_ratio', 0) > 2.1 for s in segments):
        reasons.append('repetition')
    if text and len(text.replace(' ', '')) / max(duration, .1) > 22:
        reasons.append('text_density')
    return reasons


def aligned_turn_segments(result, words, boundary, duration):
    """Partition a verified turn without changing the full-context transcript.

    Alignment must cover the exact decoded text, be ordered, and not cut a word.
    Unreliable alignment falls back to the existing two-sided verification.
    """
    if not math.isfinite(boundary) or not 0 < boundary < duration or not words:
        return None
    if ''.join(w['text'] for w in words).strip() != result['text']:
        return None
    left, right = [], []
    previous_end = 0.
    for word in words:
        start, end = word['start'], word['end']
        if not (math.isfinite(start) and math.isfinite(end)
                and 0 <= start <= end <= duration and start >= previous_end):
            return None
        previous_end = end
        if end <= boundary:
            left.append(word)
        elif start >= boundary:
            right.append(word)
        else:
            return None
    if not left or not right:
        return None
    pieces = []
    for side in (left, right):
        text = ''.join(w['text'] for w in side).strip()
        if not text:
            return None
        pieces.append(dict(language=result['language'], text=text,
                           start=side[0]['start'], end=side[-1]['end']))
    return pieces


def transcribe(model, audio, language, options, detector=None, adaptive=False):
    forced = language if language in SUPPORTED_LANGUAGES else None
    # The detector needs >=0.8 s on each side; shorter audio cannot contain
    # an eligible boundary and does not need expensive word alignment.
    detector = detector if len(audio) / SAMPLE_RATE >= 1.6 else None
    decode_options = dict(options)
    # Optional game vocabulary applies only to explicitly selected Japanese.
    # Auto language detection and English must not inherit Japanese name hints.
    if forced != 'ja':
        decode_options.pop('hotwords', None)
    if detector is not None:
        decode_options['word_timestamps'] = True
    if adaptive:
        decode_options['beam_size'] = min(options.get('beam_size', 5), 2)
    decode_started = time.monotonic()
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
    segments = list(segments)
    reasons = retry_reasons(segments, info, ''.join(s.text for s in segments), len(audio) / SAMPLE_RATE) if adaptive else []
    retried = bool(reasons and decode_options.get('beam_size', 5) < 5)
    if retried:
        decode_options['beam_size'] = 5
        # Fixed language across retries; no reference text or previous hypothesis
        # is supplied as a prompt. At most one full-clip quality retry.
        retry_segments, info = model.transcribe(audio, language=forced or info.language, **decode_options)
        segments = list(retry_segments)
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
        "decode": {"beam_size": decode_options.get('beam_size'), "retried": retried,
                   "retry_reasons": reasons, "word_timestamps": detector is not None,
                   "elapsed_s": time.monotonic() - decode_started,
                   "avg_logprob": [getattr(s, 'avg_logprob', None) for s in segments],
                   "no_speech_prob": [getattr(s, 'no_speech_prob', None) for s in segments]},
    }
    if detector is not None and result["text"]:
        turn_started = time.monotonic()
        turn = {"extra_decodes": 0}
        result["turn"] = turn
        try:
            boundary = detector.detect(audio, words)
            turn["detection_s"] = time.monotonic() - turn_started
            turn.update(getattr(detector, "last_stats", {}))
            if boundary is not None:
                duration = len(audio) / SAMPLE_RATE
                if not math.isfinite(boundary) or not 0 < boundary < duration:
                    raise ValueError("接话边界超出音频范围")
                turn["boundary_s"] = boundary
                split = aligned_turn_segments(result, words, boundary, duration)
                if split is not None:
                    turn["strategy"] = "aligned"
                else:
                    turn["strategy"] = "redecode"
                    cut = round(boundary * SAMPLE_RATE)
                    split = []
                    # Only retry when alignment cannot preserve the full text.
                    # Keep the original if either side fails.
                    for offset, chunk in ((0., audio[:cut]), (cut / SAMPLE_RATE, audio[cut:])):
                        turn["extra_decodes"] += 1
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
        finally:
            turn["elapsed_s"] = time.monotonic() - turn_started
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--dll-dir", action="append", default=[])
    parser.add_argument("--beam-size", type=int, default=0, help="0 picks 5 on CUDA and 2 on CPU")
    parser.add_argument("--turn-model", default="", help="Optional local WeSpeaker ONNX model")
    parser.add_argument("--adaptive", action="store_true", help="Experimental beam<=2 with one quality retry")
    parser.add_argument("--presegmented", action="store_true", help="Input already gated by streaming Silero; do not crop twice")
    parser.add_argument("--vad-presence-only", action="store_true", help="Reject non-speech with VAD but retain full audio context")
    parser.add_argument("--compute-type", choices=['default', 'float16', 'int8_float16', 'int8'], default='default')
    parser.add_argument("--hotwords", default='', help="Optional Japanese names/terms, max 256 characters; fixed ja only")
    args = parser.parse_args()
    add_dll_directories(args.dll_dir)
    try:
        import numpy as np
        from faster_whisper import WhisperModel
    except Exception as exc:
        emit({"error": f"无法加载本地 ASR 依赖，请安装 faster-whisper 和 numpy：{exc}"})
        return 2
    compute_type = args.compute_type if args.compute_type != 'default' else ("int8" if args.device == "cpu" else "float16")
    options = {
        # Silero VAD (bundled with faster-whisper) drops non-speech, which suppresses hallucinated text on music or noise.
        "vad_filter": not (args.presegmented or args.vad_presence_only),
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
    if args.hotwords.strip():
        options['hotwords'] = args.hotwords.strip()[:256]
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
            if args.vad_presence_only:
                from faster_whisper.vad import get_speech_timestamps, VadOptions
                if not get_speech_timestamps(audio, VadOptions(**options['vad_parameters'])):
                    emit({'language': request.get('language','auto'), 'text':'', 'start':0.,
                          'end':len(audio)/SAMPLE_RATE, 'elapsed_s':time.monotonic()-started,
                          'decode':{'skipped':'no_speech'}})
                    continue
            use_turns = detector is not None and request.get("turn_enhancement", True) and cooldown == 0
            try:
                result = transcribe(model, audio, request.get("language", "auto"), options,
                                    detector if use_turns else None, args.adaptive)
            except Exception as exc:
                if not use_turns:
                    raise
                result = transcribe(model, audio, request.get("language", "auto"), options, adaptive=args.adaptive)
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
            result['elapsed_s'] = time.monotonic() - started
            emit(result)

        except Exception as exc:
            emit({"error": str(exc)})
    return 0


if __name__ == "__main__": sys.exit(main())
